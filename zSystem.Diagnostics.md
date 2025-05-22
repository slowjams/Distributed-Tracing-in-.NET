## Source Code


```C#
//-------------------V
public class Activity : IDisposable  // In .NET world, a span is represented by an Activity, System.Span class is not related to distributed tracing.
{
    private string? _traceState;
    private State _state;
    private int _currentChildId;  // A unique number for all children of this activity.

    // State associated with ID.
    private string? _id;
    private string? _rootId;
    // State associated with ParentId.
    private string? _parentId;

    // W3C formats
    private string? _parentSpanId;
    private string? _traceId;
    private string? _spanId;

    private byte _w3CIdFlags;
    private byte _parentTraceFlags;

    private TagsLinkedList? _tags;
    private BaggageLinkedList? _baggage;
    private DiagLinkedList<ActivityLink>? _links;
    private DiagLinkedList<ActivityEvent>? _events;
    private Dictionary<string, object>? _customProperties;
    private string? _displayName;
    private ActivityStatusCode _statusCode;
    private string? _statusDescription;
    private Activity? _previousActiveActivity;

    public ActivityStatusCode Status => _statusCode;
   
    private static readonly IEnumerable<KeyValuePair<string, string?>> s_emptyBaggageTags = new KeyValuePair<string, string?>[0];
    private static readonly IEnumerable<KeyValuePair<string, object?>> s_emptyTagObjects = new KeyValuePair<string, object?>[0];
    private static readonly IEnumerable<ActivityLink> s_emptyLinks = new DiagLinkedList<ActivityLink>();
    private static readonly IEnumerable<ActivityEvent> s_emptyEvents = new DiagLinkedList<ActivityEvent>();
    private static readonly ActivitySource s_defaultSource = new ActivitySource(string.Empty);
    private static readonly AsyncLocal<Activity?> s_current = new AsyncLocal<Activity?>();  // <---------------------------
    public static event EventHandler<ActivityChangedEventArgs>? CurrentChanged;  // event occur when the Activity.Current value changes.
 
    private static readonly string s_uniqSuffix = $"-{GetRandomNumber():x}.";

    // Int gives enough randomization and keeps hex-encoded s_currentRootId 8 chars long for most applications
    private static long s_currentRootId = (uint)GetRandomNumber();
    
    public Activity(string operationName)
    {
        Source = s_defaultSource;
        // ...
        OperationName = operationName ?? string.Empty;
    }

    public static Activity? Current   // <-------------------------------
    {
        get { return s_current.Value; }
        set
        {
            if (ValidateSetCurrent(value))
            {
                SetCurrent(value);
            }
        }
    }
    public static ActivityIdFormat DefaultIdFormat { get; set; }
    public static bool ForceDefaultIdFormat { get; set; }
    public IEnumerable<KeyValuePair<string, object?>> TagObjects { get; }
    public ActivityTraceId TraceId { get; }   // <------------------------------
    public string? Id                       // <------------------------------
    {
        get
        {
        // if we represented it as a traceId-spanId, convert it to a string.
        // We can do this concatenation with a stackalloced Span<char> if we actually used Id a lot.
        if (_id == null && _spanId != null)
        {
            // Convert flags to binary.
            Span<char> flagsChars = stackalloc char[2];
            HexConverter.ToCharsBuffer((byte)((~ActivityTraceFlagsIsSet) & _w3CIdFlags), flagsChars, 0, HexConverter.Casing.Lower);
            string id =  "00-" + _traceId + "-" + _spanId + "-" + flagsChars.ToString();
            Interlocked.CompareExchange(ref _id, id, null);
        }
        
        return _id;
        }
    }

    private static void SetCurrent(Activity? activity)
    {
        EventHandler<ActivityChangedEventArgs>? handler = CurrentChanged;
        if (handler is null)
        {
            s_current.Value = activity;  // <-------------------------pact4. parentActivity is set to Activity.Current
        }
        else
        {
            Activity? previous = s_current.Value;
            s_current.Value = activity;
            handler.Invoke(null, new ActivityChangedEventArgs(previous, activity));
        }
    }

    public void Stop()
    {
        if (_id == null && _spanId == null)
        {
            NotifyError(new InvalidOperationException(SR.ActivityNotStarted));
            return;
        }

        if (!IsStopped)
        {
            IsStopped = true;

            if (Duration == TimeSpan.Zero)
            {
                SetEndTime(GetUtcNow());
            }

            Source.NotifyActivityStop(this);
            SetCurrent(_previousActiveActivity);
        }
    }
    public Activity Start()   // <-------------------------pact3.0, cact3.0, dlr3.2.4.5
    {
        if (_id != null || _spanId != null)
        {
            NotifyError(new InvalidOperationException(SR.ActivityStartAlreadyStarted));
        }
        else
        {
            _previousActiveActivity = Current;   // <-------------------------pact3.1, Current is null for parentActivity
                                                 // <-------------------------cact3.1, Current is not null for childActivity
            if (_parentId == null && _parentSpanId is null)
            {
                if (_previousActiveActivity != null)
                {
                    Parent = _previousActiveActivity;   // <-------------------------cact3.2
                }
            }

            if (StartTimeUtc == default)
                StartTimeUtc = GetUtcNow();

            if (IdFormat == ActivityIdFormat.Unknown)
            {
                IdFormat =
                    ForceDefaultIdFormat ? DefaultIdFormat :
                    Parent != null ? Parent.IdFormat :
                    _parentSpanId != null ? ActivityIdFormat.W3C :
                    _parentId == null ? DefaultIdFormat :
                    IsW3CId(_parentId) ? ActivityIdFormat.W3C :
                    ActivityIdFormat.Hierarchical;
            }

            if (IdFormat == ActivityIdFormat.W3C)
                GenerateW3CId();   // <-------------------------pact3.2, cact3.3
            else
                _id = GenerateHierarchicalId();

            SetCurrent(this);     // <-------------------------pact3.3., cact3.4.

            Source.NotifyActivityStart(this);
        }
        return this;
    }

    internal static Activity Create(            // <-------------------------pact2.0, dlr3.2.4.2
        ActivitySource source, 
        string name, 
        ActivityKind kind, 
        string? parentId, 
        ActivityContext parentContext,
        IEnumerable<KeyValuePair<string, object?>>? tags,
        IEnumerable<ActivityLink>? links,
        DateTimeOffset startTime,
        ActivityTagsCollection? samplerTags, 
        ActivitySamplingResult request, 
        bool startIt,   // <----------------------------------
        ActivityIdFormat idFormat,
        string? traceState
    )
    {
        Activity activity = new Activity(name);    // <-------------------------pact2.1
 
        activity.Source = source;
        activity.Kind = kind;
        activity.IdFormat = idFormat;
        activity._traceState = traceState;

        if (links != null)
        {
            using (IEnumerator<ActivityLink> enumerator = links.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    activity._links = new DiagLinkedList<ActivityLink>(enumerator);
                }
            }
        }

        if (tags != null)
        {
            using (IEnumerator<KeyValuePair<string, object?>> enumerator = tags.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    activity._tags = new TagsLinkedList(enumerator);
                }
            }
        }

        if (samplerTags != null)
        {
            if (activity._tags == null)
            {
                activity._tags = new TagsLinkedList(samplerTags!);
            }
            else
            {
                activity._tags.Add(samplerTags!);
            }
        }

        if (parentId != null)
        {
            activity._parentId = parentId;
        }
        else if (parentContext != default)
        {
            activity._traceId = parentContext.TraceId.ToString();  // <-------------------------dlr3.2.4.3

            if (parentContext.SpanId != default)
            {
                activity._parentSpanId = parentContext.SpanId.ToString();
            }

            // note: don't inherit Recorded from parent as it is set below based on sampling decision
            activity.ActivityTraceFlags = parentContext.TraceFlags & ~ActivityTraceFlags.Recorded;
            activity._parentTraceFlags = (byte)parentContext.TraceFlags;
            activity.HasRemoteParent = parentContext.IsRemote;
        }

        activity.IsAllDataRequested = request == ActivitySamplingResult.AllData || request == ActivitySamplingResult.AllDataAndRecorded;
 
        if (request == ActivitySamplingResult.AllDataAndRecorded)
        {
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        }

        if (startTime != default)
        {
            activity.StartTimeUtc = startTime.UtcDateTime;
        }

        if (startIt)
        {
            activity.Start();  // <-------------------------pact2.2., dlr3.2.4.4
        }

        return activity;
    }

    private void GenerateW3CId()
    {
        if (_traceId is null)
        {
            if (!TrySetTraceIdFromParent())
            {
                Func<ActivityTraceId>? traceIdGenerator = TraceIdGenerator;
                ActivityTraceId id = traceIdGenerator == null ? ActivityTraceId.CreateRandom() : traceIdGenerator();
                _traceId = id.ToHexString();
            }
        }

        if (!W3CIdFlagsSet)
        {
            TrySetTraceFlagsFromParent();
        }

        // Create a new SpanID.

        _spanId = ActivitySpanId.CreateRandom().ToHexString();
    }

    private bool TrySetTraceIdFromParent()  // <---------------------------this is how childActivity uses parents' traceId
    {
        if (Parent != null && Parent.IdFormat == ActivityIdFormat.W3C)
        {
            _traceId = Parent.TraceId.ToHexString();
        }
        else if (_parentId != null && IsW3CId(_parentId))
        {
            try
            {
                _traceId = ActivityTraceId.CreateFromString(_parentId.AsSpan(3, 32)).ToHexString();
            }
            catch
            {
            }
        }

        return _traceId != null;
    }

    internal static bool TryConvertIdToContext(string traceParent, string? traceState, bool isRemote, out ActivityContext context) // <---------------dlr3.2.2
    {
        context = default;
        if (!IsW3CId(traceParent))
        {
            return false;
        }

        ReadOnlySpan<char> traceIdSpan = traceParent.AsSpan(3, 32);
        ReadOnlySpan<char> spanIdSpan = traceParent.AsSpan(36, 16);

        if (!ActivityTraceId.IsLowerCaseHexAndNotAllZeros(traceIdSpan) || !ActivityTraceId.IsLowerCaseHexAndNotAllZeros(spanIdSpan) ||
            !HexConverter.IsHexLowerChar(traceParent[53]) || !HexConverter.IsHexLowerChar(traceParent[54]))
        {
            return false;
        }

        context = new ActivityContext(                                  // <---------------dlr3.2.3
                        new ActivityTraceId(traceIdSpan.ToString()),
                        new ActivitySpanId(spanIdSpan.ToString()),
                        (ActivityTraceFlags)ActivityTraceId.HexByteFromChars(traceParent[53], traceParent[54]),
                        traceState,
                        isRemote);

        return true;
    }

    public string DisplayName 
    {
        get => _displayName ?? OperationName;
        set => _displayName = value ?? throw new ArgumentNullException(nameof(value));
    }
    public string OperationName { get; }  // <-------no setter for OperationName
    public ActivitySource Source { get; }

    public bool IsAllDataRequested { get; set; }
    public ActivityIdFormat IdFormat { get; }
    public ActivityKind Kind { get; }
    public IEnumerable<ActivityEvent> Events { get; }
    public IEnumerable<ActivityLink> Links { get; }
    public ActivitySpanId ParentSpanId { get; }       // <------------------------------
    public bool Recorded { get; }
    public string? RootId { get; }
    public ActivitySpanId SpanId { get; }            // <------------------------------
    public DateTime StartTimeUtc { get; }
    public IEnumerable<KeyValuePair<string, string?>> Tags { get; }
    public Activity? Parent { get; }
    public string? ParentId { get; }
    public TimeSpan Duration { get; }
    public IEnumerable<KeyValuePair<string, string?>> Baggage { get; }
    public string? TraceStateString { get; set; }
    public ActivityContext Context { get; }
    public ActivityTraceFlags ActivityTraceFlags { get; set; }

    public Activity AddBaggage(string key, string? value);
    public Activity AddEvent(ActivityEvent e);
    public Activity AddTag(string key, string? value);
    public Activity AddTag(string key, object? value);
    public string? GetBaggageItem(string key);
    public object? GetCustomProperty(string propertyName);
    public void SetCustomProperty(string propertyName, object? propertyValue);
    public Activity SetEndTime(DateTime endTimeUtc);
    public Activity SetIdFormat(ActivityIdFormat format);
    public Activity SetParentId(string parentId);
    public Activity SetParentId(ActivityTraceId traceId, ActivitySpanId spanId, ActivityTraceFlags activityTraceFlags = ActivityTraceFlags.None);
    public Activity SetStartTime(DateTime startTimeUtc);
    public Activity SetTag(string key, object? value);
}
//-------------------Ʌ 

//------------------------------------V
public readonly struct ActivitySpanId : IEquatable<ActivitySpanId>
{
	private readonly string? _hexString;

    internal ActivitySpanId(string? hexString) => _hexString = hexString;
    
    public static ActivitySpanId CreateFromBytes(ReadOnlySpan<byte> idData);
	public static ActivitySpanId CreateFromString(ReadOnlySpan<char> idData);
	public static ActivitySpanId CreateFromUtf8String(ReadOnlySpan<byte> idData);
	public static ActivitySpanId CreateRandom();
	public void CopyTo(Span<byte> destination);
	public bool Equals(ActivitySpanId traceId);

	public static bool operator ==(ActivitySpanId traceId1, ActivitySpanId traceId2);
	public static bool operator !=(ActivitySpanId traceId1, ActivitySpanId traceId2);
}
//------------------------------------Ʌ
```

