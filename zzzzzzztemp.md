## OpenTelemetry Metrics

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

    public Histogram<T> CreateHistogram<T>(string name, string? unit = default, string? description = default, IEnumerable<KeyValuePair<string, object?>>? tags = default, InstrumentAdvice<T>? advice = default) where T : struct
        => (Histogram<T>)GetOrCreateInstrument<T>(typeof(Histogram<T>), name, unit, description, tags, () => new Histogram<T>(this, name, unit, description, tags, advice));

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
```

```C#
//----------------------------------------V
public abstract partial class MetricReader : IDisposable
{
    private const MetricReaderTemporalityPreference MetricReaderTemporalityPreferenceUnspecified = (MetricReaderTemporalityPreference)0;

    private static readonly Func<Type, AggregationTemporality> CumulativeTemporalityPreferenceFunc =
        (instrumentType) => AggregationTemporality.Cumulative;

    private static readonly Func<Type, AggregationTemporality> MonotonicDeltaTemporalityPreferenceFunc = (instrumentType) =>
    {
        return instrumentType.GetGenericTypeDefinition() switch
        {
            var type when type == typeof(Counter<>) => AggregationTemporality.Delta,
            var type when type == typeof(ObservableCounter<>) => AggregationTemporality.Delta,
            var type when type == typeof(Histogram<>) => AggregationTemporality.Delta,

            // Temporality is not defined for gauges, so this does not really affect anything.
            var type when type == typeof(ObservableGauge<>) => AggregationTemporality.Delta,
            var type when type == typeof(Gauge<>) => AggregationTemporality.Delta,

            var type when type == typeof(UpDownCounter<>) => AggregationTemporality.Cumulative,
            var type when type == typeof(ObservableUpDownCounter<>) => AggregationTemporality.Cumulative,

            // TODO: Consider logging here because we should not fall through to this case.
            _ => AggregationTemporality.Delta,
        };
    };

    private readonly Lock newTaskLock = new();
    private readonly Lock onCollectLock = new();
    private readonly TaskCompletionSource<bool> shutdownTcs = new();
    private MetricReaderTemporalityPreference temporalityPreference = MetricReaderTemporalityPreferenceUnspecified;
    private Func<Type, AggregationTemporality> temporalityFunc = CumulativeTemporalityPreferenceFunc;
    private int shutdownCount;
    private TaskCompletionSource<bool>? collectionTcs;
    private BaseProvider? parentProvider;

    public MetricReaderTemporalityPreference TemporalityPreference
    {
        get
        {
            if (this.temporalityPreference == MetricReaderTemporalityPreferenceUnspecified)
            {
                this.temporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            }

            return this.temporalityPreference;
        }

        set
        {
            if (this.temporalityPreference != MetricReaderTemporalityPreferenceUnspecified)
            {
                throw new NotSupportedException($"The temporality preference cannot be modified (the current value is {this.temporalityPreference}).");
            }

            this.temporalityPreference = value;
            this.temporalityFunc = value switch
            {
                MetricReaderTemporalityPreference.Delta => MonotonicDeltaTemporalityPreferenceFunc,
                _ => CumulativeTemporalityPreferenceFunc,
            };
        }
    }

