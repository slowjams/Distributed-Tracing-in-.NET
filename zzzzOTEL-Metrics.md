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

//---------------------V
public static class Sdk
{
    static Sdk()
    {
        Propagators.DefaultTextMapPropagator = new CompositeTextMapPropagator(new TextMapPropagator[]
        {
            new TraceContextPropagator(),
            new BaggagePropagator(),
        });

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        SelfDiagnostics.EnsureInitialized();

        var sdkAssembly = typeof(Sdk).Assembly;
        InformationalVersion = sdkAssembly.GetPackageVersion();
    }

    public static bool SuppressInstrumentation => SuppressInstrumentationScope.IsSuppressed;

    internal static string InformationalVersion { get; }

    public static void SetDefaultTextMapPropagator(TextMapPropagator textMapPropagator)
    {
        Guard.ThrowIfNull(textMapPropagator);

        Propagators.DefaultTextMapPropagator = textMapPropagator;
    }

    public static MeterProviderBuilder CreateMeterProviderBuilder()
    {
        return new MeterProviderBuilderBase();
    }

    public static TracerProviderBuilder CreateTracerProviderBuilder()
    {
        return new TracerProviderBuilderBase();
    }

    public static LoggerProviderBuilder CreateLoggerProviderBuilder()
    {
        return new LoggerProviderBuilderBase();
    }
}
//---------------------Ʌ

//-----------------------------------V
public class MeterProviderBuilderBase : MeterProviderBuilder, IMeterProviderBuilder
{
    private readonly bool allowBuild;
    private readonly MeterProviderServiceCollectionBuilder innerBuilder;

    public MeterProviderBuilderBase()
    {
        var services = new ServiceCollection();

        services
            .AddOpenTelemetrySharedProviderBuilderServices()
            .AddOpenTelemetryMeterProviderBuilderServices()
            .TryAddSingleton<MeterProvider>(
                sp => throw new NotSupportedException("Self-contained MeterProvider cannot be accessed using the application IServiceProvider call Build instead."));

        this.innerBuilder = new MeterProviderServiceCollectionBuilder(services);

        this.allowBuild = true;
    }

    internal MeterProviderBuilderBase(IServiceCollection services)
    {
        services
            .AddOpenTelemetryMeterProviderBuilderServices()
            .TryAddSingleton<MeterProvider>(sp => new MeterProviderSdk(sp, ownsServiceProvider: false));

        this.innerBuilder = new MeterProviderServiceCollectionBuilder(services);

        this.allowBuild = false;
    }

    MeterProvider? IMeterProviderBuilder.Provider => null;

    public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        this.innerBuilder.AddInstrumentation(instrumentationFactory);

        return this;
    }

    public override MeterProviderBuilder AddMeter(params string[] names)
    {
        this.innerBuilder.AddMeter(names);

        return this;
    }

    MeterProviderBuilder IMeterProviderBuilder.ConfigureServices(Action<IServiceCollection> configure)
    {
        this.innerBuilder.ConfigureServices(configure);

        return this;
    }

    MeterProviderBuilder IDeferredMeterProviderBuilder.Configure(Action<IServiceProvider, MeterProviderBuilder> configure)
    {
        this.innerBuilder.ConfigureBuilder(configure);

        return this;
    }

    internal MeterProvider InvokeBuild()
        => this.Build();

    protected MeterProvider Build()
    {
        if (!this.allowBuild)
        {
            throw new NotSupportedException("A MeterProviderBuilder bound to external service cannot be built directly. Access the MeterProvider using the application IServiceProvider instead.");
        }

        var services = this.innerBuilder.Services ?? throw new NotSupportedException("MeterProviderBuilder build method cannot be called multiple times.");

        this.innerBuilder.Services = null;

        bool validateScopes = true;  // false if it is not DEBUG

        var serviceProvider = services.BuildServiceProvider(validateScopes);

        return new MeterProviderSdk(serviceProvider, ownsServiceProvider: true);
    }
}
//-----------------------------------Ʌ

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

    internal MeterProviderSdk(
        IServiceProvider serviceProvider,
        bool ownsServiceProvider)
    {
        Debug.Assert(serviceProvider != null, "serviceProvider was null");

        MeterProviderBuilderSdk state = serviceProvider!.GetRequiredService<MeterProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
            this.OwnedServiceProvider = serviceProvider as IDisposable;

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

        this.listener = new MeterListener();   // <-----------------------------!!
        var viewConfigCount = this.viewConfigs.Count;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Number of views configured = {viewConfigCount}.");

        this.listener.InstrumentPublished = (instrument, listener) =>   // <-----------------------mt0.3
        {
            object? state = this.InstrumentPublished(instrument, listeningIsManagedExternally: false);  // <-------------mt0.4, state is MetricState
            if (state != null)
            {
                listener.EnableMeasurementEvents(instrument, state);   // <------------------------
            }
        };

        // <------------------------------------------------------------------------smec
        // Everything double
        this.listener.SetMeasurementEventCallback<double>(MeasurementRecordedDouble);
        this.listener.SetMeasurementEventCallback<float>(static (instrument, value, tags, state) => MeasurementRecordedDouble(instrument, value, tags, state));

        // Everything long
        this.listener.SetMeasurementEventCallback<long>(MeasurementRecordedLong);
        this.listener.SetMeasurementEventCallback<int>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));  // <-------mt2.1
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
            return;

        metricState.CompleteMeasurement();
    }

    internal static void MeasurementRecordedLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)  // <-------mt2.2
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

    internal object? InstrumentPublished(Instrument instrument, bool listeningIsManagedExternally)   // <-------------------------mt0.5
    {
        var listenToInstrumentUsingSdkConfiguration = this.shouldListenTo(instrument);

        if (listeningIsManagedExternally && listenToInstrumentUsingSdkConfiguration)   // listeningIsManagedExternally is default by false
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name,
                "Instrument belongs to a Meter which has been enabled both externally and via a subscription on the provider. External subscription will be ignored in favor of the provider subscription.", "Programmatic calls adding meters to the SDK (either by calling Add*Meter directly or indirectly through helpers such as 'AddInstrumentation' extensions) are always favored over external registrations. When also using external management (typically IMetricsBuilder or IMetricsListener) remove programmatic calls to the SDK to allow registrations to be added and removed dynamically.");
            
            return null;
        }
        else if (!listenToInstrumentUsingSdkConfiguration && !listeningIsManagedExternally)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name,
                "Instrument belongs to a Meter not subscribed by the provider.", "Use Add*Meter to add the Meter to the provider.");
            
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

    public override MeterProviderBuilder AddMeter(params string[] names)  // <-------------------------------1
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

