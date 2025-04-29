## Source Code


```C#
//-------------------V
public class Activity : IDisposable  // In .NET world, a span is represented by an Activity, System.Span class is not related to distributed tracing.
{
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
    
    public Activity(string operationName);

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
            s_current.Value = activity;
        }
        else
        {
            Activity? previous = s_current.Value;
            s_current.Value = activity;
            handler.Invoke(null, new ActivityChangedEventArgs(previous, activity));
        }
    }

    public Activity Start()
    {
        if (_id != null || _spanId != null)
        {
            NotifyError(new InvalidOperationException(SR.ActivityStartAlreadyStarted));
        }
        else
        {
            _previousActiveActivity = Current;
            if (_parentId == null && _parentSpanId is null)
            {
                if (_previousActiveActivity != null)
                {
                    Parent = _previousActiveActivity;
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
                GenerateW3CId();
            else
                _id = GenerateHierarchicalId();

            SetCurrent(this);

            Source.NotifyActivityStart(this);
        }
        return this;
    }

    public bool IsAllDataRequested { get; set; }
    public ActivityIdFormat IdFormat { get; }
    public ActivityKind Kind { get; }
    public string OperationName { get; }
    public string DisplayName { get; set; }
    public IEnumerable<ActivityEvent> Events { get; }
    public ActivitySource Source { get; }
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
    public void Stop();
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

    public Activity? CreateActivity(string name, ActivityKind kind)
        => CreateActivity(name, kind, default, null, null, null, default, startIt: false);
    // ...
    private Activity? CreateActivity(string name, ActivityKind kind, ActivityContext context, string? parentId, IEnumerable<KeyValuePair<string, object?>>? tags,
                                        IEnumerable<ActivityLink>? links, DateTimeOffset startTime, bool startIt = true, ActivityIdFormat idFormat = ActivityIdFormat.Unknown)
    {
        // _listeners can get assigned to null in Dispose.
        SynchronizedList<ActivityListener>? listeners = _listeners;
        if (listeners == null || listeners.Count == 0)
        {
            return null;
        }

        Activity? activity = null;
        ActivityTagsCollection? samplerTags;
        string? traceState;

        ActivitySamplingResult samplingResult = ActivitySamplingResult.None;

        if (parentId != null)
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
        else
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

        if (samplingResult != ActivitySamplingResult.None)
        {
            activity = Activity.Create(this, name, kind, parentId, context, tags, links, startTime, samplerTags, samplingResult, startIt, idFormat, traceState);
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
        ArgumentNullException.ThrowIfNull(listener);

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
    public static bool TryParse(string? traceParent, string? traceState, bool isRemote, out ActivityContext context)
    {
        if (traceParent is null)
        {
            context = default;
            return false;
        }

        return Activity.TryConvertIdToContext(traceParent, traceState, isRemote, out context);
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