    public bool Collect(int timeoutMilliseconds = Timeout.Infinite)
    {
        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect method called.");
        var shouldRunCollect = false;
        var tcs = this.collectionTcs;

        if (tcs == null)
        {
            lock (this.newTaskLock)
            {
                tcs = this.collectionTcs;

                if (tcs == null)
                {
                    shouldRunCollect = true;
                    tcs = new TaskCompletionSource<bool>();
                    this.collectionTcs = tcs;
                }
            }
        }

        if (!shouldRunCollect)
            return Task.WaitAny([tcs.Task, this.shutdownTcs.Task], timeoutMilliseconds) == 0 && tcs.Task.Result;

        var result = false;
        try
        {
            lock (this.onCollectLock)
            {
                this.collectionTcs = null;
                result = this.OnCollect(timeoutMilliseconds);   // <-------------------------------------
            }
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Collect), ex);
        }

        tcs.TrySetResult(result);

        if (result)
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect succeeded.");
        else
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect failed.");

        return result;
    }

    protected virtual bool OnCollect(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.OnCollect called.");

        var sw = timeoutMilliseconds == Timeout.Infinite ? null : Stopwatch.StartNew();

        var meterProviderSdk = this.parentProvider as MeterProviderSdk;
        meterProviderSdk?.CollectObservableInstruments();

        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("Observable instruments collected.");

        var metrics = this.GetMetricsBatch();   // <----------------------------------------------------

        bool result;
        if (sw == null)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics called.");
            result = this.ProcessMetrics(metrics, Timeout.Infinite);
            if (result)
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics succeeded.");
            else
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics failed.");

            return result;
        }
        else
        {
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

            if (timeout <= 0)
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("OnCollect failed timeout period has elapsed.");
                return false;
            }

            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics called.");
            result = this.ProcessMetrics(metrics, (int)timeout);
            if (result)
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics succeeded.");
            else
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics failed.");

            return result;
        }
    }

    public bool Shutdown(int timeoutMilliseconds = Timeout.Infinite)
    {
        Guard.ThrowIfInvalidTimeout(timeoutMilliseconds);

        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown called.");

        if (Interlocked.CompareExchange(ref this.shutdownCount, 1, 0) != 0)
            return false; // shutdown already called

        var result = false;
        try
        {
            result = this.OnShutdown(timeoutMilliseconds);
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Shutdown), ex);
        }

        this.shutdownTcs.TrySetResult(result);

        if (result)
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown succeeded.");
        else
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown failed.");

        return result;
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal virtual void SetParentProvider(BaseProvider parentProvider)
    {
        this.parentProvider = parentProvider;
    }

    internal virtual bool ProcessMetrics(in Batch<Metric> metrics, int timeoutMilliseconds)
    {
        return true;
    }

    protected virtual bool OnShutdown(int timeoutMilliseconds) => this.Collect(timeoutMilliseconds);

    protected virtual void Dispose(bool disposing){ }
}
//----------------------------------------Ʌ

//----------------------------------------V  from MetricReaderExt.cs
public abstract partial class MetricReader
{
    private readonly HashSet<string> metricStreamNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MetricStreamIdentity, Metric?> instrumentIdentityToMetric = new();
    private readonly Lock instrumentCreationLock = new();
    private int metricLimit;
    private int cardinalityLimit;
    private Metric?[]? metrics;
    private Metric[]? metricsCurrentBatch;
    private int metricIndex = -1;
    private ExemplarFilterType? exemplarFilter;
    private ExemplarFilterType? exemplarFilterForHistograms;

    internal static void DeactivateMetric(Metric metric)
    {
        if (metric.Active)
        {
            metric.Active = false;

            OpenTelemetrySdkEventSource.Log.MetricInstrumentDeactivated(
                metric.Name,
                metric.MeterName);
        }
    }

    internal AggregationTemporality GetAggregationTemporality(Type instrumentType)
    {
        return this.temporalityFunc(instrumentType);
    }