//-------------------------------V
internal sealed class MetricState
{
    public readonly Action CompleteMeasurement;

    public readonly RecordMeasurementAction<long> RecordMeasurementLong;
    public readonly RecordMeasurementAction<double> RecordMeasurementDouble;

    private MetricState(Action completeMeasurement, RecordMeasurementAction<long> recordMeasurementLong, RecordMeasurementAction<double> recordMeasurementDouble)
    {
        this.CompleteMeasurement = completeMeasurement;
        this.RecordMeasurementLong = recordMeasurementLong;
        this.RecordMeasurementDouble = recordMeasurementDouble;
    }

    internal delegate void RecordMeasurementAction<T>(T value, ReadOnlySpan<KeyValuePair<string, object?>> tags);

    public static MetricState BuildForSingleMetric(Metric metric)
    {
        return new(
            completeMeasurement: () => MetricReader.DeactivateMetric(metric!),
            recordMeasurementLong: metric!.UpdateLong,
            recordMeasurementDouble: metric!.UpdateDouble);
    }

    public static MetricState BuildForMetricList(List<Metric> metrics)
    {
        var metricsArray = metrics!.ToArray();

        return new(
            completeMeasurement: () =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    MetricReader.DeactivateMetric(metricsArray[i]);
                }
            },
            recordMeasurementLong: (v, t) =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    metricsArray[i].UpdateLong(v, t);
                }
            },
            recordMeasurementDouble: (v, t) =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    metricsArray[i].UpdateDouble(v, t);
                }
            });
    }
}
//-------------------------------Ʌ

//------------------------V
public sealed class Metric
{
    internal const int DefaultExponentialHistogramMaxBuckets = 160;

    internal const int DefaultExponentialHistogramMaxScale = 20;

    internal static readonly double[] DefaultHistogramBounds = new double[] { 0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000 };

    // Short default histogram bounds. Based on the recommended semantic convention values for http.server.request.duration.
    internal static readonly double[] DefaultHistogramBoundsShortSeconds = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 };
    internal static readonly HashSet<(string, string)> DefaultHistogramBoundShortMappings = new HashSet<(string, string)>
    {
        ("Microsoft.AspNetCore.Hosting", "http.server.request.duration"),
        ("Microsoft.AspNetCore.RateLimiting", "aspnetcore.rate_limiting.request.time_in_queue"),
        ("Microsoft.AspNetCore.RateLimiting", "aspnetcore.rate_limiting.request_lease.duration"),
        ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.tls_handshake.duration"),
        ("OpenTelemetry.Instrumentation.AspNet", "http.server.request.duration"),
        ("OpenTelemetry.Instrumentation.AspNetCore", "http.server.request.duration"),
        ("OpenTelemetry.Instrumentation.Http", "http.client.request.duration"),
        ("System.Net.Http", "http.client.request.duration"),
        ("System.Net.Http", "http.client.request.time_in_queue"),
        ("System.Net.NameResolution", "dns.lookup.duration"),
    }

    // Long default histogram bounds. Not based on a standard. May change in the future.
    internal static readonly double[] DefaultHistogramBoundsLongSeconds = new double[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300 };
    internal static readonly HashSet<(string, string)> DefaultHistogramBoundLongMappings = new HashSet<(string, string)>
    {
        ("Microsoft.AspNetCore.Http.Connections", "signalr.server.connection.duration"),
        ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.connection.duration"),
        ("System.Net.Http", "http.client.connection.duration"),
    };

    internal readonly AggregatorStore AggregatorStore;

    internal Metric(
        MetricStreamIdentity instrumentIdentity,
        AggregationTemporality temporality,
        int cardinalityLimit,
        ExemplarFilterType? exemplarFilter = null,
        Func<ExemplarReservoir?>? exemplarReservoirFactory = null)
    {
        this.InstrumentIdentity = instrumentIdentity;

        AggregationType aggType;
        if (instrumentIdentity.InstrumentType == typeof(ObservableCounter<long>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<int>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<short>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<byte>))
        {
            aggType = AggregationType.LongSumIncomingCumulative;
            this.MetricType = MetricType.LongSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Counter<long>)
            || instrumentIdentity.InstrumentType == typeof(Counter<int>)
            || instrumentIdentity.InstrumentType == typeof(Counter<short>)
            || instrumentIdentity.InstrumentType == typeof(Counter<byte>))
        {
            aggType = AggregationType.LongSumIncomingDelta;
            this.MetricType = MetricType.LongSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Counter<double>)
            || instrumentIdentity.InstrumentType == typeof(Counter<float>))
        {
            aggType = AggregationType.DoubleSumIncomingDelta;
            this.MetricType = MetricType.DoubleSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(ObservableCounter<double>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<float>))
        {
            aggType = AggregationType.DoubleSumIncomingCumulative;
            this.MetricType = MetricType.DoubleSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<long>)
            || instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<int>)
            || instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<short>)
            || instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<byte>))
        {
            aggType = AggregationType.LongSumIncomingCumulative;
            this.MetricType = MetricType.LongSumNonMonotonic;
        }
        else if (instrumentIdentity.InstrumentType == typeof(UpDownCounter<long>)
            || instrumentIdentity.InstrumentType == typeof(UpDownCounter<int>)
            || instrumentIdentity.InstrumentType == typeof(UpDownCounter<short>)
            || instrumentIdentity.InstrumentType == typeof(UpDownCounter<byte>))
        {
            aggType = AggregationType.LongSumIncomingDelta;
            this.MetricType = MetricType.LongSumNonMonotonic;
        }
        else if (instrumentIdentity.InstrumentType == typeof(UpDownCounter<double>)
            || instrumentIdentity.InstrumentType == typeof(UpDownCounter<float>))
        {
            aggType = AggregationType.DoubleSumIncomingDelta;
            this.MetricType = MetricType.DoubleSumNonMonotonic;
        }
        else if (instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<double>)
            || instrumentIdentity.InstrumentType == typeof(ObservableUpDownCounter<float>))
        {
            aggType = AggregationType.DoubleSumIncomingCumulative;
            this.MetricType = MetricType.DoubleSumNonMonotonic;
        }
        else if (instrumentIdentity.InstrumentType == typeof(ObservableGauge<double>)
            || instrumentIdentity.InstrumentType == typeof(ObservableGauge<float>))
        {
            aggType = AggregationType.DoubleGauge;
            this.MetricType = MetricType.DoubleGauge;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Gauge<double>)
            || instrumentIdentity.InstrumentType == typeof(Gauge<float>))
        {
            aggType = AggregationType.DoubleGauge;
            this.MetricType = MetricType.DoubleGauge;
        }
        else if (instrumentIdentity.InstrumentType == typeof(ObservableGauge<long>)
            || instrumentIdentity.InstrumentType == typeof(ObservableGauge<int>)
            || instrumentIdentity.InstrumentType == typeof(ObservableGauge<short>)
            || instrumentIdentity.InstrumentType == typeof(ObservableGauge<byte>))
        {
            aggType = AggregationType.LongGauge;
            this.MetricType = MetricType.LongGauge;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Gauge<long>)
            || instrumentIdentity.InstrumentType == typeof(Gauge<int>)
            || instrumentIdentity.InstrumentType == typeof(Gauge<short>)
            || instrumentIdentity.InstrumentType == typeof(Gauge<byte>))
        {
            aggType = AggregationType.LongGauge;
            this.MetricType = MetricType.LongGauge;
        }
        else if (instrumentIdentity.IsHistogram)
        {
            var explicitBucketBounds = instrumentIdentity.HistogramBucketBounds;
            var exponentialMaxSize = instrumentIdentity.ExponentialHistogramMaxSize;
            var histogramRecordMinMax = instrumentIdentity.HistogramRecordMinMax;

            this.MetricType = exponentialMaxSize == 0
                ? MetricType.Histogram
                : MetricType.ExponentialHistogram;

            if (this.MetricType == MetricType.Histogram)
            {
                aggType = explicitBucketBounds != null && explicitBucketBounds.Length == 0
                    ? (histogramRecordMinMax ? AggregationType.HistogramWithMinMax : AggregationType.Histogram)
                    : (histogramRecordMinMax ? AggregationType.HistogramWithMinMaxBuckets : AggregationType.HistogramWithBuckets);
            }
            else
            {
                aggType = histogramRecordMinMax ? AggregationType.Base2ExponentialHistogramWithMinMax : AggregationType.Base2ExponentialHistogram;
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported Instrument Type: {instrumentIdentity.InstrumentType.FullName}");
        }

        this.AggregatorStore = new AggregatorStore(
            instrumentIdentity,
            aggType,
            temporality,
            cardinalityLimit,
            exemplarFilter,
            exemplarReservoirFactory);
        this.Temporality = temporality;
    }

    public MetricType MetricType { get; private set; }

    public AggregationTemporality Temporality { get; private set; }

    public string Name => this.InstrumentIdentity.InstrumentName;

    public string Description => this.InstrumentIdentity.Description;

    public string Unit => this.InstrumentIdentity.Unit;

    public string MeterName => this.InstrumentIdentity.MeterName;

    public string MeterVersion => this.InstrumentIdentity.MeterVersion;

    public IEnumerable<KeyValuePair<string, object?>>? MeterTags => this.InstrumentIdentity.MeterTags?.KeyValuePairs;

    internal MetricStreamIdentity InstrumentIdentity { get; private set; }

    internal bool Active { get; set; } = true;

    public MetricPointsAccessor GetMetricPoints()  => this.AggregatorStore.GetMetricPoints();

    internal void UpdateLong(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags);

    internal void UpdateDouble(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags);

    internal int Snapshot() => this.AggregatorStore.Snapshot();
}
//------------------------Ʌ