```C#
//--------------------------------V
public sealed class ActivitySource : IDisposable
{
    private static readonly SynchronizedList<ActivitySource> s_activeSources = new SynchronizedList<ActivitySource>();
    private static readonly SynchronizedList<ActivityListener> s_allListeners = new SynchronizedList<ActivityListener>();
    private SynchronizedList<ActivityListener>? _listeners;

    public ActivitySource(string name) : this(name, version: "", tags: null, telemetrySchemaUrl: null) {}

    public ActivitySource(string name, string? version = "") : this(name, version, tags: null, telemetrySchemaUrl: null) {}

    public ActivitySource(string name, string? version = "", IEnumerable<KeyValuePair<string, object?>>? tags = default) : this(name, version, tags, telemetrySchemaUrl: null) {}

    public ActivitySource(ActivitySourceOptions options) : this((options ?? throw new ArgumentNullException(nameof(options))).Name, options.Version, options.Tags, options.TelemetrySchemaUrl) {}

    private ActivitySource(string name, string? version, IEnumerable<KeyValuePair<string, object?>>? tags, string? telemetrySchemaUrl)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Version = version;
        TelemetrySchemaUrl = telemetrySchemaUrl;

        // Sorting the tags to make sure the tags are always in the same order.
        // Sorting can help in comparing the tags used for any scenario.
        if (tags is not null)
        {
            var tagList = new List<KeyValuePair<string, object?>>(tags);
            tagList.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
            Tags = tagList.AsReadOnly();
        }

        s_activeSources.Add(this);

        s_allListeners.EnumWithAction((listener, source) =>
        {
            Func<ActivitySource, bool>? shouldListenTo = listener.ShouldListenTo;
            if (shouldListenTo != null)
            {
                var activitySource = (ActivitySource)source;
                if (shouldListenTo(activitySource))
                {
                    activitySource.AddListener(listener);
                }
            }
        }, this);

        GC.KeepAlive(DiagnosticSourceEventSource.Log);
    }

    public string Name { get; }
    public string? Version { get; }
    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; }
    public string? TelemetrySchemaUrl { get; }

    public bool HasListeners()
    {
        SynchronizedList<ActivityListener>? listeners = _listeners;
        return listeners != null && listeners.Count > 0;
    }

    public Activity? StartActivity(string name = "", ActivityKind kind = ActivityKind.Internal)
        => CreateActivity(name, kind, default, null, null, null, default);

    public Activity? StartActivity(            // <-------------------------pact1.0, cact1.0
                                string name,   
                                ActivityKind kind,
                                ActivityContext parentContext, 
                                IEnumerable<KeyValuePair<string, object?>>? tags = null, 
                                IEnumerable<ActivityLink>? links = null,
                                DateTimeOffset startTime = default
                                )
        => CreateActivity(name, kind, parentContext, null, tags, links, startTime);   // <-------------------------pact1.0, cact1.0
                                                                                     
    // ...
    private Activity? CreateActivity(   // <------------------------pact1.1, cact1.1, dlr3.2.4.0
                                     string name,   
                                     ActivityKind kind,
                                     ActivityContext context, 
                                     string? parentId, 
                                     IEnumerable<KeyValuePair<string, object?>>? tags,
                                     IEnumerable<ActivityLink>? links, 
                                     DateTimeOffset startTime, 
                                     bool startIt = true,   // <-----------------
                                     ActivityIdFormat idFormat = ActivityIdFormat.Unknown
                                    )
    {
        // _listeners can get assigned to null in Dispose.
        SynchronizedList<ActivityListener>? listeners = _listeners;
        if (listeners == null || listeners.Count == 0)
        {
            return null;  // <---------------------------won't create an Activity if there is no listener
        }

        Activity? activity = null;
        ActivityTagsCollection? samplerTags;
        string? traceState;

        ActivitySamplingResult samplingResult = ActivitySamplingResult.None;

        if (parentId != null)   // parentId is null for both parentActivity and childActivity
        {
            ActivityCreationOptions<string> aco = default;
            ActivityCreationOptions<ActivityContext> acoContext = default;

            aco = new ActivityCreationOptions<string>(this, name, parentId, kind, tags, links, idFormat);
            if (aco.IdFormat == ActivityIdFormat.W3C)
            {
                // acoContext is used only in the Sample calls which called only when we have W3C Id format.
                acoContext = new ActivityCreationOptions<ActivityContext>(this, name, aco.GetContext(), kind, tags, links, ActivityIdFormat.W3C);
            }

            listeners.EnumWithFunc((ActivityListener listener, ref ActivityCreationOptions<string> data, ref ActivitySamplingResult result, ref ActivityCreationOptions<ActivityContext> dataWithContext) => {
                SampleActivity<string>? sampleUsingParentId = listener.SampleUsingParentId;
                if (sampleUsingParentId != null)
                {
                    ActivitySamplingResult sr = sampleUsingParentId(ref data);
                    dataWithContext.SetTraceState(data.TraceState); // Keep the trace state in sync between data and dataWithContext

                    if (sr > result)
                    {
                        result = sr;
                    }
                }
                else if (data.IdFormat == ActivityIdFormat.W3C)
                {
                    // In case we have a parent Id and the listener not providing the SampleUsingParentId, we'll try to find out if the following conditions are true:
                    //   - The listener is providing the Sample callback
                    //   - Can convert the parent Id to a Context. ActivityCreationOptions.TraceId != default means parent id converted to a valid context.
                    // Then we can call the listener Sample callback with the constructed context.
                    SampleActivity<ActivityContext>? sample = listener.Sample;
                    if (sample != null)
                    {
                        ActivitySamplingResult sr = sample(ref dataWithContext);
                        data.SetTraceState(dataWithContext.TraceState); // Keep the trace state in sync between data and dataWithContext

                        if (sr > result)
                        {
                            result = sr;
                        }
                    }
                }
            }, ref aco, ref samplingResult, ref acoContext);

            if (context == default)
            {
                if (aco.GetContext() != default)
                {
                    context = aco.GetContext();
                    parentId = null;
                }
                else if (acoContext.GetContext() != default)
                {
                    context = acoContext.GetContext();
                    parentId = null;
                }
            }

            samplerTags = aco.GetSamplingTags();
            ActivityTagsCollection? atc = acoContext.GetSamplingTags();
            if (atc != null)
            {
                if (samplerTags == null)
                {
                    samplerTags = atc;
                }
                else
                {
                    foreach (KeyValuePair<string, object?> tag in atc)
                    {
                        samplerTags.Add(tag);
                    }
                }
            }

            idFormat = aco.IdFormat;
            traceState = aco.TraceState;
        }
        else    // <-------------------------pact1.2, cact1.2, for parentActivity and childActivity paths as parentId for parentActivity is null for both of them
        {
            bool useCurrentActivityContext = context == default && Activity.Current != null;
            var aco = new ActivityCreationOptions<ActivityContext>(this, name, useCurrentActivityContext ? Activity.Current!.Context : context, kind, tags, links, idFormat);
            listeners.EnumWithFunc((ActivityListener listener, ref ActivityCreationOptions<ActivityContext> data, ref ActivitySamplingResult result, ref ActivityCreationOptions<ActivityContext> unused) => {
                SampleActivity<ActivityContext>? sample = listener.Sample;
                if (sample != null)
                {
                    ActivitySamplingResult dr = sample(ref data);
                    if (dr > result)
                    {
                        result = dr;
                    }
                }
            }, ref aco, ref samplingResult, ref aco);

            if (!useCurrentActivityContext)
            {          
                context = aco.GetContext();
            }

            samplerTags = aco.GetSamplingTags();
            idFormat = aco.IdFormat;
            traceState = aco.TraceState;
        }

        if (samplingResult != ActivitySamplingResult.None)  // <-------------------------------------sam, discard the creation of Activity if needed
        {
            activity =  Activity.Create(this, name, kind, parentId, context, tags, links, startTime, samplerTags, samplingResult, startIt, idFormat, traceState); // dlr3.2.4.1
                        // <-------------------------pact1.2., cact1.2.                   startIt is true by default
        }

        return activity;
    }

    public void Dispose()
    {
        _listeners = null;
        s_activeSources.Remove(this);
    }

    public static void AddActivityListener(ActivityListener listener)
    {
        if (s_allListeners.AddIfNotExist(listener))
        {
            s_activeSources.EnumWithAction((source, obj) => {
                var shouldListenTo = ((ActivityListener)obj).ShouldListenTo;
                if (shouldListenTo != null && shouldListenTo(source))
                {
                    source.AddListener((ActivityListener)obj);
                }
            }, listener);
        }
    }

    internal delegate void Function<T, TParent>(T item, ref ActivityCreationOptions<TParent> data, ref ActivitySamplingResult samplingResult, ref ActivityCreationOptions<ActivityContext> dataWithContext);

    internal void AddListener(ActivityListener listener)
    {
        if (_listeners == null)
        {
            Interlocked.CompareExchange(ref _listeners, new SynchronizedList<ActivityListener>(), null);
        }

        _listeners.AddIfNotExist(listener);
    }

    internal static void DetachListener(ActivityListener listener)
    {
        s_allListeners.Remove(listener);
        s_activeSources.EnumWithAction((source, obj) => source._listeners?.Remove((ActivityListener) obj), listener);
    }

    internal void NotifyActivityStart(Activity activity)
    {
        SynchronizedList<ActivityListener>? listeners = _listeners;
        if (listeners != null && listeners.Count > 0)
        {
            listeners.EnumWithAction((listener, obj) => listener.ActivityStarted?.Invoke((Activity)obj), activity);
        }
    }

    internal void NotifyActivityStop(Activity activity)
    {
        SynchronizedList<ActivityListener>? listeners = _listeners;
        if (listeners != null && listeners.Count > 0)
        {
            listeners.EnumWithAction((listener, obj) => listener.ActivityStopped?.Invoke((Activity)obj), activity);
        }
    }

    internal void NotifyActivityAddException(Activity activity, Exception exception, ref TagList tags)
    {
        // ...
    }
}

/*
internal sealed class SynchronizedList<T>
{
    private readonly object _writeLock;
    private T[] _volatileArray;
    public SynchronizedList()
    {
        _volatileArray = [];
        _writeLock = new();
    }

    public void Add(T item)
    {
        lock (_writeLock)
        {
            T[] newArray = new T[_volatileArray.Length + 1];

            Array.Copy(_volatileArray, newArray, _volatileArray.Length);// copy existing items
            newArray[_volatileArray.Length] = item;// copy new item

            _volatileArray = newArray;
        }
    }

    public bool AddIfNotExist(T item)
    {
        // on _volatileArray
    }

    public bool Remove(T item)
    {
        // on _volatileArray
    }

    public int Count => _volatileArray.Length;

    public void EnumWithFunc<TParent>(ActivitySource.Function<T, TParent> func, ref ActivityCreationOptions<TParent> data, ref ActivitySamplingResult samplingResult, ref ActivityCreationOptions<ActivityContext> dataWithContext)
    {
        foreach (T item in _volatileArray)
        {
            func(item, ref data, ref samplingResult, ref dataWithContext);
        }
    }

    public void EnumWithAction(Action<T, object> action, object arg)
    {
        foreach (T item in _volatileArray)
        {
            action(item, arg);
        }
    }

    // ...
}
*/
//--------------------------------Ʌ

//--------------------------------------------V
public readonly partial struct ActivityContext : IEquatable<ActivityContext>
{
    public ActivityContext(ActivityTraceId traceId, ActivitySpanId spanId, ActivityTraceFlags traceFlags, string? traceState = null, bool isRemote = false)
    {
        // ... assignments
    }

    public ActivityTraceId TraceId { get; }
    public ActivitySpanId SpanId { get; }>
    public ActivityTraceFlags TraceFlags { get; }
    public string? TraceState { get; }

    public bool IsRemote { get; }
    public static bool TryParse(string? traceParent, string? traceState, bool isRemote, out ActivityContext context)  // <---------------dlr3.2.0
    {
        if (traceParent is null)
        {
            context = default;
            return false;
        }

        return Activity.TryConvertIdToContext(traceParent, traceState, isRemote, out context);  // <---------------dlr3.2.1
    }

    public static bool TryParse(string? traceParent, string? traceState, out ActivityContext context) => TryParse(traceParent, traceState, isRemote: false, out context);
    public static ActivityContext Parse(string traceParent, string? traceState)
    {
        if (!Activity.TryConvertIdToContext(traceParent, traceState, isRemote: false, out ActivityContext context))
            throw new ArgumentException(SR.InvalidTraceParent);

        return context;
    }

    public bool Equals(ActivityContext value) => SpanId.Equals(value.SpanId) && TraceId.Equals(value.TraceId) && TraceFlags == value.TraceFlags && TraceState == value.TraceState && IsRemote == value.IsRemote;

    public override bool Equals([NotNullWhen(true)] object? obj) => (obj is ActivityContext context) ? Equals(context) : false;
    public static bool operator ==(ActivityContext left, ActivityContext right) => left.Equals(right);
    public static bool operator !=(ActivityContext left, ActivityContext right) => !(left == right);
}
//--------------------------------------------Ʌ

//----------------------------------V
public sealed class ActivityListener : IDisposable
{
    // public delegate ActivitySamplingResult SampleActivity<T>(ref ActivityCreationOptions<T> options);
    // public delegate void ExceptionRecorder(Activity activity, Exception exception, ref TagList tags);

    public ActivityListener() { }

    public Action<Activity>? ActivityStarted { get; set; }
    
    public Action<Activity>? ActivityStopped { get; set; }
    
    public ExceptionRecorder? ExceptionRecorder { get; set; }
    
    public Func<ActivitySource, bool>? ShouldListenTo { get; set; }
    
    public SampleActivity<string>? SampleUsingParentId { get; set; }

    public SampleActivity<ActivityContext>? Sample { get; set; }

    public void Dispose() => ActivitySource.DetachListener(this);
}
//----------------------------------Ʌ

//-----------------------------------------------V
public readonly struct ActivityCreationOptions<T>
{
    private readonly ActivityTagsCollection? _samplerTags;
    private readonly ActivityContext _context;
    private readonly string? _traceState;

    internal ActivityCreationOptions(ActivitySource source, string name, T parent, ActivityKind kind, IEnumerable<KeyValuePair<string, object?>>? tags, IEnumerable<ActivityLink>? links, ActivityIdFormat idFormat)
    {
        Source = source;
        Name = name;
        Kind = kind;
        Parent = parent;
        Tags = tags;
        Links = links;
        IdFormat = idFormat;

        if (IdFormat == ActivityIdFormat.Unknown && Activity.ForceDefaultIdFormat)
            IdFormat = Activity.DefaultIdFormat;

        _samplerTags = null;
        _traceState = null;

        if (parent is ActivityContext ac && ac != default)
        {
            _context = ac;
            if (IdFormat == ActivityIdFormat.Unknown)
                IdFormat = ActivityIdFormat.W3C;

            _traceState = ac.TraceState;
        }
        else if (parent is string p)
        {
            if (IdFormat != ActivityIdFormat.Hierarchical)
            {
                if (ActivityContext.TryParse(p, null, out _context))
                    IdFormat = ActivityIdFormat.W3C;

                if (IdFormat == ActivityIdFormat.Unknown)
                    IdFormat = ActivityIdFormat.Hierarchical;
            }
            else
                _context = default;
        }
        else
        {
            _context = default;
            if (IdFormat == ActivityIdFormat.Unknown)
                IdFormat = Activity.Current != null ? Activity.Current.IdFormat : Activity.DefaultIdFormat;
        }
    }

    public ActivitySource Source { get; }

    public string Name { get; }

    public ActivityKind Kind { get; }

    public T Parent { get; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; }

    public IEnumerable<ActivityLink>? Links { get; }

    public ActivityTagsCollection SamplingTags
    {
        get
        {
            if (_samplerTags == null)
                Unsafe.AsRef(in _samplerTags) = new ActivityTagsCollection();

            return _samplerTags!;
        }
    }

    public ActivityTraceId TraceId
    {
        get
        {
            if (Parent is ActivityContext && IdFormat == ActivityIdFormat.W3C && _context == default)
            {
                Func<ActivityTraceId>? traceIdGenerator = Activity.TraceIdGenerator;
                ActivityTraceId id = traceIdGenerator == null ? ActivityTraceId.CreateRandom() : traceIdGenerator();

                // Because the struct is readonly, we cannot directly assign _context. We have to workaround it by calling Unsafe.AsRef
                Unsafe.AsRef(in _context) = new ActivityContext(id, default, ActivityTraceFlags.None);
            }

            return _context.TraceId;
        }
    }

    public string? TraceState
    {
        get => _traceState;
        init
        {
            _traceState = value;
        }
    }

    internal void SetTraceState(string? traceState) => Unsafe.AsRef(in _traceState) = traceState;

    internal ActivityIdFormat IdFormat { get; }

    internal ActivityTagsCollection? GetSamplingTags() => _samplerTags;

    internal ActivityContext GetContext() => _context;
}
//-----------------------------------------------Ʌ
```

