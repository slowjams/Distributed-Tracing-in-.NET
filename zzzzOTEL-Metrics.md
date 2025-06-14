## OpenTelemetry Metrics

```C#
//--------------------------------------V
public sealed class OpenTelemetryBuilder : IOpenTelemetryBuilder
{
    internal OpenTelemetryBuilder(IServiceCollection services)
    {
        services.AddOpenTelemetrySharedProviderBuilderServices();

        this.Services = services;
    }

    public IServiceCollection Services { get; }

    public OpenTelemetryBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.ConfigureResource(this, configure);
        return this;
    }

    public OpenTelemetryBuilder WithMetrics()
        => this.WithMetrics(b => { });

    public OpenTelemetryBuilder WithMetrics(Action<MeterProviderBuilder> configure)   // <--------------------------------
    {
        OpenTelemetryBuilderSdkExtensions.WithMetrics(this, configure);
        return this;
    }
    
    // ...
}
//--------------------------------------Ʌ

//---------------------------------------------------V
public static class OpenTelemetryBuilderSdkExtensions
{
    public static IOpenTelemetryBuilder ConfigureResource(this IOpenTelemetryBuilder builder, Action<ResourceBuilder> configure)
    {
        builder.Services.ConfigureOpenTelemetryMeterProvider(
            builder => builder.ConfigureResource(configure));

        builder.Services.ConfigureOpenTelemetryTracerProvider(
            builder => builder.ConfigureResource(configure));

        builder.Services.ConfigureOpenTelemetryLoggerProvider(
            builder => builder.ConfigureResource(configure));

        return builder;
    }

    public static IOpenTelemetryBuilder WithMetrics(this IOpenTelemetryBuilder builder)
        => WithMetrics(builder, b => { });

    public static IOpenTelemetryBuilder WithMetrics(this IOpenTelemetryBuilder builder, Action<MeterProviderBuilder> configure)
    {
        OpenTelemetryMetricsBuilderExtensions.RegisterMetricsListener(
            builder.Services,
            configure);

        return builder;
    }

    // ...
}
//---------------------------------------------------Ʌ

//---------------------------------------------------------V
internal static class OpenTelemetryMetricsBuilderExtensions
{
    public static IMetricsBuilder UseOpenTelemetry(this IMetricsBuilder metricsBuilder)
        => UseOpenTelemetry(metricsBuilder, b => { });

    public static IMetricsBuilder UseOpenTelemetry(this IMetricsBuilder metricsBuilder, Action<MeterProviderBuilder> configure)
    {
        RegisterMetricsListener(metricsBuilder.Services, configure);

        return metricsBuilder;
    }

    internal static void RegisterMetricsListener(IServiceCollection services, Action<MeterProviderBuilder> configure)
    {
        var builder = new MeterProviderBuilderBase(services!);

        services!.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMetricsListener, OpenTelemetryMetricsListener>());

        configure(builder);
    }
}
//---------------------------------------------------------Ʌ

//------------------------------------------------V
internal sealed class OpenTelemetryMetricsListener : IMetricsListener, IDisposable
{
    private readonly MeterProviderSdk meterProviderSdk;
    private IObservableInstrumentsSource? observableInstrumentsSource;

    public OpenTelemetryMetricsListener(MeterProvider meterProvider)
    {
        var meterProviderSdk = meterProvider as MeterProviderSdk;

        this.meterProviderSdk = meterProviderSdk!;

        this.meterProviderSdk.OnCollectObservableInstruments += this.OnCollectObservableInstruments;
    }

    public string Name => "OpenTelemetry";

    public void Dispose()
    {
        this.meterProviderSdk.OnCollectObservableInstruments -= this.OnCollectObservableInstruments;
    }

    public MeasurementHandlers GetMeasurementHandlers()
    {
        return new MeasurementHandlers()
        {
            ByteHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            ShortHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            IntHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            LongHandler = MeterProviderSdk.MeasurementRecordedLong,
            FloatHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedDouble(instrument, value, tags, state),
            DoubleHandler = MeterProviderSdk.MeasurementRecordedDouble,
        };
    }

    public bool InstrumentPublished(Instrument instrument, out object? userState)
    {
        userState = this.meterProviderSdk.InstrumentPublished(instrument, listeningIsManagedExternally: true);
        return userState != null;
    }

    public void MeasurementsCompleted(Instrument instrument, object? userState)
    {
        MeterProviderSdk.MeasurementsCompleted(instrument, userState);
    }

    public void Initialize(IObservableInstrumentsSource source)
    {
        this.observableInstrumentsSource = source;
    }

    private void OnCollectObservableInstruments()
    {
        this.observableInstrumentsSource?.RecordObservableInstruments();
    }
}
//------------------------------------------------Ʌ

//------------------------------------V
internal sealed class MeterProviderSdk : MeterProvider
{
    internal const string ExemplarFilterConfigKey = "OTEL_METRICS_EXEMPLAR_FILTER";
    internal const string ExemplarFilterHistogramsConfigKey = "OTEL_DOTNET_EXPERIMENTAL_METRICS_EXEMPLAR_FILTER_HISTOGRAMS";

    internal readonly IServiceProvider ServiceProvider;
    internal IDisposable? OwnedServiceProvider;
    internal int ShutdownCount;
    internal bool Disposed;
    internal ExemplarFilterType? ExemplarFilter;
    internal ExemplarFilterType? ExemplarFilterForHistograms;
    internal Action? OnCollectObservableInstruments;

    private readonly List<object> instrumentations = [];
    private readonly List<Func<Instrument, MetricStreamConfiguration?>> viewConfigs;
    private readonly Lock collectLock = new();
    private readonly MeterListener listener;
    private readonly Func<Instrument, bool> shouldListenTo = instrument => false;
    private CompositeMetricReader? compositeMetricReader;
    private MetricReader? reader;

    internal MeterProviderSdk(IServiceProvider serviceProvider, bool ownsServiceProvider)
    {
        var state = serviceProvider!.GetRequiredService<MeterProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
        {
            this.OwnedServiceProvider = serviceProvider as IDisposable;
        }

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent("Building MeterProvider.");

        var configureProviderBuilders = serviceProvider!.GetServices<IConfigureMeterProviderBuilder>();
        foreach (var configureProviderBuilder in configureProviderBuilders)
        {
            configureProviderBuilder.ConfigureBuilder(serviceProvider!, state);
        }

        this.ExemplarFilter = state.ExemplarFilter;

        this.ApplySpecificationConfigurationKeys(serviceProvider!.GetRequiredService<IConfiguration>());

        StringBuilder exportersAdded = new StringBuilder();
        StringBuilder instrumentationFactoriesAdded = new StringBuilder();

        var resourceBuilder = state.ResourceBuilder ?? ResourceBuilder.CreateDefault();
        resourceBuilder.ServiceProvider = serviceProvider;
        this.Resource = resourceBuilder.Build();

        this.viewConfigs = state.ViewConfigs;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
            $"MeterProvider configuration: {{MetricLimit={state.MetricLimit}, CardinalityLimit={state.CardinalityLimit}, ExemplarFilter={this.ExemplarFilter}, ExemplarFilterForHistograms={this.ExemplarFilterForHistograms}}}.");

        foreach (var reader in state.Readers)
        {
            reader.SetParentProvider(this);

            reader.ApplyParentProviderSettings(
                state.MetricLimit,
                state.CardinalityLimit,
                this.ExemplarFilter,
                this.ExemplarFilterForHistograms);

            if (this.reader == null)
            {
                this.reader = reader;
            }
            else if (this.reader is CompositeMetricReader compositeReader)
            {
                compositeReader.AddReader(reader);
            }
            else
            {
                this.reader = new CompositeMetricReader([this.reader, reader]);
            }

            if (reader is PeriodicExportingMetricReader periodicExportingMetricReader)
            {
                exportersAdded.Append(periodicExportingMetricReader.Exporter);
                exportersAdded.Append(" (Paired with PeriodicExportingMetricReader exporting at ");
                exportersAdded.Append(periodicExportingMetricReader.ExportIntervalMilliseconds);
                exportersAdded.Append(" milliseconds intervals.)");
                exportersAdded.Append(';');
            }
            else if (reader is BaseExportingMetricReader baseExportingMetricReader)
            {
                exportersAdded.Append(baseExportingMetricReader.Exporter);
                exportersAdded.Append(" (Paired with a MetricReader requiring manual trigger to export.)");
                exportersAdded.Append(';');
            }
        }

        if (exportersAdded.Length != 0)
        {
            exportersAdded.Remove(exportersAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exporters added = \"{exportersAdded}\".");
        }

        this.compositeMetricReader = this.reader as CompositeMetricReader;

        if (state.Instrumentation.Count > 0)
        {
            foreach (var instrumentation in state.Instrumentation)
            {
                if (instrumentation.Instance is not null)
                {
                    this.instrumentations.Add(instrumentation.Instance);
                }

                instrumentationFactoriesAdded.Append(instrumentation.Name);
                instrumentationFactoriesAdded.Append(';');
            }
        }

        if (instrumentationFactoriesAdded.Length != 0)
        {
            instrumentationFactoriesAdded.Remove(instrumentationFactoriesAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Instrumentations added = \"{instrumentationFactoriesAdded}\".");
        }

        // Setup Listener
        if (state.MeterSources.Exists(WildcardHelper.ContainsWildcard))
        {
            var regex = WildcardHelper.GetWildcardRegex(state.MeterSources);
            this.shouldListenTo = instrument => regex.IsMatch(instrument.Meter.Name);
        }
        else if (state.MeterSources.Count > 0)
        {
            var meterSourcesToSubscribe = new HashSet<string>(state.MeterSources, StringComparer.OrdinalIgnoreCase);
            this.shouldListenTo = instrument => meterSourcesToSubscribe.Contains(instrument.Meter.Name);
        }

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Listening to following meters = \"{string.Join(";", state.MeterSources)}\".");

        this.listener = new MeterListener();    // <------------------------------------------------
        var viewConfigCount = this.viewConfigs.Count;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Number of views configured = {viewConfigCount}.");

        this.listener.InstrumentPublished = (instrument, listener) =>
        {
            object? state = this.InstrumentPublished(instrument, listeningIsManagedExternally: false);
            if (state != null)
            {
                listener.EnableMeasurementEvents(instrument, state);
            }
        };

        // Everything double
        this.listener.SetMeasurementEventCallback<double>(MeasurementRecordedDouble);
        this.listener.SetMeasurementEventCallback<float>(static (instrument, value, tags, state) => MeasurementRecordedDouble(instrument, value, tags, state));

        // Everything long
        this.listener.SetMeasurementEventCallback<long>(MeasurementRecordedLong);
        this.listener.SetMeasurementEventCallback<int>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));
        this.listener.SetMeasurementEventCallback<short>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));
        this.listener.SetMeasurementEventCallback<byte>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));

        this.listener.MeasurementsCompleted = MeasurementsCompleted;

        this.listener.Start();

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent("MeterProvider built successfully.");
    }

    internal Resource Resource { get; }

    internal List<object> Instrumentations => this.instrumentations;

    internal MetricReader? Reader => this.reader;

    internal int ViewCount => this.viewConfigs.Count;

    internal static void MeasurementsCompleted(Instrument instrument, object? state)
    {
        if (state is not MetricState metricState)
        {
            return;
        }

        metricState.CompleteMeasurement();
    }

    internal static void MeasurementRecordedLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not MetricState metricState)
        {
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(instrument?.Name ?? "UnknownInstrument", "SDK internal error occurred.", "Contact SDK owners.");
            return;
        }

        metricState.RecordMeasurementLong(value, tags);
    }

    internal static void MeasurementRecordedDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not MetricState metricState)
        {
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(instrument?.Name ?? "UnknownInstrument", "SDK internal error occurred.", "Contact SDK owners.");
            return;
        }

        metricState.RecordMeasurementDouble(value, tags);
    }

    internal object? InstrumentPublished(Instrument instrument, bool listeningIsManagedExternally)
    {
        var listenToInstrumentUsingSdkConfiguration = this.shouldListenTo(instrument);

        if (listeningIsManagedExternally && listenToInstrumentUsingSdkConfiguration)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(
                instrument.Name,
                instrument.Meter.Name,
                "Instrument belongs to a Meter which has been enabled both externally and via a subscription on the provider. External subscription will be ignored in favor of the provider subscription.",
                "Programmatic calls adding meters to the SDK (either by calling AddMeter directly or indirectly through helpers such as 'AddInstrumentation' extensions) are always favored over external registrations. When also using external management (typically IMetricsBuilder or IMetricsListener) remove programmatic calls to the SDK to allow registrations to be added and removed dynamically.");
            return null;
        }
        else if (!listenToInstrumentUsingSdkConfiguration && !listeningIsManagedExternally)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(
                instrument.Name,
                instrument.Meter.Name,
                "Instrument belongs to a Meter not subscribed by the provider.",
                "Use AddMeter to add the Meter to the provider.");
            return null;
        }

        object? state = null;
        var viewConfigCount = this.viewConfigs.Count;

        try
        {
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Started publishing Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\".");

            if (viewConfigCount <= 0)
            {
                if (!MeterProviderBuilderSdk.IsValidInstrumentName(instrument.Name))
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(
                        instrument.Name,
                        instrument.Meter.Name,
                        "Instrument name is invalid.",
                        "The name must comply with the OpenTelemetry specification");
                    return null;
                }

                if (this.reader != null)
                {
                    var metrics = this.reader.AddMetricWithNoViews(instrument);
                    if (metrics.Count == 1)
                    {
                        state = MetricState.BuildForSingleMetric(metrics[0]);
                    }
                    else if (metrics.Count > 0)
                    {
                        state = MetricState.BuildForMetricList(metrics);
                    }
                }
            }
            else
            {
                // Creating list with initial capacity as the maximum
                // possible size, to avoid any array resize/copy internally.
                // There may be excess space wasted, but it'll eligible for
                // GC right after this method.
                var metricStreamConfigs = new List<MetricStreamConfiguration?>(viewConfigCount);
                for (var i = 0; i < viewConfigCount; ++i)
                {
                    var viewConfig = this.viewConfigs[i];
                    MetricStreamConfiguration? metricStreamConfig = null;

                    try
                    {
                        metricStreamConfig = viewConfig(instrument);

                        // The SDK provides some static MetricStreamConfigurations.
                        // For example, the Drop configuration. The static ViewId
                        // should not be changed for these configurations.
                        if (metricStreamConfig != null && !metricStreamConfig.ViewId.HasValue)
                        {
                            metricStreamConfig.ViewId = i;
                        }

                        if (metricStreamConfig is HistogramConfiguration
                            && instrument.GetType().GetGenericTypeDefinition() != typeof(Histogram<>))
                        {
                            metricStreamConfig = null;

                            OpenTelemetrySdkEventSource.Log.MetricViewIgnored(
                                instrument.Name,
                                instrument.Meter.Name,
                                "The current SDK does not allow aggregating non-Histogram instruments as Histograms.",
                                "Fix the view configuration.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OpenTelemetrySdkEventSource.Log.MetricViewIgnored(instrument.Name, instrument.Meter.Name, ex.Message, "Fix the view configuration.");
                    }

                    if (metricStreamConfig != null)
                    {
                        metricStreamConfigs.Add(metricStreamConfig);
                    }
                }

                if (metricStreamConfigs.Count == 0)
                {
                    // No views matched. Add null
                    // which will apply defaults.
                    // Users can turn off this default
                    // by adding a view like below as the last view.
                    // .AddView(instrumentName: "*", MetricStreamConfiguration.Drop)
                    metricStreamConfigs.Add(null);
                }

                if (this.reader != null)
                {
                    var metrics = this.reader.AddMetricWithViews(instrument, metricStreamConfigs);
                    if (metrics.Count == 1)
                    {
                        state = MetricState.BuildForSingleMetric(metrics[0]);
                    }
                    else if (metrics.Count > 0)
                    {
                        state = MetricState.BuildForMetricList(metrics);
                    }
                }
            }

            if (state != null)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Measurements for Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\" will be processed and aggregated by the SDK.");
                return state;
            }
            else
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Measurements for Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\" will be dropped by the SDK.");
                return null;
            }
        }
        catch (Exception)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name, "SDK internal error occurred.", "Contact SDK owners.");
            return null;
        }
    }

    internal void CollectObservableInstruments()
    {
        lock (this.collectLock)
        {
            // Record all observable instruments
            try
            {
                this.listener.RecordObservableInstruments();

                this.OnCollectObservableInstruments?.Invoke();
            }
            catch (Exception exception)
            {
                OpenTelemetrySdkEventSource.Log.MetricObserverCallbackException(exception);
            }
        }
    }

    internal bool OnForceFlush(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.OnForceFlush)} called with {nameof(timeoutMilliseconds)} = {timeoutMilliseconds}.");
        return this.reader?.Collect(timeoutMilliseconds) ?? true;
    }

    internal bool OnShutdown(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.OnShutdown)} called with {nameof(timeoutMilliseconds)} = {timeoutMilliseconds}.");
        return this.reader?.Shutdown(timeoutMilliseconds) ?? true;
    }

    protected override void Dispose(bool disposing)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.Dispose)} started.");
        if (!this.Disposed)
        {
            if (disposing)
            {
                foreach (var item in this.instrumentations)
                {
                    (item as IDisposable)?.Dispose();
                }

                this.instrumentations.Clear();

                // Wait for up to 5 seconds grace period
                this.reader?.Shutdown(5000);
                this.reader?.Dispose();
                this.reader = null;

                this.compositeMetricReader?.Dispose();
                this.compositeMetricReader = null;

                this.listener?.Dispose();

                this.OwnedServiceProvider?.Dispose();
                this.OwnedServiceProvider = null;
            }

            this.Disposed = true;
            OpenTelemetrySdkEventSource.Log.ProviderDisposed(nameof(MeterProvider));
        }

        base.Dispose(disposing);
    }

    private void ApplySpecificationConfigurationKeys(IConfiguration configuration)
    {
        var hasProgrammaticExemplarFilterValue = this.ExemplarFilter.HasValue;

        if (configuration.TryGetStringValue(ExemplarFilterConfigKey, out var configValue))
        {
            if (hasProgrammaticExemplarFilterValue)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
                    $"Exemplar filter configuration value '{configValue}' has been ignored because a value '{this.ExemplarFilter}' was set programmatically.");
                return;
            }

            if (!TryParseExemplarFilterFromConfigurationValue(configValue, out var exemplarFilter))
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter configuration was found but the value '{configValue}' is invalid and will be ignored.");
                return;
            }

            this.ExemplarFilter = exemplarFilter;

            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter set to '{exemplarFilter}' from configuration.");
        }

        if (configuration.TryGetStringValue(ExemplarFilterHistogramsConfigKey, out configValue))
        {
            if (hasProgrammaticExemplarFilterValue)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
                    $"Exemplar filter histogram configuration value '{configValue}' has been ignored because a value '{this.ExemplarFilter}' was set programmatically.");
                return;
            }

            if (!TryParseExemplarFilterFromConfigurationValue(configValue, out var exemplarFilter))
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter histogram configuration was found but the value '{configValue}' is invalid and will be ignored.");
                return;
            }

            this.ExemplarFilterForHistograms = exemplarFilter;

            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter for histograms set to '{exemplarFilter}' from configuration.");
        }

        static bool TryParseExemplarFilterFromConfigurationValue(string? configValue, out ExemplarFilterType? exemplarFilter)
        {
            if (string.Equals("always_off", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.AlwaysOff;
                return true;
            }

            if (string.Equals("always_on", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.AlwaysOn;
                return true;
            }

            if (string.Equals("trace_based", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.TraceBased;
                return true;
            }

            exemplarFilter = null;
            return false;
        }
    }
}
//------------------------------------Ʌ

//-------------------------------------------V
internal sealed class MeterProviderBuilderSdk : MeterProviderBuilder, IMeterProviderBuilder
{
    public const int DefaultMetricLimit = 1000;
    public const int DefaultCardinalityLimit = 2000;
    private const string DefaultInstrumentationVersion = "1.0.0.0";

    private readonly IServiceProvider serviceProvider;
    private MeterProviderSdk? meterProvider;

    public MeterProviderBuilderSdk(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public static Regex InstrumentNameRegex { get; set; } = new(@"^[a-z][a-z0-9-._/]{0,254}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<InstrumentationRegistration> Instrumentation { get; } = new();

    public ResourceBuilder? ResourceBuilder { get; private set; }

    public ExemplarFilterType? ExemplarFilter { get; private set; }

    public MeterProvider? Provider => this.meterProvider;

    public List<MetricReader> Readers { get; } = new();

    public List<string> MeterSources { get; } = new();

    public List<Func<Instrument, MetricStreamConfiguration?>> ViewConfigs { get; } = new();

    public int MetricLimit { get; private set; } = DefaultMetricLimit;

    public int CardinalityLimit { get; private set; } = DefaultCardinalityLimit;

    public static bool IsValidInstrumentName(string instrumentName)
    {
        if (string.IsNullOrWhiteSpace(instrumentName))
        {
            return false;
        }

        return InstrumentNameRegex.IsMatch(instrumentName);
    }

    public static bool IsValidViewName(string customViewName)
    {
        // Only validate the view name in case it's not null. In case it's null, the view name will be the instrument name as per the spec.
        if (customViewName == null)
        {
            return true;
        }

        return InstrumentNameRegex.IsMatch(customViewName);
    }

    public void RegisterProvider(MeterProviderSdk meterProvider)
    {
        if (this.meterProvider != null)
        {
            throw new NotSupportedException("MeterProvider cannot be accessed while build is executing.");
        }

        this.meterProvider = meterProvider;
    }

    public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        return this.AddInstrumentation(
            typeof(TInstrumentation).Name,
            typeof(TInstrumentation).Assembly.GetName().Version?.ToString() ?? DefaultInstrumentationVersion,
            instrumentationFactory!());
    }

    public MeterProviderBuilder AddInstrumentation(string instrumentationName, string instrumentationVersion, object? instrumentation)
    {
        this.Instrumentation.Add(new InstrumentationRegistration(instrumentationName, instrumentationVersion, instrumentation));

        return this;
    }

    public MeterProviderBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        var resourceBuilder = this.ResourceBuilder ??= ResourceBuilder.CreateDefault();

        configure!(resourceBuilder);

        return this;
    }

    public MeterProviderBuilder SetResourceBuilder(ResourceBuilder resourceBuilder)
    {
        this.ResourceBuilder = resourceBuilder;

        return this;
    }

    public MeterProviderBuilder SetExemplarFilter(ExemplarFilterType exemplarFilter)
    {
        this.ExemplarFilter = exemplarFilter;

        return this;
    }

    public override MeterProviderBuilder AddMeter(params string[] names)
    {
        foreach (var name in names!)
        {
            this.MeterSources.Add(name);
        }

        return this;
    }

    public MeterProviderBuilder AddReader(MetricReader reader)
    {
        this.Readers.Add(reader!);

        return this;
    }

    public MeterProviderBuilder AddView(Func<Instrument, MetricStreamConfiguration?> viewConfig)
    {
        this.ViewConfigs.Add(viewConfig!);

        return this;
    }

    public MeterProviderBuilder SetMetricLimit(int metricLimit)
    {
        this.MetricLimit = metricLimit;

        return this;
    }

    public MeterProviderBuilder SetDefaultCardinalityLimit(int cardinalityLimit)
    {
        this.CardinalityLimit = cardinalityLimit;

        return this;
    }

    public MeterProviderBuilder ConfigureBuilder(Action<IServiceProvider, MeterProviderBuilder> configure)
    {
        configure!(this.serviceProvider, this);

        return this;
    }

    public MeterProviderBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        throw new NotSupportedException("Services cannot be configured after ServiceProvider has been created.");
    }

    MeterProviderBuilder IDeferredMeterProviderBuilder.Configure(Action<IServiceProvider, MeterProviderBuilder> configure)
        => this.ConfigureBuilder(configure);

    internal readonly struct InstrumentationRegistration
    {
        public readonly string Name;
        public readonly string Version;
        public readonly object? Instance;

        internal InstrumentationRegistration(string name, string version, object? instance)
        {
            this.Name = name;
            this.Version = version;
            this.Instance = instance;
        }
    }
}
//-------------------------------------------Ʌ

//------------------------------------V
public class MetricStreamConfiguration
{
    private string? name;

    private int? cardinalityLimit;

    public static MetricStreamConfiguration Drop { get; } = new MetricStreamConfiguration { ViewId = -1 };

    public string? Name
    {
        get => this.name;
        set
        {
            if (value != null && !MeterProviderBuilderSdk.IsValidViewName(value))
            {
                throw new ArgumentException($"Custom view name {value} is invalid.", nameof(value));
            }

            this.name = value;
        }
    }

    public string? Description { get; set; }

    public string[]? TagKeys
    {
        get => this.CopiedTagKeys?.ToArray();
        set => this.CopiedTagKeys = value?.ToArray();
    }

    public int? CardinalityLimit
    {
        get => this.cardinalityLimit;
        set
        {
            if (value != null)
            {
                Guard.ThrowIfOutOfRange(value.Value, min: 1, max: int.MaxValue);
            }

            this.cardinalityLimit = value;
        }
    }

    public Func<ExemplarReservoir?>? ExemplarReservoirFactory { get; set; }

    internal string[]? CopiedTagKeys { get; private set; }

    internal int? ViewId { get; set; }
}
//------------------------------------Ʌ
```