//-------------------------------------------V
internal readonly struct MetricStreamIdentity : IEquatable<MetricStreamIdentity>
{
    private static readonly StringArrayEqualityComparer StringArrayComparer = new();
    private readonly int hashCode;

    public MetricStreamIdentity(Instrument instrument, MetricStreamConfiguration? metricStreamConfiguration)
    {
        this.MeterName = instrument.Meter.Name;
        this.MeterVersion = instrument.Meter.Version ?? string.Empty;
        this.MeterTags = instrument.Meter.Tags != null ? new Tags(instrument.Meter.Tags.ToArray()) : null;
        this.InstrumentName = metricStreamConfiguration?.Name ?? instrument.Name;
        this.Unit = instrument.Unit ?? string.Empty;
        this.Description = metricStreamConfiguration?.Description ?? instrument.Description ?? string.Empty;
        this.InstrumentType = instrument.GetType();
        this.ViewId = metricStreamConfiguration?.ViewId;
        this.MetricStreamName = $"{this.MeterName}.{this.MeterVersion}.{this.InstrumentName}";
        this.TagKeys = metricStreamConfiguration?.CopiedTagKeys;
        this.HistogramBucketBounds = GetExplicitBucketHistogramBounds(instrument, metricStreamConfiguration);
        this.ExponentialHistogramMaxSize = (metricStreamConfiguration as Base2ExponentialBucketHistogramConfiguration)?.MaxSize ?? 0;
        this.ExponentialHistogramMaxScale = (metricStreamConfiguration as Base2ExponentialBucketHistogramConfiguration)?.MaxScale ?? 0;
        this.HistogramRecordMinMax = (metricStreamConfiguration as HistogramConfiguration)?.RecordMinMax ?? true;


        HashCode hashCode = default;
        hashCode.Add(this.InstrumentType);
        hashCode.Add(this.MeterName);
        hashCode.Add(this.MeterVersion);
        hashCode.Add(this.MeterTags);
        hashCode.Add(this.InstrumentName);
        hashCode.Add(this.HistogramRecordMinMax);
        hashCode.Add(this.Unit);
        hashCode.Add(this.Description);
        hashCode.Add(this.ViewId);
        hashCode.Add(this.TagKeys!, StringArrayComparer);
        hashCode.Add(this.ExponentialHistogramMaxSize);
        hashCode.Add(this.ExponentialHistogramMaxScale);
        if (this.HistogramBucketBounds != null)
        {
            for (var i = 0; i < this.HistogramBucketBounds.Length; ++i)
                hashCode.Add(this.HistogramBucketBounds[i]);
        }

        var hash = hashCode.ToHashCode();

        this.hashCode = hash;
    }

    public string MeterName { get; }

    public string MeterVersion { get; }

    public Tags? MeterTags { get; }

    public string InstrumentName { get; }

    public string Unit { get; }

    public string Description { get; }

    public Type InstrumentType { get; }

    public int? ViewId { get; }

    public string MetricStreamName { get; }

    public string[]? TagKeys { get; }

    public double[]? HistogramBucketBounds { get; }

    public int ExponentialHistogramMaxSize { get; }

    public int ExponentialHistogramMaxScale { get; }

    public bool HistogramRecordMinMax { get; }

    public bool IsHistogram =>
        this.InstrumentType == typeof(Histogram<long>)
        || this.InstrumentType == typeof(Histogram<int>)
        || this.InstrumentType == typeof(Histogram<short>)
        || this.InstrumentType == typeof(Histogram<byte>)
        || this.InstrumentType == typeof(Histogram<float>)
        || this.InstrumentType == typeof(Histogram<double>);

    public static bool operator ==(MetricStreamIdentity metricIdentity1, MetricStreamIdentity metricIdentity2) => metricIdentity1.Equals(metricIdentity2);

    public static bool operator !=(MetricStreamIdentity metricIdentity1, MetricStreamIdentity metricIdentity2) => !metricIdentity1.Equals(metricIdentity2);

    public override readonly bool Equals(object? obj)
    {
        return obj is MetricStreamIdentity other && this.Equals(other);
    }

    public bool Equals(MetricStreamIdentity other)
    {
        return this.InstrumentType == other.InstrumentType
            && this.MeterName == other.MeterName
            && this.MeterVersion == other.MeterVersion
            && this.InstrumentName == other.InstrumentName
            && this.Unit == other.Unit
            && this.Description == other.Description
            && this.ViewId == other.ViewId
            && this.MeterTags == other.MeterTags
            && this.HistogramRecordMinMax == other.HistogramRecordMinMax
            && this.ExponentialHistogramMaxSize == other.ExponentialHistogramMaxSize
            && this.ExponentialHistogramMaxScale == other.ExponentialHistogramMaxScale
            && StringArrayComparer.Equals(this.TagKeys, other.TagKeys)
            && HistogramBoundsEqual(this.HistogramBucketBounds, other.HistogramBucketBounds);
    }

    public override readonly int GetHashCode() => this.hashCode;

    private static double[]? GetExplicitBucketHistogramBounds(Instrument instrument, MetricStreamConfiguration? metricStreamConfiguration)
    {
        if (metricStreamConfiguration is ExplicitBucketHistogramConfiguration explicitBucketHistogramConfiguration
            && explicitBucketHistogramConfiguration.CopiedBoundaries != null)
        {
            return explicitBucketHistogramConfiguration.CopiedBoundaries;
        }

        return instrument switch
        {
            Histogram<long> longHistogram => GetExplicitBucketHistogramBoundsFromAdvice(longHistogram),
            Histogram<int> intHistogram => GetExplicitBucketHistogramBoundsFromAdvice(intHistogram),
            Histogram<short> shortHistogram => GetExplicitBucketHistogramBoundsFromAdvice(shortHistogram),
            Histogram<byte> byteHistogram => GetExplicitBucketHistogramBoundsFromAdvice(byteHistogram),
            Histogram<float> floatHistogram => GetExplicitBucketHistogramBoundsFromAdvice(floatHistogram),
            Histogram<double> doubleHistogram => GetExplicitBucketHistogramBoundsFromAdvice(doubleHistogram),
            _ => null,
        };
    }

    private static double[]? GetExplicitBucketHistogramBoundsFromAdvice<T>(Histogram<T> histogram) where T : struct
    {
        var adviceExplicitBucketBoundaries = histogram.Advice?.HistogramBucketBoundaries;
        if (adviceExplicitBucketBoundaries == null)
        {
            return null;
        }

        if (typeof(T) == typeof(double))
        {
            return ((IReadOnlyList<double>)adviceExplicitBucketBoundaries).ToArray();
        }
        else
        {
            double[] explicitBucketBoundaries = new double[adviceExplicitBucketBoundaries.Count];

            for (int i = 0; i < adviceExplicitBucketBoundaries.Count; i++)
            {
                explicitBucketBoundaries[i] = Convert.ToDouble(adviceExplicitBucketBoundaries[i], CultureInfo.InvariantCulture);
            }

            return explicitBucketBoundaries;
        }
    }

    private static bool HistogramBoundsEqual(double[]? bounds1, double[]? bounds2)
    {
        if (ReferenceEquals(bounds1, bounds2))
            return true;

        if (ReferenceEquals(bounds1, null) || ReferenceEquals(bounds2, null))
            return false;

        var len1 = bounds1.Length;

        if (len1 != bounds2.Length)
            return false;

        for (int i = 0; i < len1; i++)
        {
            if (!bounds1[i].Equals(bounds2[i]))
                return false;
        }

        return true;
    }
}
//-------------------------------------------Ʌ

