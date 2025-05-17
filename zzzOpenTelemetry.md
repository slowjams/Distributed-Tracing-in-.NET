## OpenTelemetry

```C#
public class Program
{
    public static void Main(string[] args)
    {
        // ...
        builder.Services
            .AddOpenTelemetry()
            .WithTracing(builder => builder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddConsoleExporter()
                .AddJaegerExporter()
                .AddSource("Tracing.NET")
             );
        //...
    }
}
```


## Source Code

```C#
//-------------------------------------------------V
public static class OpenTelemetryServicesExtensions
{
    public static OpenTelemetryBuilder AddOpenTelemetry(this IServiceCollection services)
    {
        if (!services.Any((ServiceDescriptor d) => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TelemetryHostedService)))
        {
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, TelemetryHostedService>());
        }

        return new(services);
    }
}
//-------------------------------------------------Ʌ

//------------------------------------------V
internal sealed class TelemetryHostedService : IHostedService
{
    private readonly IServiceProvider serviceProvider;

    public TelemetryHostedService(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Initialize(this.serviceProvider);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    internal static void Initialize(IServiceProvider serviceProvider)
    {
        var meterProvider = serviceProvider!.GetService<MeterProvider>();
        if (meterProvider == null)
            HostingExtensionsEventSource.Log.MeterProviderNotRegistered();

        var tracerProvider = serviceProvider!.GetService<TracerProvider>();
        if (tracerProvider == null)
            HostingExtensionsEventSource.Log.TracerProviderNotRegistered();

        var loggerProvider = serviceProvider!.GetService<LoggerProvider>();
        if (loggerProvider == null)
            HostingExtensionsEventSource.Log.LoggerProviderNotRegistered();
    }
}
//------------------------------------------Ʌ

//--------------------------------Ʌ
public abstract class BaseProvider : IDisposable
{
    ~BaseProvider()
    {
        this.Dispose(false);
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }
}
//--------------------------------Ʌ

//-------------------------V
public class TracerProvider : BaseProvider
{
    internal ConcurrentDictionary<TracerKey, Tracer>? Tracers = new();

    protected TracerProvider() { }

    public static TracerProvider Default { get; } = new TracerProvider();

    public Tracer GetTracer(string name, string? version) => this.GetTracer(name, version, null);

    public Tracer GetTracer(string name, string? version = null, IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        var tracers = this.Tracers;
        if (tracers == null)
        {
            // note: Returns a no-op Tracer once dispose has been called.
            return new(activitySource: null);
        }

        var key = new TracerKey(name, version, tags);

        if (!tracers.TryGetValue(key, out var tracer))
        {
            lock (tracers)
            {
                if (this.Tracers == null)
                {
                    // note: ee check here for a race with Dispose and return a no-op Tracer in that case.
                    return new(activitySource: null);
                }

                tracer = new(new(key.Name, key.Version, key.Tags));
                bool result = tracers.TryAdd(key, tracer);
                System.Diagnostics.Debug.Assert(result, "Write into tracers cache failed");
            }
        }

        return tracer;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var tracers = Interlocked.Exchange(ref this.Tracers, null);
            if (tracers != null)
            {
                lock (tracers)
                {
                    foreach (var kvp in tracers)
                    {
                        var tracer = kvp.Value;
                        var activitySource = tracer.ActivitySource;
                        tracer.ActivitySource = null;
                        activitySource?.Dispose();
                    }

                    tracers.Clear();
                }
            }
        }

        base.Dispose(disposing);
    }

    internal readonly record struct TracerKey
    {
        public readonly string Name;
        public readonly string? Version;
        public readonly KeyValuePair<string, object?>[]? Tags;

        public TracerKey(string? name, string? version, IEnumerable<KeyValuePair<string, object?>>? tags)
        {
            this.Name = name ?? string.Empty;
            this.Version = version;
            this.Tags = GetOrderedTags(tags);
        }

        public bool Equals(TracerKey other)
        {
            if (!string.Equals(this.Name, other.Name, StringComparison.Ordinal) || !string.Equals(this.Version, other.Version, StringComparison.Ordinal))
                return false;

            return AreTagsEqual(this.Tags, other.Tags);
        }

        public override int GetHashCode() { ... }
       
        private static bool AreTagsEqual(KeyValuePair<string, object?>[]? tags1, KeyValuePair<string, object?>[]? tags2) { ... }
       

        private static int GetTagsHashCode(IEnumerable<KeyValuePair<string, object?>>? tags) { ... }
        
        private static KeyValuePair<string, object?>[]? GetOrderedTags(IEnumerable<KeyValuePair<string, object?>>? tags)
        {
            if (tags is null)
                return null;

            var orderedTagList = new List<KeyValuePair<string, object?>>(tags);
            orderedTagList.Sort((left, right) =>
            {
                // First compare by key
                int keyComparison = string.Compare(left.Key, right.Key, StringComparison.Ordinal);
                if (keyComparison != 0)
                    return keyComparison;
                
                // If keys are equal, compare by value
                if (left.Value == null && right.Value == null)
                    return 0;

                if (left.Value == null)
                    return -1;

                if (right.Value == null)
                    return 1;

                // Both values are non-null, compare as strings
                return string.Compare(left.Value.ToString(), right.Value.ToString(), StringComparison.Ordinal);
            });

            return [.. orderedTagList];
        }
    }
}
//-------------------------Ʌ
```