    internal virtual List<Metric> AddMetricWithNoViews(Instrument instrument)   // <---------------------------met2.4.2
    {
        var metricStreamIdentity = new MetricStreamIdentity(instrument!, metricStreamConfiguration: null);   // <---------------------------met2.4.3

        var exemplarFilter = metricStreamIdentity.IsHistogram
            ? this.exemplarFilterForHistograms ?? this.exemplarFilter
            : this.exemplarFilter;

        lock (this.instrumentCreationLock)
        {
            if (this.TryGetExistingMetric(in metricStreamIdentity, out var existingMetric))
            {
                return new() { existingMetric };
            }

            var index = ++this.metricIndex;
            if (index >= this.metricLimit)
            {
                OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Maximum allowed Metric streams for the provider exceeded.", "Use MeterProviderBuilder.AddView to drop unused instruments. Or use MeterProviderBuilder.SetMaxMetricStreams to configure MeterProvider to allow higher limit.");
                return new();
            }
            else
            {
                Metric? metric = null;
                try
                {
                    metric = new Metric(      // <---------------------------met2.4.4
                        metricStreamIdentity,
                        this.GetAggregationTemporality(metricStreamIdentity.InstrumentType),
                        this.cardinalityLimit,
                        exemplarFilter);
                }
                catch (NotSupportedException nse)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Unsupported instrument. Details: " + nse.Message, "Switch to a supported instrument type.");
                    return new();
                }

                this.instrumentIdentityToMetric[metricStreamIdentity] = metric;
                this.metrics![index] = metric;     // <---------------------------met2.4.5

                this.CreateOrUpdateMetricStreamRegistration(in metricStreamIdentity);

                return new() { metric };   // <---------------------------met2.4.5.
            }
        }
    }

    internal virtual List<Metric> AddMetricWithViews(Instrument instrument, List<MetricStreamConfiguration?> metricStreamConfigs)
    {
        var maxCountMetricsToBeCreated = metricStreamConfigs!.Count;

        var metrics = new List<Metric>(maxCountMetricsToBeCreated);
        lock (this.instrumentCreationLock)
        {
            for (int i = 0; i < maxCountMetricsToBeCreated; i++)
            {
                var metricStreamConfig = metricStreamConfigs[i];
                var metricStreamIdentity = new MetricStreamIdentity(instrument!, metricStreamConfig);

                var exemplarFilter = metricStreamIdentity.IsHistogram
                    ? this.exemplarFilterForHistograms ?? this.exemplarFilter
                    : this.exemplarFilter;

                if (!MeterProviderBuilderSdk.IsValidInstrumentName(metricStreamIdentity.InstrumentName))
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(
                        metricStreamIdentity.InstrumentName,
                        metricStreamIdentity.MeterName,
                        "Metric name is invalid.",
                        "The name must comply with the OpenTelemetry specification.");

                    continue;
                }

                if (this.TryGetExistingMetric(in metricStreamIdentity, out var existingMetric))
                {
                    metrics.Add(existingMetric);
                    continue;
                }

                if (metricStreamConfig == MetricStreamConfiguration.Drop)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "View configuration asks to drop this instrument.", "Modify view configuration to allow this instrument, if desired.");
                    continue;
                }

                var index = ++this.metricIndex;
                if (index >= this.metricLimit)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Maximum allowed Metric streams for the provider exceeded.", "Use MeterProviderBuilder.AddView to drop unused instruments. Or use MeterProviderBuilder.SetMaxMetricStreams to configure MeterProvider to allow higher limit.");
                }
                else
                {
                    Metric metric = new(
                        metricStreamIdentity,
                        this.GetAggregationTemporality(metricStreamIdentity.InstrumentType),
                        metricStreamConfig?.CardinalityLimit ?? this.cardinalityLimit,
                        exemplarFilter,
                        metricStreamConfig?.ExemplarReservoirFactory);

                    this.instrumentIdentityToMetric[metricStreamIdentity] = metric;
                    this.metrics![index] = metric;
                    metrics.Add(metric);

                    this.CreateOrUpdateMetricStreamRegistration(in metricStreamIdentity);
                }
            }

            return metrics;
        }
    }

    internal void ApplyParentProviderSettings(int metricLimit, int cardinalityLimit, ExemplarFilterType? exemplarFilter, ExemplarFilterType? exemplarFilterForHistograms)
    {
        this.metricLimit = metricLimit;
        this.metrics = new Metric[metricLimit];
        this.metricsCurrentBatch = new Metric[metricLimit];
        this.cardinalityLimit = cardinalityLimit;
        this.exemplarFilter = exemplarFilter;
        this.exemplarFilterForHistograms = exemplarFilterForHistograms;
    }

    private bool TryGetExistingMetric(in MetricStreamIdentity metricStreamIdentity, [NotNullWhen(true)] out Metric? existingMetric)
        => this.instrumentIdentityToMetric.TryGetValue(metricStreamIdentity, out existingMetric)
            && existingMetric != null && existingMetric.Active;

    private void CreateOrUpdateMetricStreamRegistration(in MetricStreamIdentity metricStreamIdentity)
    {
        if (!this.metricStreamNames.Add(metricStreamIdentity.MetricStreamName))
        {
            OpenTelemetrySdkEventSource.Log.DuplicateMetricInstrument(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName,
                "Metric instrument has the same name as an existing one but differs by description, unit, or instrument type. Measurements from this instrument will still be exported but may result in conflicts.", "Either change the name of the instrument or use MeterProviderBuilder.AddView to resolve the conflict.");
        }
    }

    private Batch<Metric> GetMetricsBatch()   // <---------------------------------------
    {      
        try
        {
            var indexSnapshot = Math.Min(this.metricIndex, this.metricLimit - 1);
            var target = indexSnapshot + 1;
            int metricCountCurrentBatch = 0;
            for (int i = 0; i < target; i++)
            {
                ref var metric = ref this.metrics![i];
                if (metric != null)
                {
                    int metricPointSize = metric.Snapshot();

                    if (metricPointSize > 0)
                        this.metricsCurrentBatch![metricCountCurrentBatch++] = metric;

                    if (!metric.Active)
                        this.RemoveMetric(ref metric);
                }
            }

            return (metricCountCurrentBatch > 0) ? new Batch<Metric>(this.metricsCurrentBatch!, metricCountCurrentBatch) : default;
        }
        catch (Exception ex)
        {
            return default;
        }
    }

    private void RemoveMetric(ref Metric? metric)
    {             
        this.instrumentIdentityToMetric.TryUpdate(metric.InstrumentIdentity, null, metric);
   
        metric = null;
    }
}
//----------------------------------------Ʌ