//-----------------------------V
internal enum MetricPointStatus
{
    NoCollectPending,
    CollectPending,
}
//-----------------------------Ʌ

//-----------------------V
public struct MetricPoint
{
    internal int ReferenceCount;

    private const int DefaultSimpleReservoirPoolSize = 1;

    private readonly AggregatorStore aggregatorStore;

    private readonly AggregationType aggType;

    private MetricPointOptionalComponents? mpComponents;

    // Represents temporality adjusted "value" for double/long metric types or "count" when histogram
    private MetricPointValueStorage runningValue;

    // Represents either "value" for double/long metric types or "count" when histogram
    private MetricPointValueStorage snapshotValue;

    private MetricPointValueStorage deltaLastValue;

    internal MetricPoint(
        AggregatorStore aggregatorStore,
        AggregationType aggType,
        KeyValuePair<string, object?>[]? tagKeysAndValues,
        double[] histogramExplicitBounds,
        int exponentialHistogramMaxSize,
        int exponentialHistogramMaxScale,
        LookupData? lookupData = null)
    {
        this.aggType = aggType;
        this.Tags = new ReadOnlyTagCollection(tagKeysAndValues);
        this.runningValue = default;
        this.snapshotValue = default;
        this.deltaLastValue = default;
        this.MetricPointStatus = MetricPointStatus.NoCollectPending;
        this.ReferenceCount = 1;
        this.LookupData = lookupData;

        var isExemplarEnabled = aggregatorStore!.IsExemplarEnabled();

        ExemplarReservoir? reservoir;
        try
        {
            reservoir = isExemplarEnabled ? aggregatorStore.ExemplarReservoirFactory?.Invoke() : null;
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricViewException("ExemplarReservoirFactory", ex);
            reservoir = null;
        }

        if (this.aggType == AggregationType.HistogramWithBuckets ||
            this.aggType == AggregationType.HistogramWithMinMaxBuckets)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.HistogramBuckets = new HistogramBuckets(histogramExplicitBounds);
            if (isExemplarEnabled && reservoir == null)
            {
                reservoir = new AlignedHistogramBucketExemplarReservoir(histogramExplicitBounds!.Length);
            }
        }
        else if (this.aggType == AggregationType.Histogram || this.aggType == AggregationType.HistogramWithMinMax)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.HistogramBuckets = new HistogramBuckets(null);
        }
        else if (this.aggType == AggregationType.Base2ExponentialHistogram || this.aggType == AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.Base2ExponentialBucketHistogram = new Base2ExponentialBucketHistogram(exponentialHistogramMaxSize, exponentialHistogramMaxScale);
            if (isExemplarEnabled && reservoir == null)
            {
                reservoir = new SimpleFixedSizeExemplarReservoir(Math.Min(20, exponentialHistogramMaxSize));
            }
        }
        else
        {
            this.mpComponents = null;
        }

        if (isExemplarEnabled && reservoir == null)
        {
            reservoir = new SimpleFixedSizeExemplarReservoir(DefaultSimpleReservoirPoolSize);
        }

        if (reservoir != null)
        {
            if (this.mpComponents == null)
            {
                this.mpComponents = new MetricPointOptionalComponents();
            }

            reservoir.Initialize(aggregatorStore);

            this.mpComponents.ExemplarReservoir = reservoir;
        }

        // Note: Intentionally set last because this is used to detect valid MetricPoints.
        this.aggregatorStore = aggregatorStore;
    }

    public readonly ReadOnlyTagCollection Tags { get; }

    public readonly DateTimeOffset StartTime => this.aggregatorStore.StartTimeExclusive;
    public readonly DateTimeOffset EndTime => this.aggregatorStore.EndTimeInclusive;

    internal MetricPointStatus MetricPointStatus { get; set; }
    
    internal LookupData? LookupData { get; set; }

    internal readonly bool IsInitialized => this.aggregatorStore != null;

    public readonly double GetSumDouble()
    {
        if (this.aggType != AggregationType.DoubleSumIncomingDelta && this.aggType != AggregationType.DoubleSumIncomingCumulative)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetSumDouble));
        }

        return this.snapshotValue.AsDouble;
    }

    public readonly double GetGaugeLastValueDouble()
    {
        if (this.aggType != AggregationType.DoubleGauge)
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetGaugeLastValueDouble));

        return this.snapshotValue.AsDouble;
    }

    public readonly long GetHistogramCount()
    {
        return this.snapshotValue.AsLong;
    }

    public readonly double GetHistogramSum()
    {
        return this.mpComponents!.HistogramBuckets != null
            ? this.mpComponents.HistogramBuckets.SnapshotSum
            : this.mpComponents.Base2ExponentialBucketHistogram!.SnapshotSum;
    }

    public readonly HistogramBuckets GetHistogramBuckets()
    {
        if (this.aggType != AggregationType.HistogramWithBuckets &&
            this.aggType != AggregationType.Histogram &&
            this.aggType != AggregationType.HistogramWithMinMaxBuckets &&
            this.aggType != AggregationType.HistogramWithMinMax)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetHistogramBuckets));
        }

        Debug.Assert(this.mpComponents?.HistogramBuckets != null, "HistogramBuckets was null");

        return this.mpComponents!.HistogramBuckets!;
    }

    public ExponentialHistogramData GetExponentialHistogramData()
    {
        if (this.aggType != AggregationType.Base2ExponentialHistogram &&
            this.aggType != AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetExponentialHistogramData));
        }

        Debug.Assert(this.mpComponents?.Base2ExponentialBucketHistogram != null, "Base2ExponentialBucketHistogram was null");

        return this.mpComponents!.Base2ExponentialBucketHistogram!.GetExponentialHistogramData();
    }

    public readonly bool TryGetHistogramMinMaxValues(out double min, out double max)
    {
        if (this.aggType == AggregationType.HistogramWithMinMax
            || this.aggType == AggregationType.HistogramWithMinMaxBuckets)
        {
            Debug.Assert(this.mpComponents?.HistogramBuckets != null, "HistogramBuckets was null");

            min = this.mpComponents!.HistogramBuckets!.SnapshotMin;
            max = this.mpComponents.HistogramBuckets.SnapshotMax;
            return true;
        }

        if (this.aggType == AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            Debug.Assert(this.mpComponents?.Base2ExponentialBucketHistogram != null, "Base2ExponentialBucketHistogram was null");

            min = this.mpComponents!.Base2ExponentialBucketHistogram!.SnapshotMin;
            max = this.mpComponents.Base2ExponentialBucketHistogram.SnapshotMax;
            return true;
        }

        min = 0;
        max = 0;
        return false;
    }

    public readonly bool TryGetExemplars(out ReadOnlyExemplarCollection exemplars)
    {
        exemplars = this.mpComponents?.Exemplars ?? ReadOnlyExemplarCollection.Empty;
        return exemplars.MaximumCount > 0;
    }

    internal readonly MetricPoint Copy()
    {
        MetricPoint copy = this;
        copy.mpComponents = this.mpComponents?.Copy();
        return copy;
    }

    internal void Update(long number)
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
                {
                    Interlocked.Add(ref this.runningValue.AsLong, number);
                    break;
                }

            case AggregationType.LongSumIncomingCumulative:
            case AggregationType.LongGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsLong, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number);
                    return;
                }
            // ...
        }

        this.CompleteUpdate();
    }

    internal void UpdateWithExemplar(long number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
                {
                    Interlocked.Add(ref this.runningValue.AsLong, number);
                    break;
                }

            case AggregationType.LongSumIncomingCumulative:
            case AggregationType.LongGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsLong, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number, tags, offerExemplar);
                    return;
                }
            // ...
        }

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    internal void Update(double number)  // <------------------there is no `Update(int number)` method, int is implicitly converted to long before calling Update
    {
        switch (this.aggType)
        {
            case AggregationType.DoubleSumIncomingDelta:
                {
                    InterlockedHelper.Add(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.DoubleSumIncomingCumulative:
            case AggregationType.DoubleGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number);
                    return;
                }
            // ...        
        }

        this.CompleteUpdate();
    }

    internal void UpdateWithExemplar(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        switch (this.aggType)
        {
            case AggregationType.DoubleSumIncomingDelta:
                {
                    InterlockedHelper.Add(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.DoubleSumIncomingCumulative:
            case AggregationType.DoubleGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number, tags, offerExemplar);
                    return;
                }
            // ...      
        }

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    internal void TakeSnapshot(bool outputDelta)
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
            case AggregationType.LongSumIncomingCumulative:
                {
                    if (outputDelta)
                    {
                        long initValue = Interlocked.Read(ref this.runningValue.AsLong);
                        this.snapshotValue.AsLong = initValue - this.deltaLastValue.AsLong;
                        this.deltaLastValue.AsLong = initValue;
                        this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                        // Check again if value got updated, if yes reset status.
                        // This ensures no Updates get Lost.
                        if (initValue != Interlocked.Read(ref this.runningValue.AsLong))
                        {
                            this.MetricPointStatus = MetricPointStatus.CollectPending;
                        }
                    }
                    else
                    {
                        this.snapshotValue.AsLong = Interlocked.Read(ref this.runningValue.AsLong);
                    }

                    break;
                }

            case AggregationType.DoubleSumIncomingDelta:
            case AggregationType.DoubleSumIncomingCumulative:
                {
                    if (outputDelta)
                    {
                        double initValue = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                        this.snapshotValue.AsDouble = initValue - this.deltaLastValue.AsDouble;
                        this.deltaLastValue.AsDouble = initValue;
                        this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                        // Check again if value got updated, if yes reset status.
                        // This ensures no Updates get Lost.
                        if (initValue != InterlockedHelper.Read(ref this.runningValue.AsDouble))
                        {
                            this.MetricPointStatus = MetricPointStatus.CollectPending;
                        }
                    }
                    else
                    {
                        this.snapshotValue.AsDouble = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                    }

                    break;
                }

            case AggregationType.LongGauge:
                {
                    this.snapshotValue.AsLong = Interlocked.Read(ref this.runningValue.AsLong);
                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    // Check again if value got updated, if yes reset status.
                    // This ensures no Updates get Lost.
                    if (this.snapshotValue.AsLong != Interlocked.Read(ref this.runningValue.AsLong))
                    {
                        this.MetricPointStatus = MetricPointStatus.CollectPending;
                    }

                    break;
                }

            case AggregationType.DoubleGauge:
                {
                    this.snapshotValue.AsDouble = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    // Check again if value got updated, if yes reset status.
                    // This ensures no Updates get Lost.
                    if (this.snapshotValue.AsDouble != InterlockedHelper.Read(ref this.runningValue.AsDouble))
                    {
                        this.MetricPointStatus = MetricPointStatus.CollectPending;
                    }

                    break;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                    }

                    histogramBuckets.Snapshot(outputDelta);

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Histogram:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;
                    histogramBuckets.SnapshotMin = histogramBuckets.RunningMin;
                    histogramBuckets.SnapshotMax = histogramBuckets.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                        histogramBuckets.RunningMin = double.PositiveInfinity;
                        histogramBuckets.RunningMax = double.NegativeInfinity;
                    }

                    histogramBuckets.Snapshot(outputDelta);

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;
                    histogramBuckets.SnapshotMin = histogramBuckets.RunningMin;
                    histogramBuckets.SnapshotMax = histogramBuckets.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                        histogramBuckets.RunningMin = double.PositiveInfinity;
                        histogramBuckets.RunningMax = double.NegativeInfinity;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Base2ExponentialHistogram:
                {
                    var histogram = this.mpComponents!.Base2ExponentialBucketHistogram!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogram.SnapshotSum = histogram.RunningSum;
                    histogram.Snapshot();

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogram.RunningSum = 0;
                        histogram.Reset();
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Base2ExponentialHistogramWithMinMax:
                {
                    var histogram = this.mpComponents!.Base2ExponentialBucketHistogram!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogram.SnapshotSum = histogram.RunningSum;
                    histogram.Snapshot();
                    histogram.SnapshotMin = histogram.RunningMin;
                    histogram.SnapshotMax = histogram.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogram.RunningSum = 0;
                        histogram.Reset();
                        histogram.RunningMin = double.PositiveInfinity;
                        histogram.RunningMax = double.NegativeInfinity;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }
        }
    }

    internal void TakeSnapshotWithExemplar(bool outputDelta)
    {
        this.TakeSnapshot(outputDelta);

        this.mpComponents.Exemplars = this.mpComponents.ExemplarReservoir!.Collect();
    }

    internal void NullifyMetricPointState()
    {
        this.LookupData = null;
        this.mpComponents = null;
    }

    private void UpdateHistogram(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets!;

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
        }

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets!;

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
        }

        histogramBuckets.RunningMin = Math.Min(histogramBuckets.RunningMin, number);
        histogramBuckets.RunningMax = Math.Max(histogramBuckets.RunningMax, number);

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithBuckets(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets;

        int bucketIndex = histogramBuckets!.FindBucketIndex(number);

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
            histogramBuckets.BucketCounts[bucketIndex].RunningValue++;
        }

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar, bucketIndex);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithBucketsAndMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets;

        int bucketIndex = histogramBuckets!.FindBucketIndex(number);

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
            histogramBuckets.BucketCounts[bucketIndex].RunningValue++;
        }

        histogramBuckets.RunningMin = Math.Min(histogramBuckets.RunningMin, number);
        histogramBuckets.RunningMax = Math.Max(histogramBuckets.RunningMax, number);

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar, bucketIndex);

        this.CompleteUpdate();
    }

    private void UpdateBase2ExponentialHistogram(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false);

    private void UpdateBase2ExponentialHistogramWithMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false);

    private readonly void UpdateExemplar(long number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        if (offerExemplar)
        {
            this.mpComponents!.ExemplarReservoir!.Offer(new ExemplarMeasurement<long>(number, tags));
        }
    }

    private readonly void UpdateExemplar(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar, int explicitBucketHistogramBucketIndex = -1)
    {
        if (offerExemplar)
        {
            this.mpComponents!.ExemplarReservoir!.Offer(new ExemplarMeasurement<double>(number, tags, explicitBucketHistogramBucketIndex));
        }
    }

    private void CompleteUpdate()
    {
        this.MetricPointStatus = MetricPointStatus.CollectPending;

        this.CompleteUpdateWithoutMeasurement();
    }

    private void CompleteUpdateWithoutMeasurement()
    {
        if (this.aggregatorStore.OutputDelta)
        {
            Interlocked.Decrement(ref this.ReferenceCount);
        }
    }
}
//-----------------------Ʌ