```C#
//--------------------------------------------------------------V
internal static class ProviderBuilderServiceCollectionExtensions
{
    public static IServiceCollection AddOpenTelemetryLoggerProviderBuilderServices(this IServiceCollection services)
    {
        services!.TryAddSingleton<LoggerProviderBuilderSdk>();
        services!.RegisterOptionsFactory(configuration => new BatchExportLogRecordProcessorOptions(configuration));
        services!.RegisterOptionsFactory(
            (sp, configuration, name) => new LogRecordExportProcessorOptions(
                sp.GetRequiredService<IOptionsMonitor<BatchExportLogRecordProcessorOptions>>().Get(name)));

        return services!;
    }

    public static IServiceCollection AddOpenTelemetryMeterProviderBuilderServices(this IServiceCollection services)
    {
        services!.TryAddSingleton<MeterProviderBuilderSdk>();
        services!.RegisterOptionsFactory(configuration => new PeriodicExportingMetricReaderOptions(configuration));
        services!.RegisterOptionsFactory(
            (sp, configuration, name) => new MetricReaderOptions(
                sp.GetRequiredService<IOptionsMonitor<PeriodicExportingMetricReaderOptions>>().Get(name)));

        return services!;
    }

    public static IServiceCollection AddOpenTelemetryTracerProviderBuilderServices(this IServiceCollection services)
    {
        services!.TryAddSingleton<TracerProviderBuilderSdk>();
        services!.RegisterOptionsFactory(configuration => new BatchExportActivityProcessorOptions(configuration));
        services!.RegisterOptionsFactory(
            (sp, configuration, name) => new ActivityExportProcessorOptions(
                sp.GetRequiredService<IOptionsMonitor<BatchExportActivityProcessorOptions>>().Get(name)));

        return services!;
    }

    public static IServiceCollection AddOpenTelemetrySharedProviderBuilderServices(this IServiceCollection services)
    {
        // accessing Sdk class is just to trigger its static ctor, which sets default Propagators and default Activity Id format
        _ = Sdk.SuppressInstrumentation;

        services!.AddOptions();

        services!.TryAddSingleton<IConfiguration>(sp => new ConfigurationBuilder().AddEnvironmentVariables().Build());

        return services!;
    }
}
//--------------------------------------------------------------Ʌ

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

//-------------------------------------------------V
public static class TracerProviderBuilderExtensions
{
    public static TracerProviderBuilder SetErrorStatusOnException(this TracerProviderBuilder tracerProviderBuilder, bool enabled = true)
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetErrorStatusOnException(enabled);
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder SetSampler(this TracerProviderBuilder tracerProviderBuilder, Sampler sampler)
    {
        Guard.ThrowIfNull(sampler);

        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetSampler(sampler);
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder SetSampler<T>(this TracerProviderBuilder tracerProviderBuilder) where T : Sampler
    {
        tracerProviderBuilder.ConfigureServices(services => services.TryAddSingleton<T>());

        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetSampler(sp.GetRequiredService<T>());
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder SetSampler(this TracerProviderBuilder tracerProviderBuilder, Func<IServiceProvider, Sampler> implementationFactory)
    {
        Guard.ThrowIfNull(implementationFactory);

        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetSampler(implementationFactory(sp));
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder SetResourceBuilder(this TracerProviderBuilder tracerProviderBuilder, ResourceBuilder resourceBuilder)
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetResourceBuilder(resourceBuilder);
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder ConfigureResource(this TracerProviderBuilder tracerProviderBuilder, Action<ResourceBuilder> configure)
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.ConfigureResource(configure);
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder AddProcessor(this TracerProviderBuilder tracerProviderBuilder, BaseProcessor<Activity> processor)
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.AddProcessor(processor);
            }
        });

        return tracerProviderBuilder;
    }


    public static TracerProviderBuilder AddProcessor<T>(this TracerProviderBuilder tracerProviderBuilder) where T : BaseProcessor<Activity>
    {
        tracerProviderBuilder.ConfigureServices(services => services.TryAddSingleton<T>());

        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.AddProcessor(sp.GetRequiredService<T>());
            }
        });

        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder AddProcessor(this TracerProviderBuilder tracerProviderBuilder, Func<IServiceProvider, BaseProcessor<Activity>> implementationFactory)
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
                tracerProviderBuilderSdk.AddProcessor(implementationFactory(sp));
        });

        return tracerProviderBuilder;
    }

    public static TracerProvider Build(this TracerProviderBuilder tracerProviderBuilder)
    {
        if (tracerProviderBuilder is TracerProviderBuilderBase tracerProviderBuilderBase)
            return tracerProviderBuilderBase.InvokeBuild();

        throw new NotSupportedException($"Build is not supported on '{tracerProviderBuilder?.GetType().FullName ?? "null"}' instances.");
    }
}
//-------------------------------------------------Ʌ

//--------------------------------------------------------------------------V
public static class HttpClientInstrumentationTracerProviderBuilderExtensions
{
    public static TracerProviderBuilder AddHttpClientInstrumentation(this TracerProviderBuilder builder)  // <-------------------hci0
    {
        return builder.AddHttpClientInstrumentation(null, null);
    }

    public static TracerProviderBuilder AddHttpClientInstrumentation(this TracerProviderBuilder builder, Action<HttpClientTraceInstrumentationOptions>? configureHttpClientTraceInstrumentationOptions)
    {
        return builder.AddHttpClientInstrumentation(null, configureHttpClientTraceInstrumentationOptions);
    }

    public static TracerProviderBuilder AddHttpClientInstrumentation(this TracerProviderBuilder builder, string? name, Action<HttpClientTraceInstrumentationOptions>? configureHttpClientTraceInstrumentationOptions)
    {
        Action<HttpClientTraceInstrumentationOptions> configureHttpClientTraceInstrumentationOptions2 = configureHttpClientTraceInstrumentationOptions;
        string name2 = name;
        _ = TelemetryHelper.BoxedStatusCodes;
        _ = HttpTagHelper.RequestDataHelper;
        if (name2 == null)
        {
            name2 = Options.DefaultName;
        }

        builder.ConfigureServices(delegate (IServiceCollection services)
        {
            if (configureHttpClientTraceInstrumentationOptions2 != null)
            {
                services.Configure(name2, configureHttpClientTraceInstrumentationOptions2);
            }

            services.RegisterOptionsFactory((IConfiguration configuration) => new HttpClientTraceInstrumentationOptions(configuration));
        });
        builder.AddHttpClientInstrumentationSource();
        builder.AddInstrumentation((IServiceProvider sp) => new HttpClientInstrumentation(sp.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().Get(name2)));
        return builder;
    }

    internal static void AddHttpClientInstrumentationSource(this TracerProviderBuilder builder)
    {
        if (HttpHandlerDiagnosticListener.IsNet7OrGreater)
        {
            builder.AddSource("System.Net.Http");  // <----------------------------! // hci0
        }
        else
        {
            builder.AddSource(HttpHandlerDiagnosticListener.ActivitySourceName);
            builder.AddLegacySource("System.Net.Http.HttpRequestOut");
        }
    }
}
//--------------------------------------------------------------------------Ʌ

//------------------------------------------------V
public class HttpClientTraceInstrumentationOptions
{
    public HttpClientTraceInstrumentationOptions() : this(new ConfigurationBuilder().AddEnvironmentVariables().Build()) { }

    internal HttpClientTraceInstrumentationOptions(IConfiguration configuration)
    {
        Debug.Assert(configuration != null, "configuration was null");

        if (configuration!.TryGetBoolValue(HttpInstrumentationEventSource.Log, "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION",out var disableUrlQueryRedaction))
            this.DisableUrlQueryRedaction = disableUrlQueryRedaction;
    }

    public Func<HttpRequestMessage, bool>? FilterHttpRequestMessage { get; set; }
    public Action<Activity, HttpRequestMessage>? EnrichWithHttpRequestMessage { get; set; }
    public Action<Activity, HttpResponseMessage>? EnrichWithHttpResponseMessage { get; set; }
    public Action<Activity, Exception>? EnrichWithException { get; set; }
    public Func<HttpWebRequest, bool>? FilterHttpWebRequest { get; set; }
    public Action<Activity, HttpWebRequest>? EnrichWithHttpWebRequest { get; set; }
    public Action<Activity, HttpWebResponse>? EnrichWithHttpWebResponse { get; set; }
    public bool RecordException { get; set; }
    internal bool DisableUrlQueryRedaction { get; set; }

    internal bool EventFilterHttpRequestMessage(string activityName, object arg1)
    {
        try
        {
            return
                this.FilterHttpRequestMessage == null ||
                !TryParseHttpRequestMessage(activityName, arg1, out var requestMessage) ||
                this.FilterHttpRequestMessage(requestMessage);
        }
        catch (Exception ex)
        {
            HttpInstrumentationEventSource.Log.RequestFilterException(ex);
            return false;
        }
    }

    internal bool EventFilterHttpWebRequest(HttpWebRequest request)
    {
        try
        {
            return this.FilterHttpWebRequest?.Invoke(request) ?? true;
        }
        catch (Exception ex)
        {
            HttpInstrumentationEventSource.Log.RequestFilterException(ex);
            return false;
        }
    }

    private static bool TryParseHttpRequestMessage(string activityName, object arg1, [NotNullWhen(true)] out HttpRequestMessage? requestMessage)
    {
        return (requestMessage = arg1 as HttpRequestMessage) != null && activityName == "System.Net.Http.HttpRequestOut";
    }
}
//------------------------------------------------Ʌ

//--------------------------------------V
public sealed class OpenTelemetryBuilder : IOpenTelemetryBuilder
{
    internal OpenTelemetryBuilder(IServiceCollection services)
    {
        Guard.ThrowIfNull(services);

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

    public OpenTelemetryBuilder WithMetrics(Action<MeterProviderBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.WithMetrics(this, configure);
        return this;
    }

    public OpenTelemetryBuilder WithTracing() => this.WithTracing(b => { });

    public OpenTelemetryBuilder WithTracing(Action<TracerProviderBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.WithTracing(this, configure);
        return this;
    }

    public OpenTelemetryBuilder WithLogging(Action<LoggerProviderBuilder>? configureBuilder, Action<OpenTelemetryLoggerOptions>? configureOptions)
    {
        OpenTelemetryBuilderSdkExtensions.WithLogging(this, configureBuilder, configureOptions);

        return this;
    }
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

    public static IOpenTelemetryBuilder WithTracing(this IOpenTelemetryBuilder builder)
        => WithTracing(builder, b => { });

    public static IOpenTelemetryBuilder WithTracing(this IOpenTelemetryBuilder builder, Action<TracerProviderBuilder> configure)
    {
        var tracerProviderBuilder = new TracerProviderBuilderBase(builder.Services);

        configure(tracerProviderBuilder);

        return builder;
    }

    public static IOpenTelemetryBuilder WithLogging(this IOpenTelemetryBuilder builder)
        => WithLogging(builder, configureBuilder: null, configureOptions: null);

    public static IOpenTelemetryBuilder WithLogging(this IOpenTelemetryBuilder builder, Action<LoggerProviderBuilder> configure)
    {
        return WithLogging(builder, configureBuilder: configure, configureOptions: null);
    }

    public static IOpenTelemetryBuilder WithLogging(this IOpenTelemetryBuilder builder, Action<LoggerProviderBuilder>? configureBuilder, Action<OpenTelemetryLoggerOptions>? configureOptions)
    {
        builder.Services.AddLogging(logging => logging.UseOpenTelemetry(configureBuilder, configureOptions));

        return builder;
    }
}
//---------------------------------------------------Ʌ

//------------------------------------V
public class TracerProviderBuilderBase : TracerProviderBuilder, ITracerProviderBuilder
{
    private readonly bool allowBuild;
    private readonly TracerProviderServiceCollectionBuilder innerBuilder;

    public TracerProviderBuilderBase()
    {
        var services = new ServiceCollection();

        services
            .AddOpenTelemetrySharedProviderBuilderServices()
            .AddOpenTelemetryTracerProviderBuilderServices()
            .TryAddSingleton<TracerProvider>(
                sp => throw new NotSupportedException("Self-contained TracerProvider cannot be accessed using the application IServiceProvider call Build instead."));

        this.innerBuilder = new TracerProviderServiceCollectionBuilder(services);

        this.allowBuild = true;
    }

    internal TracerProviderBuilderBase(IServiceCollection services)
    {
        services
            .AddOpenTelemetryTracerProviderBuilderServices()
            .TryAddSingleton<TracerProvider>(sp => new TracerProviderSdk(sp, ownsServiceProvider: false));

        this.innerBuilder = new TracerProviderServiceCollectionBuilder(services);

        this.allowBuild = false;
    }

    TracerProvider? ITracerProviderBuilder.Provider => null;

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        this.innerBuilder.AddInstrumentation(instrumentationFactory);

        return this;
    }

    public override TracerProviderBuilder AddSource(params string[] names)
    {
        this.innerBuilder.AddSource(names);

        return this;
    }

    public override TracerProviderBuilder AddLegacySource(string operationName)
    {
        this.innerBuilder.AddLegacySource(operationName);

        return this;
    }

    TracerProviderBuilder ITracerProviderBuilder.ConfigureServices(Action<IServiceCollection> configure)
    {
        this.innerBuilder.ConfigureServices(configure);

        return this;
    }

    TracerProviderBuilder IDeferredTracerProviderBuilder.Configure(Action<IServiceProvider, TracerProviderBuilder> configure)
    {
        this.innerBuilder.ConfigureBuilder(configure);

        return this;
    }

    internal TracerProvider InvokeBuild() => this.Build();

    protected TracerProviderBuilder AddInstrumentation(string instrumentationName, string instrumentationVersion, Func<object?> instrumentationFactory)
    {
        this.innerBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderState)
            {
                tracerProviderBuilderState.AddInstrumentation(
                    instrumentationName,
                    instrumentationVersion,
                    instrumentationFactory());
            }
        });

        return this;
    }

    protected TracerProvider Build()
    {
        if (!this.allowBuild)
        {
            throw new NotSupportedException("A TracerProviderBuilder bound to external service cannot be built directly. Access the TracerProvider using the application IServiceProvider instead.");
        }

        var services = this.innerBuilder.Services ?? throw new NotSupportedException("TracerProviderBuilder build method cannot be called multiple times.");

        this.innerBuilder.Services = null;

        bool validateScopes = false;

        var serviceProvider = services.BuildServiceProvider(validateScopes);

        return new TracerProviderSdk(serviceProvider, ownsServiceProvider: true);
    }
}
//------------------------------------Ʌ

//--------------------------------------------V
internal sealed class TracerProviderBuilderSdk : TracerProviderBuilder, ITracerProviderBuilder
{
    private const string DefaultInstrumentationVersion = "1.0.0.0";

    private readonly IServiceProvider serviceProvider;
    private TracerProviderSdk? tracerProvider;

    public TracerProviderBuilderSdk(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public List<InstrumentationRegistration> Instrumentation { get; } = new();

    public ResourceBuilder? ResourceBuilder { get; private set; }

    public TracerProvider? Provider => this.tracerProvider;

    public List<BaseProcessor<Activity>> Processors { get; } = new();

    public List<string> Sources { get; } = new();  // <------------------------

    public HashSet<string> LegacyActivityOperationNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Sampler? Sampler { get; private set; }

    public bool ExceptionProcessorEnabled { get; private set; }

    public void RegisterProvider(TracerProviderSdk tracerProvider)
    {
        if (this.tracerProvider != null)
            throw new NotSupportedException("TracerProvider cannot be accessed while build is executing.");

        this.tracerProvider = tracerProvider;
    }

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        return this.AddInstrumentation(
            typeof(TInstrumentation).Name,
            typeof(TInstrumentation).Assembly.GetName().Version?.ToString() ?? DefaultInstrumentationVersion,
            instrumentationFactory!());
    }

    public TracerProviderBuilder AddInstrumentation(string instrumentationName, string instrumentationVersion, object? instrumentation)
    {
        this.Instrumentation.Add(new InstrumentationRegistration(instrumentationName, instrumentationVersion, instrumentation));

        return this;
    }

    public TracerProviderBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        var resourceBuilder = this.ResourceBuilder ??= ResourceBuilder.CreateDefault();

        configure!(resourceBuilder);

        return this;
    }

    public TracerProviderBuilder SetResourceBuilder(ResourceBuilder resourceBuilder)
    {
        this.ResourceBuilder = resourceBuilder;

        return this;
    }

    public override TracerProviderBuilder AddLegacySource(string operationName)
    {
        this.LegacyActivityOperationNames.Add(operationName);

        return this;
    }

    public override TracerProviderBuilder AddSource(params string[] names)
    {
        foreach (var name in names!)
        {
            this.Sources.Add(name);
        }

        return this;
    }

    public TracerProviderBuilder AddProcessor(BaseProcessor<Activity> processor)
    {
        this.Processors.Add(processor!);

        return this;
    }

    public TracerProviderBuilder SetSampler(Sampler sampler)
    {
        this.Sampler = sampler;

        return this;
    }

    public TracerProviderBuilder SetErrorStatusOnException(bool enabled)
    {
        this.ExceptionProcessorEnabled = enabled;

        return this;
    }

    public TracerProviderBuilder ConfigureBuilder(Action<IServiceProvider, TracerProviderBuilder> configure)
    {
        configure!(this.serviceProvider, this);

        return this;
    }

    public TracerProviderBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        throw new NotSupportedException("Services cannot be configured after ServiceProvider has been created.");
    }

    public void AddExceptionProcessorIfEnabled(ref IEnumerable<BaseProcessor<Activity>> processors)
    {
        if (this.ExceptionProcessorEnabled)
        {
            try
            {
                processors = new BaseProcessor<Activity>[] { new ExceptionProcessor() }.Concat(processors);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"'{nameof(TracerProviderBuilderExtensions.SetErrorStatusOnException)}' is not supported on this platform", ex);
            }
        }
    }

    TracerProviderBuilder IDeferredTracerProviderBuilder.Configure(Action<IServiceProvider, TracerProviderBuilder> configure)
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
//--------------------------------------------Ʌ

//-------------------------------------V
internal sealed class TracerProviderSdk : TracerProvider
{
    internal const string TracesSamplerConfigKey = "OTEL_TRACES_SAMPLER";
    internal const string TracesSamplerArgConfigKey = "OTEL_TRACES_SAMPLER_ARG";

    internal readonly IServiceProvider ServiceProvider;
    internal IDisposable? OwnedServiceProvider;
    internal int ShutdownCount;
    internal bool Disposed;

    private readonly List<object> instrumentations = [];
    private readonly ActivityListener listener;
    private readonly Sampler sampler;
    private readonly Action<Activity> getRequestedDataAction;
    private readonly bool supportLegacyActivity;
    private BaseProcessor<Activity>? processor;

    internal TracerProviderSdk(IServiceProvider serviceProvider, bool ownsServiceProvider)
    {

        var state = serviceProvider!.GetRequiredService<TracerProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
        {
            this.OwnedServiceProvider = serviceProvider as IDisposable;
        }

        OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent("Building TracerProvider.");

        var configureProviderBuilders = serviceProvider!.GetServices<IConfigureTracerProviderBuilder>();
        foreach (var configureProviderBuilder in configureProviderBuilders)
        {
            configureProviderBuilder.ConfigureBuilder(serviceProvider!, state);
        }

        StringBuilder processorsAdded = new StringBuilder();
        StringBuilder instrumentationFactoriesAdded = new StringBuilder();

        var resourceBuilder = state.ResourceBuilder ?? ResourceBuilder.CreateDefault();
        resourceBuilder.ServiceProvider = serviceProvider;
        this.Resource = resourceBuilder.Build();

        this.sampler = GetSampler(serviceProvider!.GetRequiredService<IConfiguration>(), state.Sampler);
        OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent($"Sampler added = \"{this.sampler.GetType()}\".");

        this.supportLegacyActivity = state.LegacyActivityOperationNames.Count > 0;

        Regex? legacyActivityWildcardModeRegex = null;
        foreach (var legacyName in state.LegacyActivityOperationNames)
        {
            if (WildcardHelper.ContainsWildcard(legacyName))
            {
                legacyActivityWildcardModeRegex = WildcardHelper.GetWildcardRegex(state.LegacyActivityOperationNames);
                break;
            }
        }

        IEnumerable<BaseProcessor<Activity>> processors = state.Processors.OrderBy(p => p.PipelineWeight);

        state.AddExceptionProcessorIfEnabled(ref processors);

        foreach (var processor in processors)
        {
            this.AddProcessor(processor);
            processorsAdded.Append(processor.GetType());
            processorsAdded.Append(';');
        }

        foreach (var instrumentation in state.Instrumentation)
        {
            if (instrumentation.Instance is not null)
            {
                this.instrumentations.Add(instrumentation.Instance);
            }

            instrumentationFactoriesAdded.Append(instrumentation.Name);
            instrumentationFactoriesAdded.Append(';');
        }

        if (processorsAdded.Length != 0)
        {
            processorsAdded.Remove(processorsAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent($"Processors added = \"{processorsAdded}\".");
        }

        if (instrumentationFactoriesAdded.Length != 0)
        {
            instrumentationFactoriesAdded.Remove(instrumentationFactoriesAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent($"Instrumentations added = \"{instrumentationFactoriesAdded}\".");
        }

        var activityListener = new ActivityListener();  // <-----------------------tpsact

        if (this.supportLegacyActivity)
        {
            Func<Activity, bool>? legacyActivityPredicate = null;
            if (legacyActivityWildcardModeRegex != null)
            {
                legacyActivityPredicate = activity => legacyActivityWildcardModeRegex.IsMatch(activity.OperationName);
            }
            else
            {
                legacyActivityPredicate = activity => state.LegacyActivityOperationNames.Contains(activity.OperationName);
            }

            activityListener.ActivityStarted = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStarted(activity);

                if (string.IsNullOrEmpty(activity.Source.Name))
                {
                    if (legacyActivityPredicate(activity))
                    {
                        if (!Sdk.SuppressInstrumentation)
                            this.getRequestedDataAction!(activity);
                        else
                            activity.IsAllDataRequested = false;
                    }
                    else
                    {
                        return;
                    }
                }

                if (!activity.IsAllDataRequested)
                    return;

                if (SuppressInstrumentationScope.IncrementIfTriggered() == 0)
                    this.processor?.OnStart(activity);
            };

            activityListener.ActivityStopped = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStopped(activity);

                if (string.IsNullOrEmpty(activity.Source.Name) && !legacyActivityPredicate(activity))
                    return;

                if (!activity.IsAllDataRequested)
                    return;

                if (SuppressInstrumentationScope.DecrementIfTriggered() == 0)
                    this.processor?.OnEnd(activity);
            };
        }
        else
        {
            activityListener.ActivityStarted = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStarted(activity);

                if (activity.IsAllDataRequested && SuppressInstrumentationScope.IncrementIfTriggered() == 0)
                {
                    this.processor?.OnStart(activity);
                }
            };

            activityListener.ActivityStopped = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStopped(activity);

                if (!activity.IsAllDataRequested)
                {
                    return;
                }

                if (SuppressInstrumentationScope.DecrementIfTriggered() == 0)
                {
                    this.processor?.OnEnd(activity);
                }
            };
        }

        if (this.sampler is AlwaysOnSampler)
        {
            activityListener.Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                !Sdk.SuppressInstrumentation ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None;
            this.getRequestedDataAction = this.RunGetRequestedDataAlwaysOnSampler;
        }
        else if (this.sampler is AlwaysOffSampler)
        {
            activityListener.Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                !Sdk.SuppressInstrumentation ? PropagateOrIgnoreData(ref options) : ActivitySamplingResult.None;
            this.getRequestedDataAction = this.RunGetRequestedDataAlwaysOffSampler;
        }
        else
        {
            // This delegate informs ActivitySource about sampling decision when the parent context is an ActivityContext.
            activityListener.Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                !Sdk.SuppressInstrumentation ? ComputeActivitySamplingResult(ref options, this.sampler) : ActivitySamplingResult.None;
            this.getRequestedDataAction = this.RunGetRequestedDataOtherSampler;
        }

        // sources can be null. This happens when user is only interested in InstrumentationLibraries  which do not depend on ActivitySources.
        if (state.Sources.Count > 0)
        {
            // validation of source name is already done in builder.
            if (state.Sources.Any(s => WildcardHelper.ContainsWildcard(s)))
            {
                var regex = WildcardHelper.GetWildcardRegex(state.Sources);

                // Function which takes ActivitySource and returns true/false to indicate if it should be subscribed to or not.
                activityListener.ShouldListenTo = activitySource =>
                    this.supportLegacyActivity ?
                    string.IsNullOrEmpty(activitySource.Name) || regex.IsMatch(activitySource.Name) :
                    regex.IsMatch(activitySource.Name);
            }
            else
            {
                var activitySources = new HashSet<string>(state.Sources, StringComparer.OrdinalIgnoreCase);

                if (this.supportLegacyActivity)
                {
                    activitySources.Add(string.Empty);
                }

                // function which takes ActivitySource and returns true/false to indicate if it should be subscribed to or not.
                activityListener.ShouldListenTo = activitySource => activitySources.Contains(activitySource.Name);
            }
        }
        else
        {
            if (this.supportLegacyActivity)
            {
                activityListener.ShouldListenTo = activitySource => string.IsNullOrEmpty(activitySource.Name);
            }
        }

        ActivitySource.AddActivityListener(activityListener);
        this.listener = activityListener;
        OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent("TracerProvider built successfully.");
    }

    internal Resource Resource { get; }

    internal List<object> Instrumentations => this.instrumentations;

    internal BaseProcessor<Activity>? Processor => this.processor;

    internal Sampler Sampler => this.sampler;

    internal TracerProviderSdk AddProcessor(BaseProcessor<Activity> processor)
    {
        Guard.ThrowIfNull(processor);

        processor.SetParentProvider(this);

        if (this.processor == null)
        {
            this.processor = processor;
        }
        else if (this.processor is CompositeProcessor<Activity> compositeProcessor)
        {
            compositeProcessor.AddProcessor(processor);
        }
        else
        {
            var newCompositeProcessor = new CompositeProcessor<Activity>(new[]
            {
                this.processor,
            });
            newCompositeProcessor.SetParentProvider(this);
            newCompositeProcessor.AddProcessor(processor);
            this.processor = newCompositeProcessor;
        }

        return this;
    }

    internal bool OnForceFlush(int timeoutMilliseconds)
    {
        return this.processor?.ForceFlush(timeoutMilliseconds) ?? true;
    }

    internal bool OnShutdown(int timeoutMilliseconds)
    {
        // TO DO Put OnShutdown logic in a task to run within the user provider timeOutMilliseconds
        foreach (var item in this.instrumentations)
        {
            (item as IDisposable)?.Dispose();
        }

        this.instrumentations.Clear();

        bool? result = this.processor?.Shutdown(timeoutMilliseconds);
        this.listener?.Dispose();
        return result ?? true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.Disposed)
        {
            if (disposing)
            {
                foreach (var item in this.instrumentations)
                {
                    (item as IDisposable)?.Dispose();
                }

                this.instrumentations.Clear();

                (this.sampler as IDisposable)?.Dispose();

                // Wait for up to 5 seconds grace period
                this.processor?.Shutdown(5000);
                this.processor?.Dispose();
                this.processor = null;

                this.listener?.Dispose();

                this.OwnedServiceProvider?.Dispose();
                this.OwnedServiceProvider = null;
            }

            this.Disposed = true;
            OpenTelemetrySdkEventSource.Log.ProviderDisposed(nameof(TracerProvider));
        }

        base.Dispose(disposing);
    }

    private static Sampler GetSampler(IConfiguration configuration, Sampler? stateSampler)
    {
        var sampler = stateSampler;

        if (configuration.TryGetStringValue(TracesSamplerConfigKey, out var configValue))
        {
            if (sampler != null)
            {
                OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent(
                    $"Trace sampler configuration value '{configValue}' has been ignored because a value '{sampler.GetType().FullName}' was set programmatically.");
                return sampler;
            }

            switch (configValue)
            {
                case var _ when string.Equals(configValue, "always_on", StringComparison.OrdinalIgnoreCase):
                    sampler = new AlwaysOnSampler();
                    break;
                case var _ when string.Equals(configValue, "always_off", StringComparison.OrdinalIgnoreCase):
                    sampler = new AlwaysOffSampler();
                    break;
                case var _ when string.Equals(configValue, "traceidratio", StringComparison.OrdinalIgnoreCase):
                    {
                        var traceIdRatio = ReadTraceIdRatio(configuration);
                        sampler = new TraceIdRatioBasedSampler(traceIdRatio);
                        break;
                    }

                case var _ when string.Equals(configValue, "parentbased_always_on", StringComparison.OrdinalIgnoreCase):
                    sampler = new ParentBasedSampler(new AlwaysOnSampler());
                    break;
                case var _ when string.Equals(configValue, "parentbased_always_off", StringComparison.OrdinalIgnoreCase):
                    sampler = new ParentBasedSampler(new AlwaysOffSampler());
                    break;
                case var _ when string.Equals(configValue, "parentbased_traceidratio", StringComparison.OrdinalIgnoreCase):
                    {
                        var traceIdRatio = ReadTraceIdRatio(configuration);
                        sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(traceIdRatio));
                        break;
                    }

                default:
                    OpenTelemetrySdkEventSource.Log.TracesSamplerConfigInvalid(configValue);
                    break;
            }

            if (sampler != null)
            {
                OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent($"Trace sampler set to '{sampler.GetType().FullName}' from configuration.");
            }
        }

        return sampler ?? new ParentBasedSampler(new AlwaysOnSampler());
    }

    private static double ReadTraceIdRatio(IConfiguration configuration)
    {
        if (configuration.TryGetStringValue(TracesSamplerArgConfigKey, out var configValue) && double.TryParse(configValue, out var traceIdRatio))
            return traceIdRatio;
        else
            OpenTelemetrySdkEventSource.Log.TracesSamplerArgConfigInvalid(configValue ?? string.Empty);

        return 1.0;
    }

    private static ActivitySamplingResult ComputeActivitySamplingResult(ref ActivityCreationOptions<ActivityContext> options, Sampler sampler)
    {
        var samplingParameters = new SamplingParameters(options.Parent, options.TraceId, options.Name, options.Kind, options.Tags, options.Links);

        var samplingResult = sampler.ShouldSample(samplingParameters);

        var activitySamplingResult = samplingResult.Decision switch
        {
            SamplingDecision.RecordAndSample => ActivitySamplingResult.AllDataAndRecorded,
            SamplingDecision.RecordOnly => ActivitySamplingResult.AllData, _ => PropagateOrIgnoreData(ref options),
        };

        if (activitySamplingResult > ActivitySamplingResult.PropagationData)
        {
            foreach (var att in samplingResult.Attributes)
            {
                options.SamplingTags.Add(att.Key, att.Value);
            }
        }

        if (activitySamplingResult != ActivitySamplingResult.None && samplingResult.TraceStateString != null)
        {
            options = options with { TraceState = samplingResult.TraceStateString };
        }

        return activitySamplingResult;
    }

    private static ActivitySamplingResult PropagateOrIgnoreData(ref ActivityCreationOptions<ActivityContext> options)
    {
        var isRootSpan = options.Parent.TraceId == default;

        // if it is the root span or the parent is remote select PropagationData so the trace ID is preserved
        // even if no activity of the trace is recorded (sampled per OpenTelemetry parlance).
        return (isRootSpan || options.Parent.IsRemote)
            ? ActivitySamplingResult.PropagationData
            : ActivitySamplingResult.None;
    }

    private void RunGetRequestedDataAlwaysOnSampler(Activity activity)
    {
        activity.IsAllDataRequested = true;
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
    }

    private void RunGetRequestedDataAlwaysOffSampler(Activity activity)
    {
        activity.IsAllDataRequested = false;
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    private void RunGetRequestedDataOtherSampler(Activity activity)
    {
        ActivityContext parentContext;
        if (string.IsNullOrEmpty(activity.ParentId) || activity.ParentSpanId.ToHexString() == "0000000000000000")
        {
            parentContext = default;
        }
        else if (activity.Parent != null)
        {
            parentContext = activity.Parent.Context;
        }
        else
        {
            parentContext = new ActivityContext(activity.TraceId, activity.ParentSpanId, activity.ActivityTraceFlags, activity.TraceStateString, isRemote: true);
        }

        var samplingParameters = new SamplingParameters(parentContext, activity.TraceId, activity.DisplayName, activity.Kind, activity.TagObjects, activity.Links);

        var samplingResult = this.sampler.ShouldSample(samplingParameters);

        switch (samplingResult.Decision)
        {
            case SamplingDecision.Drop:
                activity.IsAllDataRequested = false;
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                break;
            case SamplingDecision.RecordOnly:
                activity.IsAllDataRequested = true;
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                break;
            case SamplingDecision.RecordAndSample:
                activity.IsAllDataRequested = true;
                activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
                break;
        }

        if (samplingResult.Decision != SamplingDecision.Drop)
        {
            foreach (var att in samplingResult.Attributes)
            {
                activity.SetTag(att.Key, att.Value);
            }
        }

        if (samplingResult.TraceStateString != null)
        {
            activity.TraceStateString = samplingResult.TraceStateString;
        }
    }
}
//-------------------------------------Ʌ

//------------------------------------V
public abstract class BaseProcessor<T> : IDisposable
{
    private readonly string typeName;

    private int shutdownCount;

    public BaseProvider? ParentProvider { get; private set; }

    internal int PipelineWeight { get; set; }

    public BaseProcessor()
    {
        typeName = GetType().Name;
    }

    public virtual void OnStart(T data) { }

    public virtual void OnEnd(T data) { }

    public bool ForceFlush(int timeoutMilliseconds = -1)
    {
        try
        {
            bool result = OnForceFlush(timeoutMilliseconds);
            OpenTelemetrySdkEventSource.Log.ProcessorForceFlushInvoked(typeName, result);
            return result;
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.SpanProcessorException("ForceFlush", ex);
            return false;
        }
    }

    public bool Shutdown(int timeoutMilliseconds = -1)
    {
        if (Interlocked.CompareExchange(ref shutdownCount, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            return OnShutdown(timeoutMilliseconds);
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.SpanProcessorException("Shutdown", ex);
            return false;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    internal virtual void SetParentProvider(BaseProvider parentProvider)
    {
        ParentProvider = parentProvider;
    }

    protected virtual bool OnForceFlush(int timeoutMilliseconds) => true;
    
    protected virtual bool OnShutdown(int timeoutMilliseconds) => true;

    protected virtual void Dispose(bool disposing) { }
}
//------------------------------------Ʌ

//------------------------------------------V
public abstract class BaseExportProcessor<T> : BaseProcessor<T> where T : class
{
    protected readonly BaseExporter<T> exporter;

    private readonly string friendlyTypeName;

    private bool disposed;

    internal BaseExporter<T> Exporter => exporter;

    protected BaseExportProcessor(BaseExporter<T> exporter)
    {
        friendlyTypeName = GetType().Name + "{" + exporter.GetType().Name + "}";
        this.exporter = exporter;
    }

    public override string ToString() => friendlyTypeName;

    public sealed override void OnStart(T data) { }

    public override void OnEnd(T data)
    {
        OnExport(data);
    }

    internal override void SetParentProvider(BaseProvider parentProvider)
    {
        base.SetParentProvider(parentProvider);
        exporter.ParentProvider = parentProvider;
    }

    protected abstract void OnExport(T data);

    protected override bool OnForceFlush(int timeoutMilliseconds) => exporter.ForceFlush(timeoutMilliseconds);
   
    protected override bool OnShutdown(int timeoutMilliseconds) => exporter.Shutdown(timeoutMilliseconds);
   
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                try
                {
                    exporter.Dispose();
                }
                catch (Exception ex)
                {
                    OpenTelemetrySdkEventSource.Log.SpanProcessorException("Dispose", ex);
                }
            }

            disposed = true;
        }

        base.Dispose(disposing);
    }
}
//------------------------------------------Ʌ

//--------------------------------------------V
public abstract class SimpleExportProcessor<T> : BaseExportProcessor<T> where T : class
{
    private readonly Lock syncObject = new();

    protected SimpleExportProcessor(BaseExporter<T> exporter): base(exporter) { }

    protected override void OnExport(T data)
    {
        lock (this.syncObject)
        {
            try
            {
                this.exporter.Export(new Batch<T>(data));
            }
            catch (Exception ex)
            {
                OpenTelemetrySdkEventSource.Log.SpanProcessorException(nameof(this.OnExport), ex);
            }
        }
    }
}
//--------------------------------------------Ʌ

//----------------------------------------V
public class SimpleActivityExportProcessor : SimpleExportProcessor<Activity>
{
    public SimpleActivityExportProcessor(BaseExporter<Activity> exporter) : base(exporter) { }

    public override void OnEnd(Activity data)
    {
        if (!data.Recorded)
        {
            return;
        }

        this.OnExport(data);
    }
}
//----------------------------------------Ʌ

//-------------------------------------------V
public abstract class BatchExportProcessor<T> : BaseExportProcessor<T> where T : class
{
    internal const int DefaultMaxQueueSize = 2048;
    internal const int DefaultScheduledDelayMilliseconds = 5000;
    internal const int DefaultExporterTimeoutMilliseconds = 30000;
    internal const int DefaultMaxExportBatchSize = 512;

    internal readonly int MaxExportBatchSize;
    internal readonly int ScheduledDelayMilliseconds;
    internal readonly int ExporterTimeoutMilliseconds;

    private readonly CircularBuffer<T> circularBuffer;
    private readonly Thread exporterThread;
    private readonly AutoResetEvent exportTrigger = new(false);
    private readonly ManualResetEvent dataExportedNotification = new(false);
    private readonly ManualResetEvent shutdownTrigger = new(false);
    private long shutdownDrainTarget = long.MaxValue;
    private long droppedCount;
    private bool disposed;

    protected BatchExportProcessor(
        BaseExporter<T> exporter,
        int maxQueueSize = DefaultMaxQueueSize,
        int scheduledDelayMilliseconds = DefaultScheduledDelayMilliseconds,
        int exporterTimeoutMilliseconds = DefaultExporterTimeoutMilliseconds,
        int maxExportBatchSize = DefaultMaxExportBatchSize)
        : base(exporter)
    {
        this.circularBuffer = new CircularBuffer<T>(maxQueueSize);
        this.ScheduledDelayMilliseconds = scheduledDelayMilliseconds;
        this.ExporterTimeoutMilliseconds = exporterTimeoutMilliseconds;
        this.MaxExportBatchSize = maxExportBatchSize;
        this.exporterThread = new Thread(this.ExporterProc)
        {
            IsBackground = true,
            Name = $"OpenTelemetry-{nameof(BatchExportProcessor<T>)}-{exporter.GetType().Name}",
        };
        this.exporterThread.Start();
    }

    internal long DroppedCount => Volatile.Read(ref this.droppedCount);
    internal long ReceivedCount => this.circularBuffer.AddedCount + this.DroppedCount;
    internal long ProcessedCount => this.circularBuffer.RemovedCount;

    internal bool TryExport(T data)
    {
        if (this.circularBuffer.TryAdd(data, maxSpinCount: 50000))
        {
            if (this.circularBuffer.Count >= this.MaxExportBatchSize)
            {
                try
                {
                    this.exportTrigger.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return true; // enqueue succeeded
        }

        // either the queue is full or exceeded the spin limit, drop the item on the floor
        Interlocked.Increment(ref this.droppedCount);

        return false;
    }

    protected override void OnExport(T data)
    {
        this.TryExport(data);
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        var tail = this.circularBuffer.RemovedCount;
        var head = this.circularBuffer.AddedCount;

        if (head == tail)
        {
            return true; // nothing to flush
        }

        try
        {
            this.exportTrigger.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (timeoutMilliseconds == 0)
        {
            return false;
        }

        var triggers = new WaitHandle[] { this.dataExportedNotification, this.shutdownTrigger };

        var sw = timeoutMilliseconds == Timeout.Infinite
            ? null
            : Stopwatch.StartNew();

        // There is a chance that the export thread finished processing all the data from the queue,
        // and signaled before we enter wait here, use polling to prevent being blocked indefinitely.
        const int pollingMilliseconds = 1000;

        while (true)
        {
            if (sw == null)
            {
                try
                {
                    WaitHandle.WaitAny(triggers, pollingMilliseconds);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
            else
            {
                var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

                if (timeout <= 0)
                {
                    return this.circularBuffer.RemovedCount >= head;
                }

                try
                {
                    WaitHandle.WaitAny(triggers, Math.Min((int)timeout, pollingMilliseconds));
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            if (this.circularBuffer.RemovedCount >= head)
            {
                return true;
            }

            if (Volatile.Read(ref this.shutdownDrainTarget) != long.MaxValue)
            {
                return false;
            }
        }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        Volatile.Write(ref this.shutdownDrainTarget, this.circularBuffer.AddedCount);

        try
        {
            this.shutdownTrigger.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        OpenTelemetrySdkEventSource.Log.DroppedExportProcessorItems(this.GetType().Name, this.exporter.GetType().Name, this.DroppedCount);

        if (timeoutMilliseconds == Timeout.Infinite)
        {
            this.exporterThread.Join();
            return this.exporter.Shutdown();
        }

        if (timeoutMilliseconds == 0)
        {
            return this.exporter.Shutdown(0);
        }

        var sw = Stopwatch.StartNew();
        this.exporterThread.Join(timeoutMilliseconds);
        var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;
        return this.exporter.Shutdown((int)Math.Max(timeout, 0));
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.exportTrigger.Dispose();
                this.dataExportedNotification.Dispose();
                this.shutdownTrigger.Dispose();
            }

            this.disposed = true;
        }

        base.Dispose(disposing);
    }

    private void ExporterProc()
    {
        var triggers = new WaitHandle[] { this.exportTrigger, this.shutdownTrigger };

        while (true)
        {
            if (this.circularBuffer.Count < this.MaxExportBatchSize)
            {
                try
                {
                    WaitHandle.WaitAny(triggers, this.ScheduledDelayMilliseconds);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }

            if (this.circularBuffer.Count > 0)
            {
                using (var batch = new Batch<T>(this.circularBuffer, this.MaxExportBatchSize))
                {
                    this.exporter.Export(batch);
                }

                try
                {
                    this.dataExportedNotification.Set();
                    this.dataExportedNotification.Reset();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }

            if (this.circularBuffer.RemovedCount >= Volatile.Read(ref this.shutdownDrainTarget))
                return;
        }
    }
}
//-------------------------------------------Ʌ

//---------------------------------------V
public class BatchActivityExportProcessor : BatchExportProcessor<Activity>
{
    public BatchActivityExportProcessor(
        BaseExporter<Activity> exporter,
        int maxQueueSize = DefaultMaxQueueSize,
        int scheduledDelayMilliseconds = DefaultScheduledDelayMilliseconds,
        int exporterTimeoutMilliseconds = DefaultExporterTimeoutMilliseconds,
        int maxExportBatchSize = DefaultMaxExportBatchSize)
        : base(
            exporter,
            maxQueueSize,
            scheduledDelayMilliseconds,
            exporterTimeoutMilliseconds,
            maxExportBatchSize)
    {
    }

    public override void OnEnd(Activity data)
    {
        if (!data.Recorded)
        {
            return;
        }

        this.OnExport(data);
    }
}
//---------------------------------------Ʌ
```

