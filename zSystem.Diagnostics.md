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
    private string? _displayName;  // <-------------------------------
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
        get {
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

    public void Dispose()
    {
        if (!IsStopped)
        {
            Stop();
        }

        Dispose(true);
        GC.SuppressFinalize(this);
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
                GenerateW3CId();   // <-------------------------pact3.2, cact3.3, create spanId for _traceId
            else
                _id = GenerateHierarchicalId();

            SetCurrent(this);     // <-------------------------pact3.3., cact3.4.

            Source.NotifyActivityStart(this);  // <------------call TracerProviderSdk.listener.ActivityStarted
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

        activity.IsAllDataRequested = request == ActivitySamplingResult.AllData || request == ActivitySamplingResult.AllDataAndRecorded;  // <----------------
 
        if (request == ActivitySamplingResult.AllDataAndRecorded)  // when sampling result is SamplingDecision.RecordAndSample
        {
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;  // set up "samping bit" so it can propagate to next downstream service 
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

        _spanId = ActivitySpanId.CreateRandom().ToHexString();  // <--------------------------
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

    public bool Recorded { get => (ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0; }

    public Activity SetStatus(ActivityStatusCode code, string? description = null)
    {
        _statusCode = code;
        _statusDescription = code == ActivityStatusCode.Error ? description : null;
        return this;
    }

    public Activity AddException(Exception exception, in TagList tags = default, DateTimeOffset timestamp = default)
    {
        TagList exceptionTags = tags;

        Source.NotifyActivityAddException(this, exception, ref exceptionTags);

        const string ExceptionEventName = "exception";
        const string ExceptionMessageTag = "exception.message";
        const string ExceptionStackTraceTag = "exception.stacktrace";
        const string ExceptionTypeTag = "exception.type";

        bool hasMessage = false;
        bool hasStackTrace = false;
        bool hasType = false;

        for (int i = 0; i < exceptionTags.Count; i++)
        {
            if (exceptionTags[i].Key == ExceptionMessageTag)
            {
                hasMessage = true;
            }
            else if (exceptionTags[i].Key == ExceptionStackTraceTag)
            {
                hasStackTrace = true;
            }
            else if (exceptionTags[i].Key == ExceptionTypeTag)
            {
                hasType = true;
            }
        }

        if (!hasMessage)
        {
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionMessageTag, exception.Message));
        }

        if (!hasStackTrace)
        {
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionStackTraceTag, exception.ToString()));
        }

        if (!hasType)
        {
            exceptionTags.Add(new KeyValuePair<string, object?>(ExceptionTypeTag, exception.GetType().ToString()));
        }

        return AddEvent(new ActivityEvent(ExceptionEventName, timestamp, ref exceptionTags));
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

    public ActivityContext Context => new ActivityContext(TraceId, SpanId, ActivityTraceFlags, TraceStateString);

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

    public Activity? StartActivity([CallerMemberName] string name = "", ActivityKind kind = ActivityKind.Internal)
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

    public Activity? CreateActivity(string name, ActivityKind kind, ActivityContext parentContext, IEnumerable<KeyValuePair<string, object?>>? tags = null,
                                    IEnumerable<ActivityLink>? links = null, ActivityIdFormat idFormat = ActivityIdFormat.Unknown)
        => CreateActivity(name, kind, parentContext, null, tags, links, default, startIt: false, idFormat);
                                                                               // <-------- startIt is false by default because of StartActivity call directly
                                                                                     
    // <----------- even though this method is called CreateActivity, but it might not create a brand new activity depending on sampling result
    private Activity? CreateActivity(   // <------------------------pact1.1, cact1.1, dlr3.2.4.0
                                     string name,   
                                     ActivityKind kind,
                                     ActivityContext context, 
                                     string? parentId, 
                                     IEnumerable<KeyValuePair<string, object?>>? tags,
                                     IEnumerable<ActivityLink>? links, 
                                     DateTimeOffset startTime, 
                                     bool startIt = true, // <-----------------------------------
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
                    ActivitySamplingResult dr = sample(ref data);  // <----------------sam, data is ActivityCreationOptions<ActivityContext>, check casr
                                                                   // the Sampler is invoked for every span that is created, including when the parent span is not sampled
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

        if (samplingResult != ActivitySamplingResult.None)  // <---------sam, stop the creation of Activity if the sampler explicitly drop it by return SamplingDecision.Drop
        {                                                   // that's why ActivityListener.Sample doesn't take an parameter of Activity
            activity =  Activity.Create(this, name, kind, parentId, context, tags, links, startTime, samplerTags, samplingResult, startIt, idFormat, traceState); // dlr3.2.4.1
                        // <-------------------------pact1.2., cact1.2.                   
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

    internal void NotifyActivityStart(Activity activity)  // <------------call TracerProviderSdk.listener.ActivityStarted
    {
        SynchronizedList<ActivityListener>? listeners = _listeners;
        if (listeners != null && listeners.Count > 0)
        {
            listeners.EnumWithAction((listener, obj) => listener.ActivityStarted?.Invoke((Activity)obj), activity);  // <---------------------!
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

    public SampleActivity<ActivityContext>? Sample { get; set; }  // <-----------normally set by TracerProviderSdk, see ComputeActivitySamplingResult in OpenTelemetry

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
                ActivityTraceId id = traceIdGenerator == null ? ActivityTraceId.CreateRandom() : traceIdGenerator();  // <--------------------

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
    None,                 // <-------------------------no activity instance will ever be created when sampling result is SamplingDecision.Drop
    PropagationData,      // <-------------------------only collect TraceId and SpanId, NOT tags, baggage etc
    AllData,              // <-------------------------tags, baggage will be collected, NOT marked as sampled (Activity.Recorded is false), will NOT be exported to tracing backend
    AllDataAndRecorded    // <-------------------------marked as sampled, will be exported to tracing backend
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

//----------------------------------V
public readonly struct ActivityEvent  // events are time-stamped annotations that record something (e.g., retries, exceptions) 
{                                     // that happened at a specific moment during the span. while tags are properties that are true for the entire duration of the span
    private static readonly IEnumerable<KeyValuePair<string, object?>> s_emptyTags = Array.Empty<KeyValuePair<string, object?>>();
    private readonly Activity.TagsLinkedList? _tags;

    public ActivityEvent(string name) : this(name, DateTimeOffset.UtcNow, tags: null) { }

    public ActivityEvent(string name, DateTimeOffset timestamp = default, ActivityTagsCollection? tags = null) : this(name, timestamp, tags, tags is null ? 0 : tags.Count) { }

    internal ActivityEvent(string name, DateTimeOffset timestamp, ref TagList tags) : this(name, timestamp, tags, tags.Count) { }

    private ActivityEvent(string name, DateTimeOffset timestamp, IEnumerable<KeyValuePair<string, object?>>? tags, int tagsCount)
    {
        Name = name ?? string.Empty;
        Timestamp = timestamp != default ? timestamp : DateTimeOffset.UtcNow;

        _tags = tagsCount > 0 ? new Activity.TagsLinkedList(tags!) : null;
    }

    public string Name { get; }

    public DateTimeOffset Timestamp { get; }

    public IEnumerable<KeyValuePair<string, object?>> Tags => _tags ?? s_emptyTags;

    public Activity.Enumerator<KeyValuePair<string, object?>> EnumerateTagObjects() => new Activity.Enumerator<KeyValuePair<string, object?>>(_tags?.First);
}
//----------------------------------Ʌ

//-----------------------------------------V
public readonly partial struct ActivityLink : IEquatable<ActivityLink>
{
    private readonly Activity.TagsLinkedList? _tags;

    public ActivityLink(ActivityContext context, ActivityTagsCollection? tags = null)
    {
        Context = context;

        _tags = tags?.Count > 0 ? new Activity.TagsLinkedList(tags) : null;
    }

    public ActivityContext Context { get; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags => _tags;

    public override bool Equals([NotNullWhen(true)] object? obj) => (obj is ActivityLink link) && this.Equals(link);

    public Activity.Enumerator<KeyValuePair<string, object?>> EnumerateTagObjects() => new Activity.Enumerator<KeyValuePair<string, object?>>(_tags?.First);
}
//-----------------------------------------Ʌ

public enum ActivityStatusCode
{
    Unset = 0,
    Ok = 1, 
    Error = 2
}
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

================================================================================================================================================


## Metrics

```C#
//------------------------------V
public class Meter : IDisposable
{
    private static readonly List<Meter> s_allMeters = new List<Meter>();
    private List<Instrument> _instruments = new List<Instrument>();
    private Dictionary<string, List<Instrument>> _nonObservableInstrumentsCache = new();

    internal bool Disposed { get; private set; }

    internal static bool IsSupported { get; } = InitializeIsSupported();

    private static bool InitializeIsSupported() =>
        AppContext.TryGetSwitch("System.Diagnostics.Metrics.Meter.IsSupported", out bool isSupported) ? isSupported : true;

    public Meter(MeterOptions options)
    {
        Initialize(options.Name, options.Version, options.Tags, options.Scope, options.TelemetrySchemaUrl);   // <--------------------------met1.0
    }

    public Meter(string name) : this(name, null, null, null) { }

    public Meter(string name, string? version) : this(name, version, null, null) { }

    public Meter(string name, string? version, IEnumerable<KeyValuePair<string, object?>>? tags, object? scope = null)
    {
        Initialize(name, version, tags, scope, telemetrySchemaUrl: null);
    }

    private void Initialize(string name, string? version, IEnumerable<KeyValuePair<string, object?>>? tags, object? scope = null, string? telemetrySchemaUrl = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Version = version;
        if (tags is not null)
        {
            var tagList = new List<KeyValuePair<string, object?>>(tags);
            tagList.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
            Tags = tagList.AsReadOnly();
        }
        Scope = scope;
        TelemetrySchemaUrl = telemetrySchemaUrl;

        if (!IsSupported)
        {
            return;
        }

        lock (Instrument.SyncObject)
        {
            s_allMeters.Add(this);  // <-----------------------------met1.1.
        }

        GC.KeepAlive(MetricsEventSource.Log);
    }

    public string Name { get; private set; }

    public string? Version { get; private set; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; private set; }

    public object? Scope { get; private set; }

    public string? TelemetrySchemaUrl { get; private set; }

    public Counter<T> CreateCounter<T>(string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) where T : struct
        => (Counter<T>)GetOrCreateInstrument<T>(typeof(Counter<T>), name, unit, description, tags, () => new Counter<T>(this, name, unit, description, tags));

    public Gauge<T> CreateGauge<T>(string name, string? unit = null, string? description = null, IEnumerable<KeyValuePair<string, object?>>? tags = null) where T : struct
        => (Gauge<T>)GetOrCreateInstrument<T>(typeof(Gauge<T>), name, unit, description, tags, () => new Gauge<T>(this, name, unit, description, tags));

    public Histogram<T> CreateHistogram<T>(string name, string? unit = default, string? description = default, IEnumerable<KeyValuePair<string, object?>>? tags = default, InstrumentAdvice<T>? advice = default) where T : struct
        => (Histogram<T>)GetOrCreateInstrument<T>(typeof(Histogram<T>), name, unit, description, tags, () => new Histogram<T>(this, name, unit, description, tags, advice));

    public UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) where T : struct
        => (UpDownCounter<T>)GetOrCreateInstrument<T>(typeof(UpDownCounter<T>), name, unit, description, tags, () => new UpDownCounter<T>(this, name, unit, description, tags));

    public ObservableUpDownCounter<T> CreateObservableUpDownCounter<T>(string name, Func<IEnumerable<Measurement<T>>> observeValues, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) where T : struct
        =>  new ObservableUpDownCounter<T>(this, name, observeValues, unit, description, tags);

    // ...

    public void Dispose() => Dispose(true);

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        List<Instrument>? instruments = null;

        lock (Instrument.SyncObject)
        {
            if (Disposed)
            {
                return;
            }
            Disposed = true;

            s_allMeters.Remove(this);
            instruments = _instruments;
            _instruments = new List<Instrument>();
        }

        lock (_nonObservableInstrumentsCache)
        {
            _nonObservableInstrumentsCache.Clear();
        }

        if (instruments is not null)
        {
            foreach (Instrument instrument in instruments)
            {
                instrument.NotifyForUnpublishedInstrument();
            }
        }
    }

    private static Instrument? GetCachedInstrument(List<Instrument> instrumentList, Type instrumentType, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        foreach (Instrument instrument in instrumentList)
        {
            if (instrument.GetType() == instrumentType && instrument.Unit == unit &&
                instrument.Description == description && DiagnosticsHelper.CompareTags(instrument.Tags as List<KeyValuePair<string, object?>>, tags))
            {
                return instrument;
            }
        }

        return null;
    }

    // AddInstrument will be called when publishing the instrument (i.e. calling Instrument.Publish()).
    private Instrument GetOrCreateInstrument<T>(Type instrumentType, string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags, Func<Instrument> instrumentCreator)
    {
        List<Instrument>? instrumentList;

        lock (_nonObservableInstrumentsCache)
        {
            if (!_nonObservableInstrumentsCache.TryGetValue(name, out instrumentList))
            {
                instrumentList = new List<Instrument>();
                _nonObservableInstrumentsCache.Add(name, instrumentList);
            }
        }

        lock (instrumentList)
        {
            // Find out if the instrument is already created.
            Instrument? cachedInstrument = GetCachedInstrument(instrumentList, instrumentType, unit, description, tags);
            if (cachedInstrument is not null)
            {
                return cachedInstrument;
            }
        }

        Instrument newInstrument = instrumentCreator.Invoke();

        lock (instrumentList)
        {
            // It is possible GetOrCreateInstrument get called synchronously from different threads with same instrument name.
            // we need to ensure only one instrument is added to the list.
            Instrument? cachedInstrument = GetCachedInstrument(instrumentList, instrumentType, unit, description, tags);
            if (cachedInstrument is not null)
            {
                return cachedInstrument;
            }

            instrumentList.Add(newInstrument);
        }

        return newInstrument;
    }

    // AddInstrument will be called when publishing the instrument (i.e. calling Instrument.Publish()).
    // This method is called inside the lock Instrument.SyncObject
    internal bool AddInstrument(Instrument instrument)
    {
        if (!_instruments.Contains(instrument))
        {
            _instruments.Add(instrument);
            return true;
        }
        return false;
    }

    // called from MeterListener.Start, this method is called inside the lock Instrument.SyncObject
    internal static List<Instrument>? GetPublishedInstruments()
    {
        List<Instrument>? instruments = null;

        if (s_allMeters.Count > 0)
        {
            instruments = new List<Instrument>();

            foreach (Meter meter in s_allMeters)
            {
                foreach (Instrument instrument in meter._instruments)
                {
                    instruments.Add(instrument);
                }
            }
        }

        return instruments;
    }
}
//------------------------------Ʌ

//-------------------------------------------V
public readonly struct Measurement<T> where T : struct
{
    private readonly KeyValuePair<string, object?>[] _tags;

    public Measurement(T value)
    {
        _tags = Instrument.EmptyTags;
        Value = value;
    }

    public Measurement(T value, IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        _tags = tags?.ToArray() ?? Instrument.EmptyTags;
        Value = value;
    }

    public Measurement(T value, params KeyValuePair<string, object?>[]? tags)
    {
        if (tags is not null)
        {
            _tags = new KeyValuePair<string, object?>[tags.Length];
            tags.CopyTo(_tags, 0);
        }
        else
        {
            _tags = Instrument.EmptyTags;
        }

        Value = value;
    }

    public Measurement(T value, params ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _tags = tags.ToArray();
        Value = value;
    }

    public Measurement(T value, in TagList tags)
    {
        if (tags.Count > 0)
        {
            _tags = new KeyValuePair<string, object?>[tags.Count];
            tags.CopyTo(_tags.AsSpan());
        }
        else
        {
            _tags = Instrument.EmptyTags;
        }

        Value = value;
    }

    public ReadOnlySpan<KeyValuePair<string, object?>> Tags => _tags.AsSpan();

    public T Value { get; }
}
//-------------------------------------------Ʌ

//---------------------------------------------V
public sealed class InstrumentAdvice<T> where T : struct
{
    private readonly ReadOnlyCollection<T>? _HistogramBucketBoundaries;

    public InstrumentAdvice()
    {
        Instrument.ValidateTypeParameter<T>();
    }

    public IReadOnlyList<T>? HistogramBucketBoundaries
    {
        get => _HistogramBucketBoundaries;
        init
        {
            List<T> bucketBoundariesCopy = new List<T>(value);

            if (!IsSortedAndDistinct(bucketBoundariesCopy))
            {
                throw new ArgumentException(SR.InvalidHistogramExplicitBucketBoundaries, nameof(value));
            }

            _HistogramBucketBoundaries = new ReadOnlyCollection<T>(bucketBoundariesCopy);
        }
    }

    private static bool IsSortedAndDistinct(List<T> values)
    {
        Comparer<T> comparer = Comparer<T>.Default;

        for (int i = 1; i < values.Count; i++)
        {
            if (comparer.Compare(values[i - 1], values[i]) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
//---------------------------------------------Ʌ

//------------------------------V
public abstract class Instrument
{
    internal static KeyValuePair<string, object?>[] EmptyTags => Array.Empty<KeyValuePair<string, object?>>();

    internal static object SyncObject { get; } = new object();

    internal readonly DiagLinkedList<ListenerSubscription> _subscriptions = new DiagLinkedList<ListenerSubscription>();

    protected Instrument(Meter meter, string name) : this(meter, name, unit: null, description: null, tags: null) { }
    protected Instrument(Meter meter, string name, string? unit, string? description) : this(meter, name, unit, description, tags: null) { }

    protected Instrument(Meter meter, string name, string? unit = default, string? description = default, IEnumerable<KeyValuePair<string, object?>>? tags = default)
    {
        Meter = meter ?? throw new ArgumentNullException(nameof(meter));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description;
        Unit = unit;
        if (tags is not null)
        {
            var tagList = new List<KeyValuePair<string, object?>>(tags);
            tagList.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
            Tags = tagList;
        }
    }

    protected void Publish()   // <-----------------------met2.1
    {
        // All instruments call Publish when they are created. We don't want to publish the instrument if the Meter is not supported.
        if (!Meter.IsSupported)
        {
            return;
        }

        List<MeterListener>? allListeners = null;
        lock (Instrument.SyncObject)
        {
            if (Meter.Disposed || !Meter.AddInstrument(this))   // <-----------------met2.2, add this Instrument to Meter's internal list of instruments
            {
                return;
            }

            allListeners = MeterListener.GetAllListeners();   // <-----------------------
        }

        if (allListeners is not null)
        {
            foreach (MeterListener listener in allListeners)
            {
                listener.InstrumentPublished?.Invoke(this, listener);   // <-----------------------met2.3
            }
        }
    }

    public Meter Meter { get; }   // <-----------------------------

    public string Name { get; }

    public string? Description { get; }

    public string? Unit { get; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; }

    public bool Enabled => _subscriptions.First is not null;

    public virtual bool IsObservable => false;

    internal void NotifyForUnpublishedInstrument()  // NotifyForUnpublishedInstrument is called from Meter.Dispose()
    {
        DiagNode<ListenerSubscription>? current = _subscriptions.First;
        while (current is not null)
        {
            current.Value.Listener.DisableMeasurementEvents(this);
            current = current.Next;
        }

        _subscriptions.Clear();
    }

    internal static void ValidateTypeParameter<T>()
    {
        Type type = typeof(T);
        if (type != typeof(byte) && type != typeof(short) && type != typeof(int) && type != typeof(long) &&
            type != typeof(double) && type != typeof(float) && type != typeof(decimal))
        {
            throw new InvalidOperationException(SR.Format(SR.UnsupportedType, type));
        }
    }

   
    internal object? EnableMeasurement(ListenerSubscription subscription, out bool oldStateStored)  
    {
        oldStateStored = false;

        if (!_subscriptions.AddIfNotExist(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener)))
        {
            ListenerSubscription oldSubscription = _subscriptions.Remove(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener));
            _subscriptions.AddIfNotExist(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener));
            oldStateStored = object.ReferenceEquals(oldSubscription.Listener, subscription.Listener);
            return oldSubscription.State;
        }

        return false;
    }

    internal object? DisableMeasurements(MeterListener listener)  // called from MeterListener.DisableMeasurementEvents
        => _subscriptions.Remove(new ListenerSubscription(listener), (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener)).State;

    internal virtual void Observe(MeterListener listener) => throw new InvalidOperationException();

    internal object? GetSubscriptionState(MeterListener listener)
    {
        DiagNode<ListenerSubscription>? current = _subscriptions.First;
        while (current is not null)
        {
            if (object.ReferenceEquals(listener, current.Value.Listener))
            {
                return current.Value.State;
            }
            current = current.Next;
        }

        return null;
    }
}
//------------------------------Ʌ

//-----------------------------------------V
public abstract partial class Instrument<T> : Instrument where T : struct
{     
    public InstrumentAdvice<T>? Advice { get; }

    protected Instrument(Meter meter, string name) : this(meter, name, unit: null, description: null, tags: null, advice: null) { }

    protected Instrument(Meter meter, string name, string? unit, string? description): this(meter, name, unit, description, tags: null, advice: null) { }

    protected Instrument(Meter meter, string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) : this(meter, name, unit, description, tags, advice: null) { }

    protected Instrument(
        Meter meter,
        string name,
        string? unit = default,
        string? description = default,
        IEnumerable<KeyValuePair<string, object?>>? tags = default,
        InstrumentAdvice<T>? advice = default)
        : base(meter, name, unit, description, tags)
    {
        Advice = advice;

        ValidateTypeParameter<T>();
    }

    protected void RecordMeasurement(T measurement) => RecordMeasurement(measurement, Instrument.EmptyTags.AsSpan());  // <--------------------------------met3.1

    protected void RecordMeasurement(T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        DiagNode<ListenerSubscription>? current = _subscriptions.First;
        while (current is not null)
        {
            current.Value.Listener.NotifyMeasurement(this, measurement, tags, current.Value.State);   // <----------------------------------met3.2
            current = current.Next;
        }
    }
}
//-----------------------------------------Ʌ

//----------------------------V
public sealed class Counter<T> : Instrument<T> where T : struct
{
    internal Counter(Meter meter, string name, string? unit, string? description) : this(meter, name, unit, description, null) { }

    internal Counter(Meter meter, string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) : base(meter, name, unit, description, tags)
    {        
                     // meter is passed to Instrument's constructor
        Publish();   // <-----------------------met2.0, call Instrument's Publish, not Instrument<T>'s
    }

    public void Add(T delta) => RecordMeasurement(delta);   // <--------------------------------met3.0

    public void Add(T delta, KeyValuePair<string, object?> tag) => RecordMeasurement(delta, tag);

    public void Add(T delta, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2) => RecordMeasurement(delta, tag1, tag2);

    public void Add(T delta, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2, KeyValuePair<string, object?> tag3) 
        => RecordMeasurement(delta, tag1, tag2, tag3);

    public void Add(T delta, params ReadOnlySpan<KeyValuePair<string, object?>> tags) => RecordMeasurement(delta, tags);

    public void Add(T delta, params KeyValuePair<string, object?>[] tags) => RecordMeasurement(delta, tags.AsSpan());

    public void Add(T delta, in TagList tagList) => RecordMeasurement(delta, in tagList);
}
//----------------------------Ʌ

public sealed class Histogram<T> : Instrument<T> where T : struct
{
    internal Histogram(Meter meter, string name, string? unit, string? description) : this(meter, name, unit, description, tags: null, advice: null) { }

    internal Histogram(Meter meter, string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags, InstrumentAdvice<T>? advice)
        : base(meter, name, unit, description, tags, advice)
    {
        Publish();
    }

    public void Record(T value) => RecordMeasurement(value);

    public void Record(T value, KeyValuePair<string, object?> tag) => RecordMeasurement(value, tag);

    public void Record(T value, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2) => RecordMeasurement(value, tag1, tag2);

    public void Record(T value, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2, KeyValuePair<string, object?> tag3) => RecordMeasurement(value, tag1, tag2, tag3);

    public void Record(T value, params ReadOnlySpan<KeyValuePair<string, object?>> tags) => RecordMeasurement(value, tags);

    public void Record(T value, params KeyValuePair<string, object?>[] tags) => RecordMeasurement(value, tags.AsSpan());

    public void Record(T value, in TagList tagList) => RecordMeasurement(value, in tagList);
}


//----------------------------------------V
public static class MeterFactoryExtensions
{
    public static Meter Create(this IMeterFactory meterFactory, string name, string? version = null, IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        return meterFactory.Create(new MeterOptions(name)
        {
            Version = version,
            Tags = tags,
            Scope = meterFactory
        });
    }
}
//----------------------------------------Ʌ

//---------------------------------------------V
public sealed class MeterListener : IDisposable
{
    private static readonly List<MeterListener> s_allStartedListeners = new List<MeterListener>();
    private readonly DiagLinkedList<Instrument> _enabledMeasurementInstruments = new DiagLinkedList<Instrument>();
    private bool _disposed;

    // initialize all measurement callback with no-op operations so we'll avoid the null checks during the execution;
    private MeasurementCallback<byte> _byteMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<short> _shortMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<int> _intMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<long> _longMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<float> _floatMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<double> _doubleMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<decimal> _decimalMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    // <-----------------------smec set those callbacks

    public MeterListener() 
    { 
        RuntimeMetrics.EnsureInitialized(); 
    }

    public Action<Instrument, MeterListener>? InstrumentPublished { get; set; }

    public Action<Instrument, object?>? MeasurementsCompleted { get; set; }

    public void EnableMeasurementEvents(Instrument instrument, object? state = null)   // <----------------------------met2.5.0
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        bool oldStateStored = false;
        bool enabled = false;
        object? oldState = null;

        lock (Instrument.SyncObject)
        {
            if (instrument is not null && !_disposed && !instrument.Meter.Disposed)
            {
                _enabledMeasurementInstruments.AddIfNotExist(instrument, object.ReferenceEquals);
                oldState = instrument.EnableMeasurement(new ListenerSubscription(this, state), out oldStateStored);
                enabled = true;
            }
        }

        if (enabled)
        {
            if (oldStateStored && MeasurementsCompleted is not null)
            {
                MeasurementsCompleted?.Invoke(instrument!, oldState);
            }
        }
        else
        {
            // The caller trying to enable the measurements but it didn't happen because the meter or the listener is disposed.
            // We need to send MeasurementsCompleted notification telling this instrument is not enabled for measuring.
            MeasurementsCompleted?.Invoke(instrument!, state);
        }
    }

    public object? DisableMeasurementEvents(Instrument instrument)
    {
        if (!Meter.IsSupported)
        {
            return default;
        }

        object? state = null;
        lock (Instrument.SyncObject)
        {
            if (instrument is null || _enabledMeasurementInstruments.Remove(instrument, object.ReferenceEquals) == default)
            {
                return default;
            }

            state = instrument.DisableMeasurements(this);
        }

        MeasurementsCompleted?.Invoke(instrument, state);
        return state;
    }

    public void SetMeasurementEventCallback<T>(MeasurementCallback<T>? measurementCallback) where T : struct
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        measurementCallback ??= (instrument, measurement, tags, state) => { /* no-op */};

        if (typeof(T) == typeof(byte))
            _byteMeasurementCallback = (MeasurementCallback<byte>)(object)measurementCallback;
        else if (typeof(T) == typeof(int))
            _intMeasurementCallback = (MeasurementCallback<int>)(object)measurementCallback;
        // ...
    }

    public void Start()
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        List<Instrument>? publishedInstruments = null;
        lock (Instrument.SyncObject)
        {
            if (_disposed)
            {
                return;
            }

            if (!s_allStartedListeners.Contains(this))
            {
                s_allStartedListeners.Add(this);  // <------------------------------
                publishedInstruments = Meter.GetPublishedInstruments();
            }
        }

        if (publishedInstruments is not null)
        {
            foreach (Instrument instrument in publishedInstruments)
            {
                InstrumentPublished?.Invoke(instrument, this);
            }
        }
    }

    public void RecordObservableInstruments()
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        List<Exception>? exceptionsList = null;
        DiagNode<Instrument>? current = _enabledMeasurementInstruments.First;
        while (current is not null)
        {
            if (current.Value.IsObservable)
            {
                try
                {
                    current.Value.Observe(this);
                }
                catch (Exception e)
                {
                    exceptionsList ??= new List<Exception>();
                    exceptionsList.Add(e);
                }
            }

            current = current.Next;
        }

        if (exceptionsList is not null)
        {
            throw new AggregateException(exceptionsList);
        }
    }

    public void Dispose()
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        Dictionary<Instrument, object?>? callbacksArguments = null;
        Action<Instrument, object?>? measurementsCompleted = MeasurementsCompleted;

        lock (Instrument.SyncObject)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            s_allStartedListeners.Remove(this);

            DiagNode<Instrument>? current = _enabledMeasurementInstruments.First;
            if (current is not null)
            {
                if (measurementsCompleted is not null)
                {
                    callbacksArguments = new Dictionary<Instrument, object?>();
                }

                do
                {
                    object? state = current.Value.DisableMeasurements(this);
                    callbacksArguments?.Add(current.Value, state);
                    current = current.Next;
                } while (current is not null);

                _enabledMeasurementInstruments.Clear();
            }
        }

        if (callbacksArguments is not null)
        {
            foreach (KeyValuePair<Instrument, object?> kvp in callbacksArguments)
            {
                measurementsCompleted?.Invoke(kvp.Key, kvp.Value);
            }
        }
    }

    // GetAllListeners is called from Instrument.Publish inside Instrument.SyncObject lock.
    internal static List<MeterListener>? GetAllListeners() => s_allStartedListeners.Count == 0 ? null : new List<MeterListener>(s_allStartedListeners);

    internal void NotifyMeasurement<T>(   // <-------------------------met3.3
        Instrument instrument, 
        T measurement, 
        ReadOnlySpan<KeyValuePair<string, object?>> tags, 
        object? state) where T : struct
    {
        if (typeof(T) == typeof(byte))
            _byteMeasurementCallback(instrument, (byte)(object)measurement, tags, state);
        if (typeof(T) == typeof(int))
            _intMeasurementCallback(instrument, (int)(object)measurement, tags, state);   // <-------------------------met3.4
        // ...
    }
}

internal readonly struct ListenerSubscription
{
    internal ListenerSubscription(MeterListener listener, object? state = null)
    {
        Listener = listener;
        State = state;
    }

    internal MeterListener Listener { get; }
    internal object? State { get; }
}
//---------------------------------------------Ʌ

//-------------------------------------------V
public abstract class ObservableInstrument<T> : Instrument where T : struct
{
    protected ObservableInstrument(Meter meter, string name, string? unit, string? description) : this(meter, name, unit, description, tags: null) { }

    protected ObservableInstrument(Meter meter, string name, string? unit, string? description, IEnumerable<KeyValuePair<string, object?>>? tags) : base(meter, name, unit, description, tags)
    {
        ValidateTypeParameter<T>();
    }

    protected abstract IEnumerable<Measurement<T>> Observe();

    public override bool IsObservable => true;

    internal override void Observe(MeterListener listener)
    {
        object? state = GetSubscriptionState(listener);

        IEnumerable<Measurement<T>> measurements = Observe();
        if (measurements is null)
        {
            return;
        }

        foreach (Measurement<T> measurement in measurements)
        {
            listener.NotifyMeasurement(this, measurement.Value, measurement.Tags, state);
        }
    }

    internal static IEnumerable<Measurement<T>> Observe(object callback)
    {
        if (callback is Func<T> valueOnlyFunc)
        {
            return new Measurement<T>[1] { new Measurement<T>(valueOnlyFunc()) };
        }

        if (callback is Func<Measurement<T>> measurementOnlyFunc)
        {
            return new Measurement<T>[1] { measurementOnlyFunc() };
        }

        if (callback is Func<IEnumerable<Measurement<T>>> listOfMeasurementsFunc)
        {
            return listOfMeasurementsFunc();
        }

        Debug.Fail("Execution shouldn't reach this point");
        return null;
    }
}
//-------------------------------------------Ʌ

//--------------------------------------V
public sealed class ObservableCounter<T> : ObservableInstrument<T> where T : struct
{
    private readonly object _callback;

    internal ObservableCounter(Meter meter, string name, Func<T> observeValue, string? unit, string? description) 
        : this(meter, name, observeValue, unit, description, tags: null) { }

    // ...

    internal ObservableCounter(
        Meter meter, 
        string name, 
        Func<IEnumerable<Measurement<T>>> observeValues, 
        string? unit, 
        string? description, 
        IEnumerable<KeyValuePair<string, object?>>? tags) : base(meter, name, unit, description, tags)
    {
        _callback = observeValues ?? throw new ArgumentNullException(nameof(observeValues));
        Publish();
    }

    protected override IEnumerable<Measurement<T>> Observe() => Observe(_callback);
}
//--------------------------------------Ʌ

public static class MeterFactoryExtensions
{
    public static Meter Create(this IMeterFactory meterFactory, string name, string? version = null, IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {

        return meterFactory.Create(new MeterOptions(name)
        {
            Version = version,
            Tags = tags,
            Scope = meterFactory
        });
    }
}

//----------------------------V
public interface IMeterFactory : IDisposable
{      
    Meter Create(MeterOptions options);
}
//----------------------------Ʌ

//-------------------------------------V
internal sealed class DummyMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new Meter(options);  // <--------------------------met0
 
    public void Dispose() { }
}
//-------------------------------------Ʌ

//-----------------------V
public class MeterOptions
{
    private string _name;

    public string Name
    {
        get => _name;
        set => _name = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string? Version { get; set; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; set; }

    public object? Scope { get; set; }

    public string? TelemetrySchemaUrl { get; set; }

    public MeterOptions(string name)
    {
        Name = name;
    }
}
//-----------------------Ʌ
```