//--------------------V
[Flags]
public enum MetricType : byte
{
    LongSum = 0x1a,
    DoubleSum = 0x1d,
    LongGauge = 0x2a,
    DoubleGauge = 0x2d,
    Histogram = 0x40,
    ExponentialHistogram = 0x50,
    LongSumNonMonotonic = 0x8a,
    DoubleSumNonMonotonic = 0x8d,
}
//--------------------Ʌ

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

//---------------------------------V
public class HistogramConfiguration : MetricStreamConfiguration
{
    public bool RecordMinMax { get; set; } = true;
}
//---------------------------------Ʌ

//-----------------------------------------------V
public class ExplicitBucketHistogramConfiguration : HistogramConfiguration
{
    public double[]? Boundaries
    {
        get
        {
            if (this.CopiedBoundaries != null)
            {
                double[] copy = new double[this.CopiedBoundaries.Length];
                this.CopiedBoundaries.AsSpan().CopyTo(copy);
                return copy;
            }

            return null;
        }

        set
        {
            if (value != null)
            {
                if (!IsSortedAndDistinct(value))
                {
                    throw new ArgumentException($"Histogram boundaries are invalid. Histogram boundaries must be in ascending order with distinct values.", nameof(value));
                }

                double[] copy = new double[value.Length];
                value.AsSpan().CopyTo(copy);
                this.CopiedBoundaries = copy;
            }
            else
            {
                this.CopiedBoundaries = null;
            }
        }
    }