```C#
//-------------------------------------------------V
internal sealed class HttpHandlerDiagnosticListener : ListenerHandler
{
    internal const string HttpClientActivitySourceName = "System.Net.Http";

    internal static readonly AssemblyName AssemblyName = typeof(HttpHandlerDiagnosticListener).Assembly.GetName();

    internal static readonly bool IsNet7OrGreater = Environment.Version.Major >= 7;

    internal static readonly bool IsNet9OrGreater = Environment.Version.Major >= 9;

    internal static readonly string ActivitySourceName = AssemblyName.Name + ".HttpClient";

    internal static readonly Version Version = AssemblyName.Version;

    internal static readonly ActivitySource ActivitySource = new ActivitySource(ActivitySourceName, Version.ToString());

    private const string OnStartEvent = "System.Net.Http.HttpRequestOut.Start";

    private const string OnStopEvent = "System.Net.Http.HttpRequestOut.Stop";

    private const string OnUnhandledExceptionEvent = "System.Net.Http.Exception";

    private static readonly PropertyFetcher<HttpRequestMessage> StartRequestFetcher = new PropertyFetcher<HttpRequestMessage>("Request");

    private static readonly PropertyFetcher<HttpResponseMessage> StopResponseFetcher = new PropertyFetcher<HttpResponseMessage>("Response");

    private static readonly PropertyFetcher<Exception> StopExceptionFetcher = new PropertyFetcher<Exception>("Exception");

    private static readonly PropertyFetcher<TaskStatus> StopRequestStatusFetcher = new PropertyFetcher<TaskStatus>("RequestTaskStatus");

    private readonly HttpClientTraceInstrumentationOptions options;

    public HttpHandlerDiagnosticListener(HttpClientTraceInstrumentationOptions options)
        : base("HttpHandlerDiagnosticListener")
    {
        this.options = options;
    }

    public override void OnEventWritten(string name, object? payload)
    {
        Activity current = Activity.Current;
        switch (name)
        {
            case "System.Net.Http.HttpRequestOut.Start":
                OnStartActivity(current, payload);
                break;
            case "System.Net.Http.HttpRequestOut.Stop":
                OnStopActivity(current, payload);
                break;
            case "System.Net.Http.Exception":
                OnException(current, payload);
                break;
        }
    }

    public void OnStartActivity(Activity activity, object? payload)
    {
        if (!TryFetchRequest(payload, out var request2))
        {
            HttpInstrumentationEventSource.Log.NullPayload("HttpHandlerDiagnosticListener", "OnStartActivity");
            return;
        }

        TextMapPropagator defaultTextMapPropagator = Propagators.DefaultTextMapPropagator;
        if (!(defaultTextMapPropagator is TraceContextPropagator))
        {
            defaultTextMapPropagator.Inject(new PropagationContext(activity.Context, Baggage.Current), request2, HttpRequestMessageContextPropagation.HeaderValueSetter);
        }

        if (IsNet7OrGreater && string.IsNullOrEmpty(activity.Source.Name))
        {
            activity.IsAllDataRequested = false;
        }

        if (!activity.IsAllDataRequested)
        {
            return;
        }

        try
        {
            if (!options.EventFilterHttpRequestMessage(activity.OperationName, request2))
            {
                HttpInstrumentationEventSource.Log.RequestIsFilteredOut(activity.OperationName);
                activity.IsAllDataRequested = false;
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }
        }
        catch (Exception ex)
        {
            HttpInstrumentationEventSource.Log.RequestFilterException(ex);
            activity.IsAllDataRequested = false;
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        HttpTagHelper.RequestDataHelper.SetActivityDisplayName(activity, request2.Method.Method);
        if (!IsNet7OrGreater)
        {
            ActivityInstrumentationHelper.SetActivitySourceProperty(activity, ActivitySource);
            ActivityInstrumentationHelper.SetKindProperty(activity, ActivityKind.Client);
        }

        if (!IsNet9OrGreater)
        {
            HttpTagHelper.RequestDataHelper.SetHttpMethodTag(activity, request2.Method.Method);
            if (request2.RequestUri != null)
            {
                activity.SetTag("server.address", request2.RequestUri.Host);
                activity.SetTag("server.port", request2.RequestUri.Port);
                activity.SetTag("url.full", HttpTagHelper.GetUriTagValueFromRequestUri(request2.RequestUri, options.DisableUrlQueryRedaction));
            }
        }

        try
        {
            options.EnrichWithHttpRequestMessage?.Invoke(activity, request2);
        }
        catch (Exception ex2)
        {
            HttpInstrumentationEventSource.Log.EnrichmentException(ex2);
        }

        static bool TryFetchRequest(object? payload, [NotNullWhen(true)] out HttpRequestMessage? request)
        {
            if (StartRequestFetcher.TryFetch(payload, out request))
            {
                return request != null;
            }

            return false;
        }
    }

    public void OnStopActivity(Activity activity, object? payload)
    {
        if (!activity.IsAllDataRequested)
        {
            return;
        }

        TaskStatus taskStatus = GetRequestStatus(payload);
        ActivityStatusCode status = activity.Status;
        switch (taskStatus)
        {
            case TaskStatus.Canceled:
                if (status == ActivityStatusCode.Unset)
                {
                    activity.SetStatus(ActivityStatusCode.Error, "Task Canceled");
                    activity.SetTag("error.type", typeof(TaskCanceledException).FullName);
                }

                break;
            default:
                if (status == ActivityStatusCode.Unset)
                {
                    activity.SetStatus(ActivityStatusCode.Error);
                }

                break;
            case TaskStatus.RanToCompletion:
            case TaskStatus.Faulted:
                break;
        }

        if (!TryFetchResponse(payload, out var response2))
        {
            return;
        }

        if (!IsNet9OrGreater)
        {
            if (status == ActivityStatusCode.Unset)
            {
                activity.SetStatus(SpanHelper.ResolveActivityStatusForHttpStatusCode(activity.Kind, (int)response2.StatusCode));
            }

            activity.SetTag("network.protocol.version", RequestDataHelper.GetHttpProtocolVersion(response2.Version));
            activity.SetTag("http.response.status_code", TelemetryHelper.GetBoxedStatusCode(response2.StatusCode));
            if (activity.Status == ActivityStatusCode.Error)
            {
                activity.SetTag("error.type", TelemetryHelper.GetStatusCodeString(response2.StatusCode));
            }
        }

        try
        {
            options.EnrichWithHttpResponseMessage?.Invoke(activity, response2);
        }
        catch (Exception ex)
        {
            HttpInstrumentationEventSource.Log.EnrichmentException(ex);
        }

        static TaskStatus GetRequestStatus(object? payload)
        {
            StopRequestStatusFetcher.TryFetch(payload, out var value);
            return value;
        }

        static bool TryFetchResponse(object? payload, [NotNullWhen(true)] out HttpResponseMessage? response)
        {
            if (StopResponseFetcher.TryFetch(payload, out response))
                return response != null;

            return false;
        }
    }

    public void OnException(Activity activity, object? payload)
    {
        if (!activity.IsAllDataRequested)
            return;

        if (!TryFetchException(payload, out var exc2))
        {
            HttpInstrumentationEventSource.Log.NullPayload("HttpHandlerDiagnosticListener", "OnException");
            return;
        }

        string errorType = GetErrorType(exc2);
        if (!string.IsNullOrEmpty(errorType))
            activity.SetTag("error.type", errorType);

        if (options.RecordException)
        {
            Exception exception = exc2;
            TagList tags = default(TagList);
            activity.AddException(exception, in tags);
        }

        if (exc2 is HttpRequestException)
            activity.SetStatus(ActivityStatusCode.Error);

        try
        {
            options.EnrichWithException?.Invoke(activity, exc2);
        }
        catch (Exception ex)
        {
            HttpInstrumentationEventSource.Log.EnrichmentException(ex);
        }

        static bool TryFetchException(object? payload, [NotNullWhen(true)] out Exception? exc)
        {
            if (StopExceptionFetcher.TryFetch(payload, out exc))
                return exc != null;

            return false;
        }
    }

    private static string? GetErrorType(Exception exc)
    {
        if (exc is HttpRequestException ex)
        {
            return ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "name_resolution_error",
                HttpRequestError.ConnectionError => "connection_error",
                HttpRequestError.SecureConnectionError => "secure_connection_error",
                HttpRequestError.HttpProtocolError => "http_protocol_error",
                HttpRequestError.ExtendedConnectNotSupported => "extended_connect_not_supported",
                HttpRequestError.VersionNegotiationError => "version_negotiation_error",
                HttpRequestError.UserAuthenticationError => "user_authentication_error",
                HttpRequestError.ProxyTunnelError => "proxy_tunnel_error",
                HttpRequestError.InvalidResponse => "invalid_response",
                HttpRequestError.ResponseEnded => "response_ended",
                HttpRequestError.ConfigurationLimitExceeded => "configuration_limit_exceeded",
                _ => exc.GetType().FullName,
            };
        }

        return exc.GetType().FullName;
    }
}
//-------------------------------------------------Ʌ
```