//------------------------------------V
public class BaseExportingMetricReader : MetricReader
{
    protected readonly BaseExporter<Metric> exporter;

    private readonly string exportCalledMessage;
    private readonly string exportSucceededMessage;
    private readonly string exportFailedMessage;
    private bool disposed;

    public BaseExportingMetricReader(BaseExporter<Metric> exporter)
    {
        this.exporter = exporter;

        var exporterType = exporter.GetType();
        var attributes = exporterType.GetCustomAttributes(typeof(ExportModesAttribute), true);
        if (attributes.Length > 0)
        {
            var attr = (ExportModesAttribute)attributes[attributes.Length - 1];
            this.SupportedExportModes = attr.Supported;
        }

        if (exporter is IPullMetricExporter pullExporter)
        {
            if (this.SupportedExportModes.HasFlag(ExportModes.Push))
            {
                pullExporter.Collect = this.Collect;
            }
            else
            {
                pullExporter.Collect = (timeoutMilliseconds) =>
                {
                    using (PullMetricScope.Begin())
                    {
                        return this.Collect(timeoutMilliseconds);
                    }
                };
            }
        }

        this.exportCalledMessage = $"{nameof(BaseExportingMetricReader)} calling {this.Exporter}.{nameof(this.Exporter.Export)} method.";
        this.exportSucceededMessage = $"{this.Exporter}.{nameof(this.Exporter.Export)} succeeded.";
        this.exportFailedMessage = $"{this.Exporter}.{nameof(this.Exporter.Export)} failed.";
    }

    internal BaseExporter<Metric> Exporter => this.exporter;

    protected ExportModes SupportedExportModes { get; } = ExportModes.Push | ExportModes.Pull;

    internal override void SetParentProvider(BaseProvider parentProvider)
    {
        base.SetParentProvider(parentProvider);
        this.exporter.ParentProvider = parentProvider;
    }