    internal double[]? CopiedBoundaries { get; private set; }

    private static bool IsSortedAndDistinct(double[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
//-----------------------------------------------Ʌ
```

```C#
//--------------------------------------------------V
public static class ConsoleExporterMetricsExtensions
{
    private const int DefaultExportIntervalMilliseconds = 10000;
    private const int DefaultExportTimeoutMilliseconds = Timeout.Infinite;

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder)
        => AddConsoleExporter(builder, name: null, configureExporter: null);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, Action<ConsoleExporterOptions> configureExporter)
        => AddConsoleExporter(builder, name: null, configureExporter);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, string? name, Action<ConsoleExporterOptions>? configureExporter)
    {

        name ??= Options.DefaultName;

        if (configureExporter != null)
        {
            builder.ConfigureServices(services => services.Configure(name, configureExporter));
        }

        return builder.AddReader(sp =>
        {
            return BuildConsoleExporterMetricReader(
                sp.GetRequiredService<IOptionsMonitor<ConsoleExporterOptions>>().Get(name),
                sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(name));
        });
    }

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, Action<ConsoleExporterOptions, MetricReaderOptions>? configureExporterAndMetricReader)
        => AddConsoleExporter(builder, name: null, configureExporterAndMetricReader);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, string? name, Action<ConsoleExporterOptions, MetricReaderOptions>? configureExporterAndMetricReader)
    {

        name ??= Options.DefaultName;

        return builder.AddReader(sp =>   // <--------------------------------------------2
        {
            var exporterOptions = sp.GetRequiredService<IOptionsMonitor<ConsoleExporterOptions>>().Get(name);
            var metricReaderOptions = sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(name);

            configureExporterAndMetricReader?.Invoke(exporterOptions, metricReaderOptions);

            return BuildConsoleExporterMetricReader(exporterOptions, metricReaderOptions);
        });
    }

    private static PeriodicExportingMetricReader BuildConsoleExporterMetricReader(ConsoleExporterOptions exporterOptions, MetricReaderOptions metricReaderOptions)
    {
        var metricExporter = new ConsoleMetricExporter(exporterOptions);

        return PeriodicExportingMetricReaderHelper.CreatePeriodicExportingMetricReader(  // <-------------------------------------------------3
            metricExporter,                                                              // create `new PeriodicExportingMetricReader(...)`
            metricReaderOptions,
            DefaultExportIntervalMilliseconds,
            DefaultExportTimeoutMilliseconds);
    }
}
//--------------------------------------------------Ʌ