```C#
//-------------------------------------------------V
public static class ConsoleExporterHelperExtensions
{
    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder) => AddConsoleExporter(builder, name: null, configure: null);

    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder, Action<ConsoleExporterOptions> configure)
        => AddConsoleExporter(builder, name: null, configure);

    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder, string? name, Action<ConsoleExporterOptions>? configure)
    {
        name ??= Options.DefaultName;

        if (configure != null)
            builder.ConfigureServices(services => services.Configure(name, configure));

        return builder.AddProcessor(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ConsoleExporterOptions>>().Get(name);

            return new SimpleActivityExportProcessor(new ConsoleActivityExporter(options));
        });
    }
}
//-------------------------------------------------Ʌ

//--------------------------------------V
public abstract class ConsoleExporter<T> : BaseExporter<T> where T : class
{
    private readonly ConsoleExporterOptions options;

    internal ConsoleTagWriter TagWriter { get; }

    protected ConsoleExporter(ConsoleExporterOptions options)
    {
        this.options = options ?? new ConsoleExporterOptions();
        TagWriter = new ConsoleTagWriter(OnUnsupportedTagDropped);
    }

    protected void WriteLine(string message)
    {
        if (options.Targets.HasFlag(ConsoleExporterOutputTargets.Console))
        {
            Console.WriteLine(message);
        }

        if (options.Targets.HasFlag(ConsoleExporterOutputTargets.Debug))
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
    }

    private void OnUnsupportedTagDropped(string tagKey, string tagValueTypeFullName)
    {
        WriteLine($"Unsupported attribute value type '{tagValueTypeFullName}' for '{tagKey}'.");
    }
}
//--------------------------------------Ʌ

//----------------------------------V
public class ConsoleActivityExporter : ConsoleExporter<Activity>
{
    public ConsoleActivityExporter(ConsoleExporterOptions options) : base(options) { }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            this.WriteLine($"Activity.TraceId:            {activity.TraceId}");
            this.WriteLine($"Activity.SpanId:             {activity.SpanId}");
            this.WriteLine($"Activity.TraceFlags:         {activity.ActivityTraceFlags}");
            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                this.WriteLine($"Activity.TraceState:         {activity.TraceStateString}");
            }

            if (activity.ParentSpanId != default)
                this.WriteLine($"Activity.ParentSpanId:       {activity.ParentSpanId}");

            this.WriteLine($"Activity.DisplayName:        {activity.DisplayName}");
            this.WriteLine($"Activity.Kind:               {activity.Kind}");
            this.WriteLine($"Activity.StartTime:          {activity.StartTimeUtc:yyyy-MM-ddTHH:mm:ss.fffffffZ}");
            this.WriteLine($"Activity.Duration:           {activity.Duration}");
            var statusCode = string.Empty;
            var statusDesc = string.Empty;

            if (activity.TagObjects.Any())
            {
                this.WriteLine("Activity.Tags:");
                foreach (ref readonly var tag in activity.EnumerateTagObjects())
                {
                    if (tag.Key == SpanAttributeConstants.StatusCodeKey)
                    {
                        statusCode = tag.Value as string;
                        continue;
                    }

                    if (tag.Key == SpanAttributeConstants.StatusDescriptionKey)
                    {
                        statusDesc = tag.Value as string;
                        continue;
                    }

                    if (this.TagWriter.TryTransformTag(tag, out var result))
                        this.WriteLine($"    {result.Key}: {result.Value}");
                }
            }

            if (activity.Status != ActivityStatusCode.Unset)
            {
                this.WriteLine($"StatusCode: {activity.Status}");
                if (!string.IsNullOrEmpty(activity.StatusDescription))
                    this.WriteLine($"Activity.StatusDescription:  {activity.StatusDescription}");
            }
            else if (!string.IsNullOrEmpty(statusCode))
            {
                this.WriteLine($"    StatusCode: {statusCode}");
                if (!string.IsNullOrEmpty(statusDesc))
                    this.WriteLine($"    Activity.StatusDescription: {statusDesc}");
            }

            if (activity.Events.Any())
            {
                this.WriteLine("Activity.Events:");
                foreach (ref readonly var activityEvent in activity.EnumerateEvents())
                {
                    this.WriteLine($"    {activityEvent.Name} [{activityEvent.Timestamp}]");
                    foreach (ref readonly var attribute in activityEvent.EnumerateTagObjects())
                    {
                        if (this.TagWriter.TryTransformTag(attribute, out var result))
                            this.WriteLine($"        {result.Key}: {result.Value}");
                    }
                }
            }

            if (activity.Links.Any())
            {
                this.WriteLine("Activity.Links:");
                foreach (ref readonly var activityLink in activity.EnumerateLinks())
                {
                    this.WriteLine($"    {activityLink.Context.TraceId} {activityLink.Context.SpanId}");
                    foreach (ref readonly var attribute in activityLink.EnumerateTagObjects())
                    {
                        if (this.TagWriter.TryTransformTag(attribute, out var result))
                            this.WriteLine($"        {result.Key}: {result.Value}");
                    }
                }
            }

            this.WriteLine("Instrumentation scope (ActivitySource):");
            this.WriteLine($"    Name: {activity.Source.Name}");
            if (!string.IsNullOrEmpty(activity.Source.Version))
                this.WriteLine($"    Version: {activity.Source.Version}");

            if (activity.Source.Tags?.Any() == true)
            {
                this.WriteLine("    Tags:");
                foreach (var activitySourceTag in activity.Source.Tags)
                {
                    if (this.TagWriter.TryTransformTag(activitySourceTag, out var result))
                        this.WriteLine($"        {result.Key}: {result.Value}");
                }
            }

            var resource = this.ParentProvider.GetResource();
            if (resource != Resource.Empty)
            {
                this.WriteLine("Resource associated with Activity:");
                foreach (var resourceAttribute in resource.Attributes)
                {
                    if (this.TagWriter.TryTransformTag(resourceAttribute.Key, resourceAttribute.Value, out var result))
                    {
                        this.WriteLine($"    {result.Key}: {result.Value}");
                    }
                }
            }

            this.WriteLine(string.Empty);
        }

        return ExportResult.Success;
    }
}
//----------------------------------Ʌ
```