```C#
[Flags]
public enum ActivityTraceFlags
{
    None = 0b_0_0000000,
    Recorded = 0b_0_0000001, // The Activity (or more likely its parents) has been marked as useful to record
}

public enum ActivityIdFormat
{
    Unknown = 0,      // ID format is not known.
    Hierarchical = 1, //|XXXX.XX.X_X ... see https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.DiagnosticSource/src/ActivityUserGuide.md#id-format
    W3C = 2,          // 00-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX-XXXXXXXXXXXXXXXX-XX see https://w3c.github.io/trace-context/
};

public enum ActivitySamplingResult
{
    None,
    PropagationData,
    AllData,
    AllDataAndRecorded
}

//------------------------------------V
public readonly struct ActivityTraceId : IEquatable<ActivityTraceId>
{
    private readonly string? _hexString;
 
    internal ActivityTraceId(string? hexString) => _hexString = hexString;

    public static ActivityTraceId CreateRandom()
    {
        Span<byte> span = stackalloc byte[sizeof(ulong) * 2];
        SetToRandomBytes(span);
        return CreateFromBytes(span);
    }

    public static ActivityTraceId CreateFromString(ReadOnlySpan<char> idData)
    {
        if (idData.Length != 32 || !ActivityTraceId.IsLowerCaseHexAndNotAllZeros(idData))
            throw new ArgumentOutOfRangeException(nameof(idData));

        return new ActivityTraceId(idData.ToString());
    }
    // ...
    
    public static bool operator ==(ActivityTraceId traceId1, ActivityTraceId traceId2)
}

public readonly struct ActivitySpanId : IEquatable<ActivitySpanId>
{
    // same as ActivityTraceId
}
//------------------------------------Ʌ
```