//--------------------------------V
public class ConsoleMetricExporter : ConsoleExporter<Metric>
{
    public ConsoleMetricExporter(ConsoleExporterOptions options): base(options) { }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            var msg = new StringBuilder(Environment.NewLine);
            msg.Append(CultureInfo.InvariantCulture, $"Metric Name: {metric.Name}");
            if (string.IsNullOrEmpty(metric.Description))
            {
                msg.Append(CultureInfo.InvariantCulture, $", Description: {metric.Description}");
            }

            if (string.IsNullOrEmpty(metric.Unit))
            {
                msg.Append(CultureInfo.InvariantCulture, $", Unit: {metric.Unit}");
            }

            this.WriteLine(msg.ToString());

            foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            {
                string valueDisplay = string.Empty;
                StringBuilder tagsBuilder = new StringBuilder();
                foreach (var tag in metricPoint.Tags)
                {
                    if (this.TagWriter.TryTransformTag(tag, out var result))
                    {
                        tagsBuilder.Append(CultureInfo.InvariantCulture, $"{result.Key}: {result.Value}");
                        tagsBuilder.Append(' ');
                    }
                }

                var tags = tagsBuilder.ToString().TrimEnd();

                var metricType = metric.MetricType;

                if (metricType == MetricType.Histogram || metricType == MetricType.ExponentialHistogram)
                {
                    var bucketsBuilder = new StringBuilder();
                    var sum = metricPoint.GetHistogramSum();
                    var count = metricPoint.GetHistogramCount();
                    bucketsBuilder.Append(CultureInfo.InvariantCulture, $"Sum: {sum} Count: {count} ");
                    if (metricPoint.TryGetHistogramMinMaxValues(out double min, out double max))
                    {
                        bucketsBuilder.Append(CultureInfo.InvariantCulture, $"Min: {min} Max: {max} ");
                    }

                    bucketsBuilder.AppendLine();

                    if (metricType == MetricType.Histogram)
                    {
                        bool isFirstIteration = true;
                        double previousExplicitBound = default;
                        foreach (var histogramMeasurement in metricPoint.GetHistogramBuckets())
                        {
                            if (isFirstIteration)
                            {
                                bucketsBuilder.Append("(-Infinity,");
                                bucketsBuilder.Append(histogramMeasurement.ExplicitBound);
                                bucketsBuilder.Append(']');
                                bucketsBuilder.Append(':');
                                bucketsBuilder.Append(histogramMeasurement.BucketCount);
                                previousExplicitBound = histogramMeasurement.ExplicitBound;
                                isFirstIteration = false;
                            }
                            else
                            {
                                bucketsBuilder.Append('(');
                                bucketsBuilder.Append(previousExplicitBound);
                                bucketsBuilder.Append(',');
                                if (histogramMeasurement.ExplicitBound != double.PositiveInfinity)
                                {
                                    bucketsBuilder.Append(histogramMeasurement.ExplicitBound);
                                    previousExplicitBound = histogramMeasurement.ExplicitBound;
                                }
                                else
                                {
                                    bucketsBuilder.Append("+Infinity");
                                }

                                bucketsBuilder.Append(']');
                                bucketsBuilder.Append(':');
                                bucketsBuilder.Append(histogramMeasurement.BucketCount);
                            }

                            bucketsBuilder.AppendLine();
                        }
                    }
                    else
                    {
                        var exponentialHistogramData = metricPoint.GetExponentialHistogramData();
                        var scale = exponentialHistogramData.Scale;

                        if (exponentialHistogramData.ZeroCount != 0)
                            bucketsBuilder.AppendLine(CultureInfo.InvariantCulture, $"Zero Bucket:{exponentialHistogramData.ZeroCount}");

                        var offset = exponentialHistogramData.PositiveBuckets.Offset;
                        foreach (var bucketCount in exponentialHistogramData.PositiveBuckets)
                        {
                            var lowerBound = Base2ExponentialBucketHistogramHelper.CalculateLowerBoundary(offset, scale).ToString(CultureInfo.InvariantCulture);
                            var upperBound = Base2ExponentialBucketHistogramHelper.CalculateLowerBoundary(++offset, scale).ToString(CultureInfo.InvariantCulture);
                            bucketsBuilder.AppendLine(CultureInfo.InvariantCulture, $"({lowerBound}, {upperBound}]:{bucketCount}");
                        }
                    }

                    valueDisplay = bucketsBuilder.ToString();
                }
                else if (metricType.IsDouble())
                {
                    valueDisplay = metricType.IsSum() ? metricPoint.GetSumDouble().ToString(CultureInfo.InvariantCulture) : metricPoint.GetGaugeLastValueDouble().ToString(CultureInfo.InvariantCulture);
                }
                else if (metricType.IsLong())
                {
                    valueDisplay = metricType.IsSum() ? metricPoint.GetSumLong().ToString(CultureInfo.InvariantCulture) : metricPoint.GetGaugeLastValueLong().ToString(CultureInfo.InvariantCulture);
                }

                var exemplarString = new StringBuilder();
                if (metricPoint.TryGetExemplars(out var exemplars))
                {
                    foreach (ref readonly var exemplar in exemplars)
                    {
                        exemplarString.Append("Timestamp: ");
                        exemplarString.Append(exemplar.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                        if (metricType.IsDouble())
                        {
                            exemplarString.Append(" Value: ");
                            exemplarString.Append(exemplar.DoubleValue);
                        }
                        else if (metricType.IsLong())
                        {
                            exemplarString.Append(" Value: ");
                            exemplarString.Append(exemplar.LongValue);
                        }

                        if (exemplar.TraceId != default)
                        {
                            exemplarString.Append(" TraceId: ");
                            exemplarString.Append(exemplar.TraceId.ToHexString());
                            exemplarString.Append(" SpanId: ");
                            exemplarString.Append(exemplar.SpanId.ToHexString());
                        }

                        bool appendedTagString = false;
                        foreach (var tag in exemplar.FilteredTags)
                        {
                            if (this.TagWriter.TryTransformTag(tag, out var result))
                            {
                                if (!appendedTagString)
                                {
                                    exemplarString.Append(" Filtered Tags: ");
                                    appendedTagString = true;
                                }
                                exemplarString.Append(CultureInfo.InvariantCulture, $"{result.Key}: {result.Value}");
                                exemplarString.Append($"{result.Key}: {result.Value}");
                                exemplarString.Append(' ');
                            }
                        }
                        exemplarString.AppendLine();
                    }
                }

                msg = new StringBuilder();
                msg.Append('(');
                msg.Append(metricPoint.StartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                msg.Append(", ");
                msg.Append(metricPoint.EndTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                msg.Append("] ");
                msg.Append(tags);
                if (string.IsNullOrEmpty(tags))
                {
                    msg.Append(' ');
                }

                msg.Append(metric.MetricType);
                msg.AppendLine();
                msg.Append(CultureInfo.InvariantCulture, $"Value: {valueDisplay}");

                if (exemplarString.Length > 0)
                {
                    msg.AppendLine();
                    msg.AppendLine("Exemplars");
                    msg.Append(exemplarString);
                }

                this.WriteLine(msg.ToString());

                this.WriteLine("Instrumentation scope (Meter):");
                this.WriteLine($"\tName: {metric.MeterName}");
                if (!string.IsNullOrEmpty(metric.MeterVersion))
                {
                    this.WriteLine($"\tVersion: {metric.MeterVersion}");
                }

                if (metric.MeterTags?.Any() == true)
                {
                    this.WriteLine("\tTags:");
                    foreach (var meterTag in metric.MeterTags)
                    {
                        if (this.TagWriter.TryTransformTag(meterTag, out var result))
                        {
                            this.WriteLine($"\t\t{result.Key}: {result.Value}");
                        }
                    }
                }

                var resource = this.ParentProvider.GetResource();
                if (resource != Resource.Empty)
                {
                    this.WriteLine("Resource associated with Metric:");
                    foreach (var resourceAttribute in resource.Attributes)
                    {
                        if (this.TagWriter.TryTransformTag(resourceAttribute.Key, resourceAttribute.Value, out var result))
                        {
                            this.WriteLine($"\t{result.Key}: {result.Value}");
                        }
                    }
                }
            }
        }
        return ExportResult.Success;
    }
}
//--------------------------------Ʌ

//-------------------------------------------------------V
internal static class PeriodicExportingMetricReaderHelper
{
    internal const int DefaultExportIntervalMilliseconds = 60000;
    internal const int DefaultExportTimeoutMilliseconds = 30000;