```C#
//------------------------------------------------V
public static class JaegerExporterHelperExtensions
{
    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder)
        => AddJaegerExporter(builder, name: null, configure: null);

    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder, Action<JaegerExporterOptions> configure)
        => AddJaegerExporter(builder, name: null, configure);

    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder, string name, Action<JaegerExporterOptions> configure)
    {
        Guard.ThrowIfNull(builder);

        name ??= Options.DefaultName;

        builder.ConfigureServices(services =>
        {
            if (configure != null)
            {
                services.Configure(name, configure);
            }

            services.RegisterOptionsFactory(
                (sp, configuration, name) => new JaegerExporterOptions(
                    configuration,
                    sp.GetRequiredService<IOptionsMonitor<BatchExportActivityProcessorOptions>>().Get(name)));
        });

        return builder.AddProcessor(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<JaegerExporterOptions>>().Get(name);

            return BuildJaegerExporterProcessor(options, sp);
        });
    }

    private static BaseProcessor<Activity> BuildJaegerExporterProcessor(
        JaegerExporterOptions options,
        IServiceProvider serviceProvider)
    {
        if (options.Protocol == JaegerExportProtocol.HttpBinaryThrift
            && options.HttpClientFactory == JaegerExporterOptions.DefaultHttpClientFactory)
        {
            options.HttpClientFactory = () =>
            {
                Type httpClientFactoryType = Type.GetType("System.Net.Http.IHttpClientFactory, Microsoft.Extensions.Http", throwOnError: false);
                if (httpClientFactoryType != null)
                {
                    object httpClientFactory = serviceProvider.GetService(httpClientFactoryType);
                    if (httpClientFactory != null)
                    {
                        MethodInfo createClientMethod = httpClientFactoryType.GetMethod(
                            "CreateClient",
                            BindingFlags.Public | BindingFlags.Instance,
                            binder: null,
                            new Type[] { typeof(string) },
                            modifiers: null);
                        if (createClientMethod != null)
                        {
                            return (HttpClient)createClientMethod.Invoke(httpClientFactory, new object[] { "JaegerExporter" });
                        }
                    }
                }

                return new HttpClient();
            };
        }

        var jaegerExporter = new JaegerExporter(options);

        if (options.ExportProcessorType == ExportProcessorType.Simple)
        {
            return new SimpleActivityExportProcessor(jaegerExporter);
        }
        else
        {
            return new BatchActivityExportProcessor(
                jaegerExporter,
                options.BatchExportProcessorOptions.MaxQueueSize,
                options.BatchExportProcessorOptions.ScheduledDelayMilliseconds,
                options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds,
                options.BatchExportProcessorOptions.MaxExportBatchSize);
        }
    }
}
//------------------------------------------------Ʌ

//--------------------------------------------------V
public class JaegerExporter : BaseExporter<Activity>
{
    internal uint NumberOfSpansInCurrentBatch;

    private readonly byte[] uInt32Storage = new byte[8];
    private readonly int maxPayloadSizeInBytes;
    private readonly IJaegerClient client;
    private readonly TProtocol batchWriter;
    private readonly TProtocol spanWriter;
    private readonly bool sendUsingEmitBatchArgs;
    private int minimumBatchSizeInBytes;
    private int currentBatchSizeInBytes;
    private int spanStartPosition;
    private uint sequenceId;
    private bool disposed;

    public JaegerExporter(JaegerExporterOptions options) : this(options, null) { }

    internal JaegerExporter(JaegerExporterOptions options, TProtocolFactory protocolFactory = null, IJaegerClient client = null)
    {
        this.maxPayloadSizeInBytes = (!options.MaxPayloadSizeInBytes.HasValue || options.MaxPayloadSizeInBytes <= 0)
            ? JaegerExporterOptions.DefaultMaxPayloadSizeInBytes
            : options.MaxPayloadSizeInBytes.Value;

        if (options.Protocol == JaegerExportProtocol.UdpCompactThrift)
        {
            protocolFactory ??= new TCompactProtocol.Factory();
            client ??= new JaegerUdpClient(options.AgentHost, options.AgentPort);
            this.sendUsingEmitBatchArgs = true;
        }
        else if (options.Protocol == JaegerExportProtocol.HttpBinaryThrift)
        {
            protocolFactory ??= new TBinaryProtocol.Factory();
            client ??= new JaegerHttpClient(
                options.Endpoint,
                options.HttpClientFactory?.Invoke() ?? throw new InvalidOperationException("JaegerExporterOptions was missing HttpClientFactory or it returned null."));
        }
        else
        {
            throw new NotSupportedException();
        }

        this.client = client;
        this.batchWriter = protocolFactory.GetProtocol(this.maxPayloadSizeInBytes * 2);
        this.spanWriter = protocolFactory.GetProtocol(this.maxPayloadSizeInBytes);

        this.Process = new();

        client.Connect();
    }

    internal Process Process { get; }

    internal EmitBatchArgs EmitBatchArgs { get; private set; }

    internal Batch Batch { get; private set; }

    public override ExportResult Export(in Batch<Activity> activityBatch)
    {
        try
        {
            if (this.Batch == null)
            {
                this.SetResourceAndInitializeBatch(this.ParentProvider.GetResource());
            }

            foreach (var activity in activityBatch)
            {
                var jaegerSpan = activity.ToJaegerSpan();
                this.AppendSpan(jaegerSpan);
                jaegerSpan.Return();
            }

            this.SendCurrentBatch();

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            JaegerExporterEventSource.Log.FailedExport(ex);

            return ExportResult.Failure;
        }
    }

    internal void SetResourceAndInitializeBatch(Resource resource)
    {
        Guard.ThrowIfNull(resource);

        var process = this.Process;

        string serviceName = null;
        string serviceNamespace = null;
        foreach (var label in resource.Attributes)
        {
            string key = label.Key;

            if (label.Value is string strVal)
            {
                switch (key)
                {
                    case ResourceSemanticConventions.AttributeServiceName:
                        serviceName = strVal;
                        continue;
                    case ResourceSemanticConventions.AttributeServiceNamespace:
                        serviceNamespace = strVal;
                        continue;
                }
            }

            if (JaegerTagTransformer.Instance.TryTransformTag(label, out var result))
            {
                if (process.Tags == null)
                {
                    process.Tags = new Dictionary<string, JaegerTag>();
                }

                process.Tags[key] = result;
            }
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = string.IsNullOrEmpty(serviceNamespace)
                ? serviceName
                : serviceNamespace + "." + serviceName;
        }
        else
        {
            serviceName = (string)this.ParentProvider.GetDefaultResource().Attributes.FirstOrDefault(
                pair => pair.Key == ResourceSemanticConventions.AttributeServiceName).Value;
        }

        process.ServiceName = serviceName;

        this.Batch = new Batch(process, this.batchWriter);
        if (this.sendUsingEmitBatchArgs)
        {
            this.EmitBatchArgs = new EmitBatchArgs(this.batchWriter);
            this.Batch.SpanCountPosition += this.EmitBatchArgs.EmitBatchArgsBeginMessage.Length;
            this.batchWriter.WriteRaw(this.EmitBatchArgs.EmitBatchArgsBeginMessage);
        }

        this.batchWriter.WriteRaw(this.Batch.BatchBeginMessage);
        this.spanStartPosition = this.batchWriter.Position;

        this.minimumBatchSizeInBytes = this.EmitBatchArgs?.MinimumMessageSize ?? 0
            + this.Batch.MinimumMessageSize;

        this.ResetBatch();
    }

    internal void AppendSpan(JaegerSpan jaegerSpan)
    {
        jaegerSpan.Write(this.spanWriter);
        try
        {
            var spanTotalBytesNeeded = this.spanWriter.Length;

            if (this.NumberOfSpansInCurrentBatch > 0
                && this.currentBatchSizeInBytes + spanTotalBytesNeeded >= this.maxPayloadSizeInBytes)
            {
                this.SendCurrentBatch();
            }

            var spanData = this.spanWriter.WrittenData;
            this.batchWriter.WriteRaw(spanData);

            this.NumberOfSpansInCurrentBatch++;
            this.currentBatchSizeInBytes += spanTotalBytesNeeded;
        }
        finally
        {
            this.spanWriter.Clear();
        }
    }

    internal void SendCurrentBatch()
    {
        try
        {
            this.batchWriter.WriteRaw(this.Batch.BatchEndMessage);

            if (this.sendUsingEmitBatchArgs)
            {
                this.batchWriter.WriteRaw(this.EmitBatchArgs.EmitBatchArgsEndMessage);

                this.WriteUInt32AtPosition(this.EmitBatchArgs.SeqIdPosition, ++this.sequenceId);
            }

            this.WriteUInt32AtPosition(this.Batch.SpanCountPosition, this.NumberOfSpansInCurrentBatch);

            var writtenData = this.batchWriter.WrittenData;

            this.client.Send(writtenData.Array, writtenData.Offset, writtenData.Count);
        }
        finally
        {
            this.ResetBatch();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                try
                {
                    this.client.Close();
                }
                catch
                {
                }

                this.client.Dispose();
                this.batchWriter.Dispose();
                this.spanWriter.Dispose();
            }

            this.disposed = true;
        }

        base.Dispose(disposing);
    }

    private void WriteUInt32AtPosition(int position, uint value)
    {
        this.batchWriter.Position = position;
        int numberOfBytes = this.batchWriter.WriteUI32(value, this.uInt32Storage);
        this.batchWriter.WriteRaw(this.uInt32Storage, 0, numberOfBytes);
    }

    private void ResetBatch()
    {
        this.currentBatchSizeInBytes = this.minimumBatchSizeInBytes;
        this.NumberOfSpansInCurrentBatch = 0;
        this.batchWriter.Clear(this.spanStartPosition);
    }
}

//--------------------------------------------------Ʌ

//--------------------------------V
public class JaegerExporterOptions
{
    internal const int DefaultMaxPayloadSizeInBytes = 4096;

    internal const string OTelProtocolEnvVarKey = "OTEL_EXPORTER_JAEGER_PROTOCOL";
    internal const string OTelAgentHostEnvVarKey = "OTEL_EXPORTER_JAEGER_AGENT_HOST";
    internal const string OTelAgentPortEnvVarKey = "OTEL_EXPORTER_JAEGER_AGENT_PORT";
    internal const string OTelEndpointEnvVarKey = "OTEL_EXPORTER_JAEGER_ENDPOINT";
    internal const string DefaultJaegerEndpoint = "http://localhost:14268/api/traces";  // <---------note thatPort 16686 is for viewing and analyzing trace data in the Jaeger UI.

    internal static readonly Func<HttpClient> DefaultHttpClientFactory = () => new HttpClient();

    public JaegerExporterOptions() : this(new ConfigurationBuilder().AddEnvironmentVariables().Build(), new()) { }

    internal JaegerExporterOptions(IConfiguration configuration, BatchExportActivityProcessorOptions defaultBatchOptions)
    {
        if (configuration.TryGetValue<JaegerExportProtocol>(OTelProtocolEnvVarKey, JaegerExporterProtocolParser.TryParse, out var protocol))
            this.Protocol = protocol;

        if (configuration.TryGetStringValue(OTelAgentHostEnvVarKey, out var agentHost))
            this.AgentHost = agentHost;

        if (configuration.TryGetIntValue(OTelAgentPortEnvVarKey, out var agentPort))
            this.AgentPort = agentPort;

        if (configuration.TryGetUriValue(OTelEndpointEnvVarKey, out var endpoint))
            this.Endpoint = endpoint;

        this.BatchExportProcessorOptions = defaultBatchOptions;
    }

    public JaegerExportProtocol Protocol { get; set; } = JaegerExportProtocol.UdpCompactThrift;

    public string AgentHost { get; set; } = "localhost";

    public int AgentPort { get; set; } = 6831;

    public Uri Endpoint { get; set; } = new Uri(DefaultJaegerEndpoint);

    public int? MaxPayloadSizeInBytes { get; set; } = DefaultMaxPayloadSizeInBytes;

    public ExportProcessorType ExportProcessorType { get; set; } = ExportProcessorType.Batch;

    public BatchExportProcessorOptions<Activity> BatchExportProcessorOptions { get; set; }

    public Func<HttpClient> HttpClientFactory { get; set; } = DefaultHttpClientFactory;
}
//--------------------------------Ʌ
```