```C#
//------------------------------------------------V
public abstract class DistributedContextPropagator
{
    private static DistributedContextPropagator s_current = CreateDefaultPropagator();  // <-----------------dcp

    public delegate void PropagatorGetterCallback(object? carrier, string fieldName, out string? fieldValue, out IEnumerable<string>? fieldValues);
    public delegate void PropagatorSetterCallback(object? carrier, string fieldName, string fieldValue);
    public abstract IReadOnlyCollection<string> Fields { get; }
    public abstract void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter);  // <-----------------dcp, carrier is HttpRequestMessage
    public abstract void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState);
    public abstract IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter);

    public static DistributedContextPropagator Current
    {
        get
        {
            return s_current;
        }

        set
        {
            s_current = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public static DistributedContextPropagator CreateDefaultPropagator() => W3CPropagator.Instance;  // <------------------------------
    public static DistributedContextPropagator CreatePassThroughPropagator() => PassThroughPropagator.Instance;
    public static DistributedContextPropagator CreateW3CPropagator() => W3CPropagator.Instance;  
    public static DistributedContextPropagator CreatePreW3CPropagator() => LegacyPropagator.Instance;
    public static DistributedContextPropagator CreateNoOutputPropagator() => NoOutputPropagator.Instance;

    internal static void InjectBaggage(object? carrier, IEnumerable<KeyValuePair<string, string?>> baggage, PropagatorSetterCallback setter)
    {
        using (IEnumerator<KeyValuePair<string, string?>> e = baggage.GetEnumerator())
        {
            if (e.MoveNext())
            {
                StringBuilder baggageList = new StringBuilder();

                do
                {
                    KeyValuePair<string, string?> item = e.Current;
                    baggageList.Append(WebUtility.UrlEncode(item.Key)).Append('=').Append(WebUtility.UrlEncode(item.Value)).Append(CommaWithSpace);
                } while (e.MoveNext());

                setter(carrier, CorrelationContext, baggageList.ToString(0, baggageList.Length - 2));
            }
        }
    }

    internal const string TraceParent        = "traceparent";
    internal const string RequestId          = "Request-Id";
    internal const string TraceState         = "tracestate";
    internal const string Baggage            = "baggage";
    internal const string CorrelationContext = "Correlation-Context";
    internal const char   Space              = ' ';
    internal const char   Tab                = (char)9;
    internal const char   Comma              = ',';
    internal const char   Semicolon          = ';';
    internal const string CommaWithSpace     = ", ";

    internal static readonly char [] s_trimmingSpaceCharacters = new char[] { Space, Tab };
}
//------------------------------------------------Ʌ

//----------------------------------------------------------------V
internal sealed class W3CPropagator : DistributedContextPropagator
{
    internal static DistributedContextPropagator Instance { get; } = new W3CPropagator();

    private const int MaxBaggageEntriesToEmit = 64;     // Suggested by W3C specs
    private const int MaxBaggageEncodedLength = 8192;   // Suggested by W3C specs
    private const int MaxTraceStateEncodedLength = 256; // Suggested by W3C specs

    internal const string TraceParent        = "traceparent";
    internal const string RequestId          = "Request-Id";
    internal const string TraceState         = "tracestate";
    internal const string Baggage            = "baggage";
    internal const string CorrelationContext = "Correlation-Context";
    internal const char   Space              = ' ';
    internal const char   Tab                = (char)9;
    internal const char   Comma              = ',';
    internal const char   Semicolon          = ';';
    internal const string CommaWithSpace     = ", ";
 
    internal static readonly char [] s_trimmingSpaceCharacters = new char[] { Space, Tab };

    private const char Equal = '=';
    private const char Percent = '%';
    private const char Replacement = '\uFFFD'; // �

    public override IReadOnlyCollection<string> Fields { get; } = new ReadOnlyCollection<string>(new[] { TraceParent, TraceState, Baggage, CorrelationContext });

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)  // <-----------------dcp, carrier is HttpRequestMessage
    {
        if (activity is null || setter is null || activity.IdFormat != ActivityIdFormat.W3C)
        {
            return;
        }

        string? id = activity.Id;
        if (id is null)
        {
            return;
        }

        setter(carrier, TraceParent, id);  // <---------------------dcp
        if (activity.TraceStateString is { Length: > 0 } traceState)
        {
            InjectTraceState(traceState, carrier, setter);
        }

        InjectW3CBaggage(carrier, activity.Baggage, setter);
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        if (getter is null)
        {
            traceId = null;
            traceState = null;
            return;
        }

        getter(carrier, TraceParent, out traceId, out _);
        if (IsInvalidTraceParent(traceId))
        {
            traceId = null;
        }

        getter(carrier, TraceState, out string? traceStateValue, out _);
        traceState = ValidateTraceState(traceStateValue);
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter)
    {
        if (getter is null)
        {
            return null;
        }

        getter(carrier, Baggage, out string? theBaggage, out _);
        if (theBaggage is null)
        {
            getter(carrier, CorrelationContext, out theBaggage, out _);
        }

        TryExtractBaggage(theBaggage, out IEnumerable<KeyValuePair<string, string?>>? baggage);

        return baggage;
    }

    internal static bool TryExtractBaggage(string? baggageString, out IEnumerable<KeyValuePair<string, string?>>? baggage)
    {
        baggage = null;
        List<KeyValuePair<string, string?>>? baggageList = null;

        if (string.IsNullOrEmpty(baggageString))
        {
            return true;
        }

        ReadOnlySpan<char> baggageSpan = baggageString;

        do
        {
            int entrySeparator = baggageSpan.IndexOf(Comma);
            ReadOnlySpan<char> currentEntry = entrySeparator >= 0 ? baggageSpan.Slice(0, entrySeparator) : baggageSpan;

            int keyValueSeparator = currentEntry.IndexOf(Equal);
            if (keyValueSeparator <= 0 || keyValueSeparator >= currentEntry.Length - 1)
            {
                break; // invalid format
            }

            ReadOnlySpan<char> keySpan = currentEntry.Slice(0, keyValueSeparator);
            ReadOnlySpan<char> valueSpan = currentEntry.Slice(keyValueSeparator + 1);

            if (TryDecodeBaggageKey(keySpan, out string? key) && TryDecodeBaggageValue(valueSpan, out string value))
            {
                baggageList ??= new List<KeyValuePair<string, string?>>();
                baggageList.Add(new KeyValuePair<string, string?>(key, value));
            }

            baggageSpan = entrySeparator >= 0 ? baggageSpan.Slice(entrySeparator + 1) : ReadOnlySpan<char>.Empty;
        } while (baggageSpan.Length > 0);

        // reverse order for asp.net compatibility.
        baggageList?.Reverse();

        baggage = baggageList;
        return baggageList != null;
    }

    internal static string? ValidateTraceState(string? traceState)
    {
        if (string.IsNullOrEmpty(traceState))
        {
            return null; // invalid format
        }

        int processed = 0;

        while (processed < traceState.Length)
        {
            ReadOnlySpan<char> traceStateSpan = traceState.AsSpan(processed);
            int commaIndex = traceStateSpan.IndexOf(Comma);
            ReadOnlySpan<char> entry = commaIndex >= 0 ? traceStateSpan.Slice(0, commaIndex) : traceStateSpan;
            int delta = entry.Length + (commaIndex >= 0 ? 1 : 0); // +1 for the comma

            if (processed + delta > MaxTraceStateEncodedLength)
            {
                break; // entry exceeds max length
            }

            int equalIndex = entry.IndexOf(Equal);
            if (equalIndex <= 0 || equalIndex >= entry.Length - 1)
            {
                break; // invalid format
            }

            if (IsInvalidTraceStateKey(Trim(entry.Slice(0, equalIndex))) || IsInvalidTraceStateValue(TrimSpaceOnly(entry.Slice(equalIndex + 1))))
            {
                break; // entry exceeds max length or invalid key/value, skip the whole trace state entries.
            }

            processed += delta;
        }

        if (processed > 0)
        {
            if (traceState[processed - 1] == Comma)
            {
                processed--; // remove the last comma
            }

            if (processed > 0)
            {
                return processed >= traceState.Length ? traceState : traceState.AsSpan(0, processed).ToString();
            }
        }

        return null;
    }

    internal static void InjectTraceState(string traceState, object? carrier, PropagatorSetterCallback setter)
    {

        string? traceStateValue = ValidateTraceState(traceState);
        if (traceStateValue is not null)
        {
            setter(carrier, TraceState, traceStateValue);  // <---------------------------
        }
    }

    internal static void InjectW3CBaggage(object? carrier, IEnumerable<KeyValuePair<string, string?>> baggage, PropagatorSetterCallback setter);
    // ...   
}
//----------------------------------------------------------------Ʌ
```