    internal static PeriodicExportingMetricReader CreatePeriodicExportingMetricReader(
        BaseExporter<Metric> exporter,
        MetricReaderOptions options,
        int defaultExportIntervalMilliseconds = DefaultExportIntervalMilliseconds,
        int defaultExportTimeoutMilliseconds = DefaultExportTimeoutMilliseconds)
    {
        var exportInterval =
            options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds ?? defaultExportIntervalMilliseconds;

        var exportTimeout =
            options.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds ?? defaultExportTimeoutMilliseconds;

        var metricReader = new PeriodicExportingMetricReader(exporter, exportInterval, exportTimeout)
        {
            TemporalityPreference = options.TemporalityPreference,
        };

        return metricReader;
    }
}
//-------------------------------------------------------Ʌ

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

        // use Thread rather than Task.Run (because it uses the thread pool) needs a long-running,
        // dedicated background thread, that is not tied to the .NET thread pool 
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
                    this.Collect(this.ExportTimeoutMilliseconds);
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

    internal override bool ProcessMetrics(in Batch<Metric> metrics, int timeoutMilliseconds)
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

//-------------------------------------------V
public enum MetricReaderTemporalityPreference
{
    Cumulative = 1,
    Delta = 2,
}

public enum AggregationTemporality : byte
{
    Cumulative = 0b1,

    Delta = 0b10,
}
//-------------------------------------------Ʌ

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
                result = this.OnCollect(timeoutMilliseconds);
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

        var metrics = this.GetMetricsBatch();

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

    internal virtual List<Metric> AddMetricWithNoViews(Instrument instrument)
    {
        var metricStreamIdentity = new MetricStreamIdentity(instrument!, metricStreamConfiguration: null);

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
                    metric = new Metric(
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
                this.metrics![index] = metric;

                this.CreateOrUpdateMetricStreamRegistration(in metricStreamIdentity);

                return new() { metric };
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

    private Batch<Metric> GetMetricsBatch()
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
```