    internal override bool ProcessMetrics(in Batch<Metric> metrics, int timeoutMilliseconds)  // <-----------------
    {
        try
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportCalledMessage);
            var result = this.exporter.Export(metrics);
            if (result == ExportResult.Success)
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportSucceededMessage);
                return true;
            }
            else
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportFailedMessage);
                return false;
            }
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.ProcessMetrics), ex);
            return false;
        }
    }

    protected override bool OnCollect(int timeoutMilliseconds)
    {
        if (this.SupportedExportModes.HasFlag(ExportModes.Push))
            return base.OnCollect(timeoutMilliseconds);

        if (this.SupportedExportModes.HasFlag(ExportModes.Pull) && PullMetricScope.IsPullAllowed)
            return base.OnCollect(timeoutMilliseconds);

        return false;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = true;

        if (timeoutMilliseconds == Timeout.Infinite)
        {
            result = this.Collect(Timeout.Infinite) && result;
            result = this.exporter.Shutdown(Timeout.Infinite) && result;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            result = this.Collect(timeoutMilliseconds) && result;
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;
            result = this.exporter.Shutdown((int)Math.Max(timeout, 0)) && result;
        }

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                try
                {
                    if (this.exporter is IPullMetricExporter pullExporter)
                        pullExporter.Collect = null;

                    this.exporter.Dispose();
                }
                catch (Exception ex)
                {
                    OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Dispose), ex);
                }
            }
            this.disposed = true;
        }

        base.Dispose(disposing);
    }
}
//------------------------------------Ʌ

//----------------------------------------V
public class PeriodicExportingMetricReader : BaseExportingMetricReader
{
    internal const int DefaultExportIntervalMilliseconds = 60000;
    internal const int DefaultExportTimeoutMilliseconds = 30000;

    internal readonly int ExportIntervalMilliseconds;
    internal readonly int ExportTimeoutMilliseconds;
    private readonly Thread exporterThread;
    private readonly AutoResetEvent exportTrigger = new(false);
    private readonly ManualResetEvent shutdownTrigger = new(false);
    private bool disposed;

    public PeriodicExportingMetricReader(
        BaseExporter<Metric> exporter,
        int exportIntervalMilliseconds = DefaultExportIntervalMilliseconds,
        int exportTimeoutMilliseconds = DefaultExportTimeoutMilliseconds) : base(exporter)
    {
        if ((this.SupportedExportModes & ExportModes.Push) != ExportModes.Push)
            throw new InvalidOperationException($"The '{nameof(exporter)}' does not support '{nameof(ExportModes)}.{nameof(ExportModes.Push)}'");

        this.ExportIntervalMilliseconds = exportIntervalMilliseconds;
        this.ExportTimeoutMilliseconds = exportTimeoutMilliseconds;

        // use Thread rather than Task.Run (because it uses the thread pool) needs a long-running, dedicated background thread, that is not tied to the .NET thread pool 
        this.exporterThread = new Thread(new ThreadStart(this.ExporterProc))   // <-----------------------------
        {
            IsBackground = true,
            Name = $"OpenTelemetry-{nameof(PeriodicExportingMetricReader)}-{exporter.GetType().Name}",
        };

        this.exporterThread.Start();
    }

    private void ExporterProc()  // <----------------------------------------
    {
        int index;
        int timeout;
        var triggers = new WaitHandle[] { this.exportTrigger, this.shutdownTrigger };
        var sw = Stopwatch.StartNew();

        while (true)
        {
            timeout = (int)(this.ExportIntervalMilliseconds - (sw.ElapsedMilliseconds % this.ExportIntervalMilliseconds));

            try
            {
                index = WaitHandle.WaitAny(triggers, timeout);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            switch (index)
            {
                case 0: // export
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because Export was triggered.");
                    this.Collect(this.ExportTimeoutMilliseconds);
                    break;
                case 1: // shutdown
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because Shutdown was triggered.");
                    this.Collect(this.ExportTimeoutMilliseconds); // TODO: do we want to use the shutdown timeout here?
                    return;
                case WaitHandle.WaitTimeout: // timer
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because the export interval has elapsed.");
                    this.Collect(this.ExportTimeoutMilliseconds);   // <------------------------- calls MetricReader.Collect()
                    break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.exportTrigger.Dispose();
                this.shutdownTrigger.Dispose();
            }

            this.disposed = true;
        }

        base.Dispose(disposing);
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = true;

        try
        {
            this.shutdownTrigger.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (timeoutMilliseconds == Timeout.Infinite)
        {
            this.exporterThread.Join();
            result = this.exporter.Shutdown() && result;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            result = this.exporterThread.Join(timeoutMilliseconds) && result;
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;
            result = this.exporter.Shutdown((int)Math.Max(timeout, 0)) && result;
        }

        return result;
    }
}
//----------------------------------------Ʌ
```