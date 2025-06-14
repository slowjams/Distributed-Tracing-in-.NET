## OpenTelemetry

```C#
public class Program
{
    public static void Main(string[] args)
    {
        // ...
        builder.Services
            .AddOpenTelemetry()  // register TelemetryHostedService as IHostedService
            .WithTracing(builder =>  // builder is TracerProviderBuilder
                builder
                .AddAspNetCoreInstrumentation(opt =>   // <------------!important add listener(`HttpInListener`) for ActivitySource("Microsoft.AspNetCore") 
                {                 
                    opt.EnrichWithHttpRequest = (activity, httpRequestMessage) =>  // opt is AspNetCoreTraceInstrumentationOptions, // <-----------see ehr
                    {
                        activity.SetTag("myTags.method", httpRequestMessage.Method);
                        activity.SetTag("myTags.url", httpRequestMessage.Path);
                        activity.SetBaggage("UserId", "1234");
                    };
                               
                    opt.Filter = httpContext => !IsStaticFile(httpContext.Request.Path)
                })
                //.SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))  <-----------------------this is the default sampler if you don't specify one
                .AddProcessor<MemeNameEnrichingProcessor>()  // AddOtlpExporter uses its own processor,  MemeNameEnrichingProcessor is registered first, it will be called first
                .AddHttpClientInstrumentation(options =>  // <-------------------!important add listener(`HttpHandlerDiagnosticListener`) for ActivitySource("System.Net.Http") 
                {
                    options.EnrichWithHttpRequestMessage = (act, httpRequestMessage) => 
                    {
                        if (httpRequestMessage.Options.TryGetValue(new HttpRequestOptionsKey<int>("try"), out var tryCount) && tryCount > 0)
                            act.SetTag("http.resend_count", tryCount);

                        act.SetTag("http.request_content_length", httpRequestMessage.Content?.Headers.ContentLength);
                    };

                    options.EnrichWithHttpResponseMessage = (activity, httpResponseMessage) =>  // response is HttpResponseMessage
                        activity.SetTag("http.response_content_length", httpResponseMessage.Content.Headers.ContentLength);

                    options.RecordException = true;
                })
                .AddConsoleExporter()
                .SetResourceBuilder(
                    ResourceBuilder.CreateDefault()  // <------------------rb0
                        .AddService("myApp")
                        .AddAttributes(new[] { new KeyValuePair<string, object>("env", builder.Environment.EnvironmentName) }))             
                .AddOtlpExporter(opts =>  // opts is OtlpExporterOptions
                {
                    opts.Protocol = OtlpExportProtocol.Grpc;  // no really need to set it as by default OTLP over gRPC, setting the protocol explicitly is a good practice
                    opts.Endpoint = new Uri("http://localhost:4317");  // port 4317 is OpenTelemetry Collector's gRPC receiver, need OpenTelemetry Collector as separate process
                                                                       // e.g using docker with image otel/opentelemetry-collector:latest
                })  // AddOtlpExporter uses BatchActivityExportProcessor
                /*
                .AddJaegerExporter(opts =>
                {
                    opts.Protocol = JaegerExportProtocol.Grpc;
                    opts.Endpoint = new Uri("http://localhost:14268/api/traces"); // OpenTelemetry Collector's Jaeger HTTP receiver , need OpenTelemetry Collector as separate process
                                                                                  // e.g using docker with image otel/opentelemetry-collector:latest            
                })
                .AddJaegerExporter(opts =>
                {
                    opts.Protocol = JaegerExportProtocol.Grpc;
                    opts.Endpoint = new Uri("http://jaeger.mydomain.local:14250"); // bypass OpenTelemetry Collector, send traces to Jaeger's gRPC endpoint directly
                })
                */
                .AddSource("Tracing.NET")  // <----------------- check acts to see how ActivityListener in tpsact can use this setting
        //...
    }

    /*
       port 4317 is OpenTelemetry Collector's gRPC receiver, Jaeger also expose this port to consumer OTEL messages then converts them to Jaeger compliant messages 
       port 4318 is OpenTelemetry Collector's HTTP receiver, Jaeger also expose this port to consumer OTEL messages then converts them to Jaeger compliant messages 

       port 14250 is OpenTelemetry Collector's Jaeger gRPC receiver, Jaeger also expose this port for directly consumption of Jaeger compliant messages 
       port 14268 is OpenTelemetry Collector's Jaeger HTTP receiver, Jaeger also expose this port for directly consumption of Jaeger compliant messages

       note that Jaeger has its internal component that consumes OTEL messages,  so it has many combinations between AddXXXExporter and otel collector, refer to Demystifying Tracing
       the purpose is to easy to swap between Jaeger and OTEL Collector as your trace ingestion point without changing client/exporter configuration
    */
}
```

if using Console app:

```C#
static void Main(string[] args)
{
    using var provider = Sdk.CreateTracerProviderBuilder()
        .ConfigureResource(b => b.AddService("activity-sample"))
        .AddSource("Worker")
        .AddJaegerExporter()
        .AddConsoleExporter()
        .Build()!;  // <----------------------Build() creates a `new TracerProviderSdk()` which creates an ActivityListener,
                    //  Build() is not requred for web api as above because TelemetryHostedService does the job
    // ...  
}
```


```C#
class MemeNameEnrichingProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        var name = GetName(activity);
        if (name != null) 
            activity.SetTag("meme_name", name);
    }

    private string? GetName(Activity activity)
    {
        if (Baggage.Current.GetBaggage().TryGetValue("meme_name", out var name)) 
            return name;

        return activity.GetBaggageItem("meme_name");
    }
}
```

```C#
public class WeatherForecastController : ControllerBase
{
    // ...
    private static readonly ActivitySource _activitySource = new("Tracing.NET");

    [HttpGet("OutgoingHttp")]
    public async Task OutgoingHttpRequest()
    {
        using var activity = _activitySource.StartActivity("AnotherOne");  // <-------------check tpsact see why ActivityListener is not null
        activity.SetTag("myTags.count", 1);
        var userId = Activity.Current.GetBaggageItem("UserId");
        activity.AddTag("UserId", userId);

        var client = new HttpClient();

        var response = await client.GetAsync("https://code-maze.com");
        response.EnsureSuccessStatusCode();
    }
}
```

```yaml
# NET Activity	             OTLP Field (Protobuf)            Jaeger Field (Protobuf)
Activity.TraceId	         span.trace_id                    span.traceId
Activity.SpanId	             span.span_id                     span.spanId
Activity.ParentSpanId	     span.parent_span_id              span.parentSpanId
Activity.DisplayName	     span.name                        span.operationName
Activity.Kind	             span.kind                        span.spanKind 
Activity.StartTimeUtc	     span.start_time_unix_nano        span.startTime 
Activity.EndTimeUtc	         span.end_time_unix_nano          span.endTime 
Activity.Tags	             span.attributes                  span.tags 
Activity.Events	             span.events                      span.logs 
Activity.Links	             span.links                       span.references
Activity.Status	             span.status                      span.status 
```

================================================================================================================================================================

The OTEL Request Pipeline (pip) is:

0. `TelemetryHostedService.Initialize()` calls `var tracerProvider = serviceProvider!.GetService<TracerProvider>();` 

1. DI `.TryAddSingleton<TracerProvider>(sp => new TracerProviderSdk(sp, ownsServiceProvider: false))`
   so `new TracerProviderSdk()` is created

2. Inside `TracerProviderSdk`'s Contructor, it does  `var activityListener = new ActivityListener()` and then register activityListener.ActivityStopped to call `processor.OnEnd(activity)`


## Source Code

```C#
//-------------------------------------------------V
public static class OpenTelemetryServicesExtensions
{
    public static OpenTelemetryBuilder AddOpenTelemetry(this IServiceCollection services)  // <--------------ote0
    {
        if (!services.Any((ServiceDescriptor d) => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TelemetryHostedService)))
        {
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, TelemetryHostedService>());  // <--------ote0.1. to make sure it run even before GenericWebHostService
        }

        return new OpenTelemetryBuilder(services); // <----------------------------ote1.1.
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

        var tracerProvider = serviceProvider!.GetService<TracerProvider>();  // <-----------------------------------------ote1.0, pip
                                                                             // DI will hold a reference to it so it won't garbage collected
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

//-------------------------V
public class LoggerProvider : BaseProvider
{
    protected LoggerProvider() { }

    public Logger GetLogger() => this.GetLogger(name: null, version: null);

    public Logger GetLogger(string? name) => this.GetLogger(name, version: null);

    public Logger GetLogger(string? name, string? version)
    {
        if (!this.TryCreateLogger(name, out var logger))
        {
            return NoopLogger;
        }

        logger!.SetInstrumentationScope(version);

        return logger;
    }

    internal virtual bool TryCreateLogger(string? name, out Logger? logger)
    {
        logger = null;
        return false;
    }
}
//-------------------------Ʌ

//--------------------------------------------V
internal sealed class LoggerProviderBuilderSdk : LoggerProviderBuilder, ILoggerProviderBuilder
{
    private const string DefaultInstrumentationVersion = "1.0.0.0";

    private readonly IServiceProvider serviceProvider;
    private LoggerProviderSdk? loggerProvider;

    public LoggerProviderBuilderSdk(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public List<InstrumentationRegistration> Instrumentation { get; } = new();

    public ResourceBuilder? ResourceBuilder { get; private set; }

    public LoggerProvider? Provider => this.loggerProvider;

    public List<BaseProcessor<LogRecord>> Processors { get; } = new();

    public void RegisterProvider(LoggerProviderSdk loggerProvider)
    {
        if (this.loggerProvider != null)
            throw new NotSupportedException("LoggerProvider cannot be accessed while build is executing.");

        this.loggerProvider = loggerProvider;
    }

    public override LoggerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        this.Instrumentation.Add(
            new InstrumentationRegistration(
                typeof(TInstrumentation).Name,
                typeof(TInstrumentation).Assembly.GetName().Version?.ToString() ?? DefaultInstrumentationVersion,
                instrumentationFactory!()));

        return this;
    }

    public LoggerProviderBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        var resourceBuilder = this.ResourceBuilder ??= ResourceBuilder.CreateDefault();

        configure!(resourceBuilder);

        return this;
    }

    public LoggerProviderBuilder SetResourceBuilder(ResourceBuilder resourceBuilder)
    {
        this.ResourceBuilder = resourceBuilder;

        return this;
    }

    public LoggerProviderBuilder AddProcessor(BaseProcessor<LogRecord> processor)
    {
        this.Processors.Add(processor!);

        return this;
    }

    public LoggerProviderBuilder ConfigureBuilder(Action<IServiceProvider, LoggerProviderBuilder> configure)
    {
        configure!(this.serviceProvider, this);

        return this;
    }

    public LoggerProviderBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        throw new NotSupportedException("Services cannot be configured after ServiceProvider has been created.");
    }

    LoggerProviderBuilder IDeferredLoggerProviderBuilder.Configure(Action<IServiceProvider, LoggerProviderBuilder> configure)
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
internal sealed class LoggerProviderSdk : LoggerProvider
{
    internal readonly IServiceProvider ServiceProvider;
    internal IDisposable? OwnedServiceProvider;
    internal bool Disposed;
    internal int ShutdownCount;

    private readonly List<object> instrumentations = [];
    private ILogRecordPool? threadStaticPool = LogRecordThreadStaticPool.Instance;

    public LoggerProviderSdk(IServiceProvider serviceProvider, bool ownsServiceProvider)
    {

        var state = serviceProvider!.GetRequiredService<LoggerProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
        {
            this.OwnedServiceProvider = serviceProvider as IDisposable;
            Debug.Assert(this.OwnedServiceProvider != null, "ownedServiceProvider was null");
        }

        OpenTelemetrySdkEventSource.Log.LoggerProviderSdkEvent("Building LoggerProvider.");

        var configureProviderBuilders = serviceProvider!.GetServices<IConfigureLoggerProviderBuilder>();
        foreach (var configureProviderBuilder in configureProviderBuilders)
            configureProviderBuilder.ConfigureBuilder(serviceProvider!, state);

        var resourceBuilder = state.ResourceBuilder ?? ResourceBuilder.CreateDefault();
        resourceBuilder.ServiceProvider = serviceProvider;
        this.Resource = resourceBuilder.Build();

        // Note: Linq OrderBy performs a stable sort, which is a requirement here
        foreach (var processor in state.Processors.OrderBy(p => p.PipelineWeight))
            this.AddProcessor(processor);

        StringBuilder instrumentationFactoriesAdded = new StringBuilder();

        foreach (var instrumentation in state.Instrumentation)
        {
            if (instrumentation.Instance is not null)
                this.instrumentations.Add(instrumentation.Instance);

            instrumentationFactoriesAdded.Append(instrumentation.Name);
            instrumentationFactoriesAdded.Append(';');
        }

        if (instrumentationFactoriesAdded.Length != 0)
        {
            instrumentationFactoriesAdded.Remove(instrumentationFactoriesAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.LoggerProviderSdkEvent($"Instrumentations added = \"{instrumentationFactoriesAdded}\".");
        }

        OpenTelemetrySdkEventSource.Log.LoggerProviderSdkEvent("LoggerProviderSdk built successfully.");
    }

    public Resource Resource { get; }

    public List<object> Instrumentations => this.instrumentations;

    public BaseProcessor<LogRecord>? Processor { get; private set; }

    public ILogRecordPool LogRecordPool => this.threadStaticPool ?? LogRecordSharedPool.Current;

    public static bool ContainsBatchProcessor(BaseProcessor<LogRecord> processor)
    {
        if (processor is BatchExportProcessor<LogRecord>)
        {
            return true;
        }
        else if (processor is CompositeProcessor<LogRecord> compositeProcessor)
        {
            var current = compositeProcessor.Head;
            while (current != null)
            {
                if (ContainsBatchProcessor(current.Value))
                {
                    return true;
                }

                current = current.Next;
            }
        }

        return false;
    }

    public void AddProcessor(BaseProcessor<LogRecord> processor)
    {
        processor.SetParentProvider(this);

        if (this.threadStaticPool != null && ContainsBatchProcessor(processor))
        {
            OpenTelemetrySdkEventSource.Log.LoggerProviderSdkEvent("Using shared thread pool.");

            this.threadStaticPool = null;
        }

        StringBuilder processorAdded = new StringBuilder();

        if (this.Processor == null)
        {
            processorAdded.Append("Setting processor to '");
            processorAdded.Append(processor);
            processorAdded.Append('\'');

            this.Processor = processor;
        }
        else if (this.Processor is CompositeProcessor<LogRecord> compositeProcessor)
        {
            processorAdded.Append("Adding processor '");
            processorAdded.Append(processor);
            processorAdded.Append("' to composite processor");

            compositeProcessor.AddProcessor(processor);
        }
        else
        {
            processorAdded.Append("Creating new composite processor and adding new processor '");
            processorAdded.Append(processor);
            processorAdded.Append('\'');

            var newCompositeProcessor = new CompositeProcessor<LogRecord>(
            [
                this.Processor,
            ]);
            newCompositeProcessor.SetParentProvider(this);
            newCompositeProcessor.AddProcessor(processor);
            this.Processor = newCompositeProcessor;
        }

        OpenTelemetrySdkEventSource.Log.LoggerProviderSdkEvent($"Completed adding processor = \"{processorAdded}\".");
    }

    public bool ForceFlush(int timeoutMilliseconds = Timeout.Infinite)
    {
        try
        {
            return this.Processor?.ForceFlush(timeoutMilliseconds) ?? true;
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.LoggerProviderException(nameof(this.ForceFlush), ex);
            return false;
        }
    }

    public bool Shutdown(int timeoutMilliseconds)
    {
        if (Interlocked.Increment(ref this.ShutdownCount) > 1)
            return false; // shutdown already called

        try
        {
            return this.Processor?.Shutdown(timeoutMilliseconds) ?? true;
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.LoggerProviderException(nameof(this.Shutdown), ex);
            return false;
        }
    }

    protected override bool TryCreateLogger(string? name,out Logger? logger)
    {
        logger = new LoggerSdk(this, name);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.Disposed)
        {
            if (disposing)
            {
                foreach (var item in this.instrumentations)
                    (item as IDisposable)?.Dispose();

                this.instrumentations.Clear();

                // Wait for up to 5 seconds grace period
                this.Processor?.Shutdown(5000);
                this.Processor?.Dispose();
                this.Processor = null;

                this.OwnedServiceProvider?.Dispose();
                this.OwnedServiceProvider = null;
            }

            this.Disposed = true;
            OpenTelemetrySdkEventSource.Log.ProviderDisposed(nameof(LoggerProviderSdk));
        }

        base.Dispose(disposing);
    }
}
//-------------------------------------Ʌ

//-----------------------------V
internal sealed class LoggerSdk : Logger
{
    private readonly LoggerProviderSdk loggerProvider;

    public LoggerSdk(LoggerProviderSdk loggerProvider, string? name) : base(name)
    {

        this.loggerProvider = loggerProvider;
    }

    public override void EmitLog(in LogRecordData data, in LogRecordAttributeList attributes)
    {
        var provider = this.loggerProvider;
        var processor = provider.Processor;
        if (processor != null)
        {
            var pool = provider.LogRecordPool;

            var logRecord = pool.Rent();

            logRecord.Data = data;
            logRecord.ILoggerData = default;

            logRecord.Logger = this;

            logRecord.AttributeData = attributes.Export(ref logRecord.AttributeStorage);

            processor.OnEnd(logRecord);

            // attempt to return the LogRecord to the pool. This will no-op if a batch exporter has added a reference.
            pool.Return(logRecord);
        }
    }
}
//-----------------------------Ʌ
```

```C#
//--------------------------------------------------------------V
internal static class ProviderBuilderServiceCollectionExtensions
{
    public static IServiceCollection AddOpenTelemetryLoggerProviderBuilderServices(this IServiceCollection services)
    {
        services!.TryAddSingleton<LoggerProviderBuilderSdk>();  // <----------------------------------
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

    public static IServiceCollection AddOpenTelemetryTracerProviderBuilderServices(this IServiceCollection services)  // <------------pip
    {
        services!.TryAddSingleton<TracerProviderBuilderSdk>();  //<---------------------------------pip
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

    public static TracerProviderBuilder SetSampler(this TracerProviderBuilder tracerProviderBuilder, Sampler sampler)  // <--------------------sam0
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.SetSampler(sampler);  // <--------------------sam0.1
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
                                                                             // tracerProviderBuilder is TracerProviderBuilderBase      
    public static TracerProviderBuilder AddProcessor(this TracerProviderBuilder tracerProviderBuilder, BaseProcessor<Activity> processor) 
    {
        tracerProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is TracerProviderBuilderSdk tracerProviderBuilderSdk)
            {
                tracerProviderBuilderSdk.AddProcessor(processor);  // <--------------------------------------coe1.0
                                                                   // processor is new SimpleActivityExportProcessor(new ConsoleActivityExporter(options))
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

//---------------------------------------------------------------------------------V
public static class OpenTelemetryDependencyInjectionTracerProviderBuilderExtensions
{
    public static TracerProviderBuilder AddInstrumentation<T>(this TracerProviderBuilder tracerProviderBuilder) where T : class
    {
        tracerProviderBuilder.ConfigureServices(delegate (IServiceCollection services)
        {
            services.TryAddSingleton<T>();
        });
        tracerProviderBuilder.ConfigureBuilder(delegate (IServiceProvider sp, TracerProviderBuilder builder)
        {
            builder.AddInstrumentation(sp.GetRequiredService<T>);
        });
        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder AddInstrumentation<T>(this TracerProviderBuilder tracerProviderBuilder, T instrumentation) where T : class
    {
        T instrumentation2 = instrumentation;
        Guard.ThrowIfNull(instrumentation2, "instrumentation");
        tracerProviderBuilder.ConfigureBuilder(delegate (IServiceProvider sp, TracerProviderBuilder builder)
        {
            builder.AddInstrumentation(() => instrumentation2);
        });
        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder AddInstrumentation<T>(this TracerProviderBuilder tracerProviderBuilder, Func<IServiceProvider, T> instrumentationFactory) where T : class
    {
        Func<IServiceProvider, T> instrumentationFactory2 = instrumentationFactory;
        Guard.ThrowIfNull(instrumentationFactory2, "instrumentationFactory");
        tracerProviderBuilder.ConfigureBuilder(delegate (IServiceProvider sp, TracerProviderBuilder builder)
        {
            IServiceProvider sp2 = sp;
            builder.AddInstrumentation(() => instrumentationFactory2(sp2));
        });
        return tracerProviderBuilder;
    }

    //     The supplied OpenTelemetry.Trace.TracerProviderBuilder for chaining.
    public static TracerProviderBuilder AddInstrumentation<T>(this TracerProviderBuilder tracerProviderBuilder, Func<IServiceProvider, TracerProvider, T> instrumentationFactory) where T : class
    {
        Func<IServiceProvider, TracerProvider, T> instrumentationFactory2 = instrumentationFactory;
        tracerProviderBuilder.ConfigureBuilder(delegate (IServiceProvider sp, TracerProviderBuilder builder)
        {
            IServiceProvider sp2 = sp;
            ITracerProviderBuilder iTracerProviderBuilder = builder as ITracerProviderBuilder;
            if (iTracerProviderBuilder != null && iTracerProviderBuilder.Provider != null)
            {
                builder.AddInstrumentation(() => instrumentationFactory2(sp2, iTracerProviderBuilder.Provider));
            }
        });
        return tracerProviderBuilder;
    }

    public static TracerProviderBuilder ConfigureServices(this TracerProviderBuilder tracerProviderBuilder, Action<IServiceCollection> configure)
    {
        if (tracerProviderBuilder is ITracerProviderBuilder tracerProviderBuilder2)
            tracerProviderBuilder2.ConfigureServices(configure);

        return tracerProviderBuilder;
    }

    internal static TracerProviderBuilder ConfigureBuilder(this TracerProviderBuilder tracerProviderBuilder, Action<IServiceProvider, TracerProviderBuilder> configure)
    {
        if (tracerProviderBuilder is IDeferredTracerProviderBuilder deferredTracerProviderBuilder)
        {
            deferredTracerProviderBuilder.Configure(configure);
        }

        return tracerProviderBuilder;
    }
}
//---------------------------------------------------------------------------------Ʌ

//------------------------------------------------------------------------------------V
public static class OpenTelemetryDependencyInjectionTracingServiceCollectionExtensions
{
    public static IServiceCollection ConfigureOpenTelemetryTracerProvider(this IServiceCollection services, Action<TracerProviderBuilder> configure)
    {
        configure(new TracerProviderServiceCollectionBuilder(services));

        return services;
    }

    public static IServiceCollection ConfigureOpenTelemetryTracerProvider(this IServiceCollection services, Action<IServiceProvider, TracerProviderBuilder> configure)
    {
        services.AddSingleton<IConfigureTracerProviderBuilder>(new ConfigureTracerProviderBuilderCallbackWrapper(configure));  // <-------------------------------ote4.4

        return services;
    }

    private sealed class ConfigureTracerProviderBuilderCallbackWrapper : IConfigureTracerProviderBuilder
    {
        private readonly Action<IServiceProvider, TracerProviderBuilder> configure;

        public ConfigureTracerProviderBuilderCallbackWrapper(Action<IServiceProvider, TracerProviderBuilder> configure)
        {
            this.configure = configure;
        }

        public void ConfigureBuilder(IServiceProvider serviceProvider, TracerProviderBuilder tracerProviderBuilder)
        {
            this.configure(serviceProvider, tracerProviderBuilder);  // <-------------------------------ote4.5
            // configure is (sp, builder) => builder.AddInstrumentation(sp => new AspNetCoreInstrumentation(new HttpInListener(options)));
        }
    }
}
//------------------------------------------------------------------------------------Ʌ

//--------------------------------------------------------------------------V
public static class AspNetCoreInstrumentationTracerProviderBuilderExtensions
{
    public static TracerProviderBuilder AddAspNetCoreInstrumentation(this TracerProviderBuilder builder)
        => AddAspNetCoreInstrumentation(builder, name: null, configureAspNetCoreTraceInstrumentationOptions: null);

    public static TracerProviderBuilder AddAspNetCoreInstrumentation(this TracerProviderBuilder builder, Action<AspNetCoreTraceInstrumentationOptions>? configureAspNetCoreTraceInstrumentationOptions) => AddAspNetCoreInstrumentation(builder, name: null, configureAspNetCoreTraceInstrumentationOptions);

    public static TracerProviderBuilder AddAspNetCoreInstrumentation(this TracerProviderBuilder builder, string? name, Action<AspNetCoreTraceInstrumentationOptions>? configureAspNetCoreTraceInstrumentationOptions)  // <-------------------ote3.0
    {

        // note: Warm-up the status code and method mapping.
        _ = TelemetryHelper.BoxedStatusCodes;
        _ = TelemetryHelper.RequestDataHelper;

        name ??= Options.DefaultName;

        builder.ConfigureServices(services =>   // <---------------------------ote3.1
        {
            if (configureAspNetCoreTraceInstrumentationOptions != null)
                services.Configure(name, configureAspNetCoreTraceInstrumentationOptions);

            services.RegisterOptionsFactory(configuration => new AspNetCoreTraceInstrumentationOptions(configuration));
        });

        if (builder is IDeferredTracerProviderBuilder deferredTracerProviderBuilder)
        {
            deferredTracerProviderBuilder.Configure((sp, builder) =>
            {
                AddAspNetCoreInstrumentationSources(builder, name, sp);
            });
        }

        return builder.AddInstrumentation(sp =>  // <---------------------------ote3.2.
        {
            var options = sp.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(name);

            return new AspNetCoreInstrumentation(new HttpInListener(options));  // <----------------------------hil
        });
    }

    // note: This is used by unit tests.
    internal static TracerProviderBuilder AddAspNetCoreInstrumentation(this TracerProviderBuilder builder, HttpInListener listener, string? optionsName = null)
    {
        optionsName ??= Options.DefaultName;

        builder.AddAspNetCoreInstrumentationSources(optionsName);

        return builder.AddInstrumentation(new AspNetCoreInstrumentation(listener));
    }

    private static void AddAspNetCoreInstrumentationSources(this TracerProviderBuilder builder, string optionsName, IServiceProvider? serviceProvider = null)
    {
        // for .NET7.0 onwards activity will be created using activitySource, for .NET6.0 and below, we will continue to use legacy way.
        if (HttpInListener.Net7OrGreater)
        {
            // TODO: Check with .NET team to see if this can be prevented
            // as this allows user to override the ActivitySource.
            var activitySourceService = serviceProvider?.GetService<ActivitySource>();
            if (activitySourceService != null)
                builder.AddSource(activitySourceService.Name);
            else        
                builder.AddSource(HttpInListener.AspNetCoreActivitySourceName);   // for users not using hosting package?      
        }
        else
        {
            builder.AddSource(HttpInListener.ActivitySourceName);  // <----------------------
            builder.AddLegacySource(HttpInListener.ActivityOperationName); // for the activities created by AspNetCore
        }
        // ...
    }
}
//--------------------------------------------------------------------------Ʌ

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
            builder.AddSource("System.Net.Http");  // <----------------------------------! hci0
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

//---------------------------------------------V
internal sealed class HttpClientInstrumentation : IDisposable
{
    private static readonly HashSet<string> ExcludedDiagnosticSourceEventsNet7OrGreater =
    [
        "System.Net.Http.Request",
        "System.Net.Http.Response",
        "System.Net.Http.HttpRequestOut"
    ];

    private static readonly HashSet<string> ExcludedDiagnosticSourceEvents =
    [
        "System.Net.Http.Request",
        "System.Net.Http.Response"
    ];

    private readonly DiagnosticSourceSubscriber diagnosticSourceSubscriber;

    private readonly Func<string, object?, object?, bool> isEnabled = (eventName, _, _)
        => !ExcludedDiagnosticSourceEvents.Contains(eventName);

    private readonly Func<string, object?, object?, bool> isEnabledNet7OrGreater = (eventName, _, _)
        => !ExcludedDiagnosticSourceEventsNet7OrGreater.Contains(eventName);

    public HttpClientInstrumentation(HttpClientTraceInstrumentationOptions options)
    {
        this.diagnosticSourceSubscriber = HttpHandlerDiagnosticListener.IsNet7OrGreater
                ? new DiagnosticSourceSubscriber(new HttpHandlerDiagnosticListener(options), this.isEnabledNet7OrGreater, HttpInstrumentationEventSource.Log.UnknownErrorProcessingEvent)
                : new DiagnosticSourceSubscriber(new HttpHandlerDiagnosticListener(options), this.isEnabled, HttpInstrumentationEventSource.Log.UnknownErrorProcessingEvent);

        this.diagnosticSourceSubscriber.Subscribe();
    }

    public void Dispose()
    {
        this.diagnosticSourceSubscriber?.Dispose();
    }
}
//---------------------------------------------Ʌ

//---------------------------------------------V
internal sealed class AspNetCoreInstrumentation : IDisposable
{
    private static readonly HashSet<string> DiagnosticSourceEvents =
    [
        "Microsoft.AspNetCore.Hosting.HttpRequestIn",
        "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start",
        "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop",
        "Microsoft.AspNetCore.Diagnostics.UnhandledException",
        "Microsoft.AspNetCore.Hosting.UnhandledException"
    ];

    private readonly Func<string, object?, object?, bool> isEnabled = (eventName, _, _) => DiagnosticSourceEvents.Contains(eventName);

    private readonly DiagnosticSourceSubscriber diagnosticSourceSubscriber;

    public AspNetCoreInstrumentation(HttpInListener httpInListener)
    {
        // <----------------------------hil
        this.diagnosticSourceSubscriber = new DiagnosticSourceSubscriber(httpInListener, this.isEnabled, AspNetCoreInstrumentationEventSource.Log.UnknownErrorProcessingEvent);
        this.diagnosticSourceSubscriber.Subscribe();
    }

    public void Dispose()
    {
        this.diagnosticSourceSubscriber?.Dispose();
    }
}
//---------------------------------------------Ʌ

//-------------------------------------------------V
internal sealed class HttpHandlerDiagnosticListener : ListenerHandler
{
    internal const string HttpClientActivitySourceName = "System.Net.Http";  // <-------------------------------------
    internal static readonly AssemblyName AssemblyName = typeof(HttpHandlerDiagnosticListener).Assembly.GetName();
    internal static readonly bool IsNet7OrGreater = Environment.Version.Major >= 7;
    internal static readonly bool IsNet9OrGreater = Environment.Version.Major >= 9;

    internal static readonly string ActivitySourceName = AssemblyName.Name + ".HttpClient";
    internal static readonly Version Version = AssemblyName.Version!;
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version.ToString());

    private const string OnStartEvent = "System.Net.Http.HttpRequestOut.Start";
    private const string OnStopEvent = "System.Net.Http.HttpRequestOut.Stop";
    private const string OnUnhandledExceptionEvent = "System.Net.Http.Exception";

    private static readonly PropertyFetcher<HttpRequestMessage> StartRequestFetcher = new("Request");
    private static readonly PropertyFetcher<HttpResponseMessage> StopResponseFetcher = new("Response");
    private static readonly PropertyFetcher<Exception> StopExceptionFetcher = new("Exception");
    private static readonly PropertyFetcher<TaskStatus> StopRequestStatusFetcher = new("RequestTaskStatus");

    private readonly HttpClientTraceInstrumentationOptions options;

    public HttpHandlerDiagnosticListener(HttpClientTraceInstrumentationOptions options)
        : base("HttpHandlerDiagnosticListener")
    {
        this.options = options;
    }

    public override void OnEventWritten(string name, object? payload)
    {
        var activity = Activity.Current!;
        switch (name)
        {
            case OnStartEvent:
                {
                    this.OnStartActivity(activity, payload);
                }

                break;
            case OnStopEvent:
                {
                    this.OnStopActivity(activity, payload);
                }

                break;
            case OnUnhandledExceptionEvent:
                {
                    this.OnException(activity, payload);
                }

                break;
            default:
                break;
        }
    }

    public void OnStartActivity(Activity activity, object? payload)
    {
        if (!TryFetchRequest(payload, out var request))
        {
            HttpInstrumentationEventSource.Log.NullPayload(nameof(HttpHandlerDiagnosticListener), nameof(this.OnStartActivity));
            return;
        }

        // Propagate context irrespective of sampling decision
        var textMapPropagator = Propagators.DefaultTextMapPropagator;
        if (textMapPropagator is not TraceContextPropagator)
        {
            textMapPropagator.Inject(new PropagationContext(activity.Context, Baggage.Current), request, HttpRequestMessageContextPropagation.HeaderValueSetter);
        }

        if (IsNet7OrGreater && string.IsNullOrEmpty(activity.Source.Name))
        {
            activity.IsAllDataRequested = false;
        }

        if (activity.IsAllDataRequested)
        {
            try
            {
                if (!this.options.EventFilterHttpRequestMessage(activity.OperationName, request))
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

            RequestMethodHelper.SetActivityDisplayName(activity, request.Method.Method);  // <-----------sadn, that's why APM like Jaeger shows "App1 Post" with Route
                                                                                          // OnStopActivity will not set DisplayName neither
            if (!IsNet7OrGreater)
            {
                ActivityInstrumentationHelper.SetActivitySourceProperty(activity, ActivitySource);
                ActivityInstrumentationHelper.SetKindProperty(activity, ActivityKind.Client);
            }

            if (!IsNet9OrGreater)
            {
                RequestMethodHelper.SetHttpMethodTag(activity, request.Method.Method);

                if (request.RequestUri != null)
                {
                    activity.SetTag(SemanticConventions.AttributeServerAddress, request.RequestUri.Host);
                    activity.SetTag(SemanticConventions.AttributeServerPort, request.RequestUri.Port);
                    activity.SetTag(SemanticConventions.AttributeUrlFull, HttpTagHelper.GetUriTagValueFromRequestUri(request.RequestUri, this.options.DisableUrlQueryRedaction));
                }
            }

            try
            {
                this.options.EnrichWithHttpRequestMessage?.Invoke(activity, request);
            }
            catch (Exception ex)
            {
                HttpInstrumentationEventSource.Log.EnrichmentException(ex);
            }
        }

        static bool TryFetchRequest(object? payload, [NotNullWhen(true)] out HttpRequestMessage? request)
        {
            return StartRequestFetcher.TryFetch(payload, out request) && request != null;
        }
    }

    public void OnStopActivity(Activity activity, object? payload)
    {
        if (activity.IsAllDataRequested)
        {
            var requestTaskStatus = GetRequestStatus(payload);

            var currentStatusCode = activity.Status;
            if (requestTaskStatus != TaskStatus.RanToCompletion)
            {
                if (requestTaskStatus == TaskStatus.Canceled)
                {
                    if (currentStatusCode == ActivityStatusCode.Unset)
                    {                   
                        activity.SetStatus(ActivityStatusCode.Error, "Task Canceled");
                        activity.SetTag(SemanticConventions.AttributeErrorType, typeof(TaskCanceledException).FullName);
                    }
                }
                else if (requestTaskStatus != TaskStatus.Faulted)
                {
                    if (currentStatusCode == ActivityStatusCode.Unset)
                    {
                        // Faults are handled in OnException and should already have a span.Status of Error w/ Description.
                        activity.SetStatus(ActivityStatusCode.Error);
                    }
                }
            }

            if (TryFetchResponse(payload, out var response))
            {
                if (!IsNet9OrGreater)
                {
                    if (currentStatusCode == ActivityStatusCode.Unset)
                    {
                        activity.SetStatus(SpanHelper.ResolveActivityStatusForHttpStatusCode(activity.Kind, (int)response.StatusCode));
                    }

                    activity.SetTag(SemanticConventions.AttributeNetworkProtocolVersion, RequestDataHelper.GetHttpProtocolVersion(response.Version));
                    activity.SetTag(SemanticConventions.AttributeHttpResponseStatusCode, TelemetryHelper.GetBoxedStatusCode(response.StatusCode));
                    if (activity.Status == ActivityStatusCode.Error)
                    {
                        activity.SetTag(SemanticConventions.AttributeErrorType, TelemetryHelper.GetStatusCodeString(response.StatusCode));
                    }
                }

                try
                {
                    this.options.EnrichWithHttpResponseMessage?.Invoke(activity, response);
                }
                catch (Exception ex)
                {
                    HttpInstrumentationEventSource.Log.EnrichmentException(ex);
                }
            }

            static TaskStatus GetRequestStatus(object? payload)
            {
                _ = StopRequestStatusFetcher.TryFetch(payload, out var requestTaskStatus);

                return requestTaskStatus;
            }
        }

        static bool TryFetchResponse(object? payload, [NotNullWhen(true)] out HttpResponseMessage? response)
        {
            return StopResponseFetcher.TryFetch(payload, out response) && response != null;
        }
    }

    public void OnException(Activity activity, object? payload) { ... }
    private static string? GetErrorType(Exception exc) { ... }
}
//-------------------------------------------------Ʌ

//---------------------------V
internal class HttpInListener : ListenerHandler
{
    internal const string ActivityOperationName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    internal const string OnStartEvent = "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start";
    internal const string OnStopEvent = "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop";
    internal const string OnUnhandledHostingExceptionEvent = "Microsoft.AspNetCore.Hosting.UnhandledException";
    internal const string OnUnHandledDiagnosticsExceptionEvent = "Microsoft.AspNetCore.Diagnostics.UnhandledException";

    internal const string AspNetCoreActivitySourceName = "Microsoft.AspNetCore";

    internal static readonly AssemblyName AssemblyName = typeof(HttpInListener).Assembly.GetName();
    internal static readonly string ActivitySourceName = AssemblyName.Name!;
    internal static readonly Version Version = AssemblyName.Version!;
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version.ToString());
    internal static readonly bool Net7OrGreater = Environment.Version.Major >= 7;

    private const string DiagnosticSourceName = "Microsoft.AspNetCore";

    private static readonly Func<HttpRequest, string, IEnumerable<string>> HttpRequestHeaderValuesGetter = (request, name) =>
    {
        if (request.Headers.TryGetValue(name, out var value))
        {
            // This causes allocation as the `StringValues` struct has to be casted to an `IEnumerable<string>` object.
            return value;
        }

        return [];
    };

    private static readonly PropertyFetcher<Exception> ExceptionPropertyFetcher = new("Exception");

    private readonly AspNetCoreTraceInstrumentationOptions options;

    public HttpInListener(AspNetCoreTraceInstrumentationOptions options) : base(DiagnosticSourceName)
    {
        this.options = options;
    }

    public override void OnEventWritten(string name, object? payload)  // <--------------hil
    {
        var activity = Activity.Current!;  // <---------! the source DiagnosticsHandler doesn't pass Activity, the HttpInListener has to get it by itself,  that is the pattern

        switch (name)
        {
            case OnStartEvent:
            {
                this.OnStartActivity(activity, payload);  // <----------------------
            }

                break;
            case OnStopEvent:
            {
                this.OnStopActivity(activity, payload);  // <----------------------
            }

                break;
            case OnUnhandledHostingExceptionEvent:
            case OnUnHandledDiagnosticsExceptionEvent:
            {
                this.OnException(activity, payload);  // <----------------------oe, related to HttpClientTraceInstrumentationOptions.RecordException
            }

                break;
            default:
                break;
        }
    }

    public void OnStartActivity(Activity activity, object? payload)  // <-------------------hil, see the counterpart at dlr3.4
    {
        // The overall flow of what AspNetCore library does is as below:
        // Activity.Start()
        // DiagnosticSource.WriteEvent("Start", payload)
        // DiagnosticSource.WriteEvent("Stop", payload)
        // Activity.Stop()

        // This method is in the WriteEvent("Start", payload) path.
        // By this time, samplers have already run and
        // activity.IsAllDataRequested populated accordingly.

        if (payload is not HttpContext context)
        {
            AspNetCoreInstrumentationEventSource.Log.NullPayload(nameof(HttpInListener), nameof(this.OnStartActivity), activity.OperationName);
            return;
        }

        // Ensure context extraction irrespective of sampling decision
        var request = context.Request;
        var textMapPropagator = Propagators.DefaultTextMapPropagator;
        if (textMapPropagator is not TraceContextPropagator)
        {
            var ctx = textMapPropagator.Extract(default, request, HttpRequestHeaderValuesGetter);
            if (ctx.ActivityContext.IsValid() && !((ctx.ActivityContext.TraceId == activity.TraceId) && (ctx.ActivityContext.SpanId == activity.ParentSpanId) && (ctx.ActivityContext.TraceState == activity.TraceStateString)))
            {
                // Create a new activity with its parent set from the extracted context.
                // This makes the new activity as a "sibling" of the activity created by
                // Asp.Net Core.
                Activity? newOne;
                if (Net7OrGreater)
                {
                    // For NET7.0 onwards activity is created using ActivitySource so, we will use the source of the activity to create the new one.
                    newOne = activity.Source.CreateActivity(ActivityOperationName, ActivityKind.Server, ctx.ActivityContext);
                }
                else
                {
                    newOne = new Activity(ActivityOperationName);
                    newOne.SetParentId(ctx.ActivityContext.TraceId, ctx.ActivityContext.SpanId, ctx.ActivityContext.TraceFlags);
                }

                newOne!.TraceStateString = ctx.ActivityContext.TraceState;

                newOne.SetTag("IsCreatedByInstrumentation", bool.TrueString);

                // Starting the new activity make it the Activity.Current one.
                newOne.Start();

                // Set IsAllDataRequested to false for the activity created by the framework to only export the sibling activity and not the framework activity
                activity.IsAllDataRequested = false;
                activity = newOne;
            }

            Baggage.Current = ctx.Baggage;
        }

        // enrich Activity from payload only if sampling decision is favorable.
        if (activity.IsAllDataRequested)
        {
            try
            {
                if (this.options.Filter?.Invoke(context) == false)  // <--------------hil, ofl
                {
                    AspNetCoreInstrumentationEventSource.Log.RequestIsFilteredOut(nameof(HttpInListener), nameof(this.OnStartActivity), activity.OperationName);
                    activity.IsAllDataRequested = false;
                    activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;  // <-------------hil, Fiter can even change the sampling bits, which make senses
                                                                                  // but since it changes the original activity, shouldn't it a small chance/race that
                                                                                  // a child activity will be created and sampled in and pass to downstream service?
                                                                                  // well actully no, because this change sampling bits are like atomic operation becausse 
                                                                                  // it executes right after HostingApplicationDiagnostics creates an activity
                    return;
                }
            }
            catch (Exception ex)
            {
                AspNetCoreInstrumentationEventSource.Log.RequestFilterException(nameof(HttpInListener), nameof(this.OnStartActivity), activity.OperationName, ex);
                activity.IsAllDataRequested = false;
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }

            if (!Net7OrGreater)
            {
                ActivityInstrumentationHelper.SetActivitySourceProperty(activity, ActivitySource);
                ActivityInstrumentationHelper.SetKindProperty(activity, ActivityKind.Server);
            }

            var path = (request.PathBase.HasValue || request.Path.HasValue) ? (request.PathBase + request.Path).ToString() : "/";
            RequestMethodHelper.SetActivityDisplayName(activity, request.Method);

            if (request.Host.HasValue)
            {
                activity.SetTag(SemanticConventions.AttributeServerAddress, request.Host.Host);

                if (request.Host.Port.HasValue)
                    activity.SetTag(SemanticConventions.AttributeServerPort, request.Host.Port.Value);
            }

            if (request.QueryString.HasValue)
            {
                if (this.options.DisableUrlQueryRedaction)
                    activity.SetTag(SemanticConventions.AttributeUrlQuery, request.QueryString.Value);
                else
                    activity.SetTag(SemanticConventions.AttributeUrlQuery, RedactionHelper.GetRedactedQueryString(request.QueryString.Value!));
            }

            TelemetryHelper.RequestDataHelper.SetHttpMethodTag(activity, request.Method);

            activity.SetTag(SemanticConventions.AttributeUrlScheme, request.Scheme);
            activity.SetTag(SemanticConventions.AttributeUrlPath, path);
            activity.SetTag(SemanticConventions.AttributeNetworkProtocolVersion, RequestDataHelper.GetHttpProtocolVersion(request.Protocol));

            if (request.Headers.TryGetValue("User-Agent", out var values))
            {
                var userAgent = values.Count > 0 ? values[0] : null;
                if (!string.IsNullOrEmpty(userAgent))
                {
                    activity.SetTag(SemanticConventions.AttributeUserAgentOriginal, userAgent);
                }
            }

            try
            {
                this.options.EnrichWithHttpRequest?.Invoke(activity, request);  // <-------------------ehr
            }
            catch (Exception ex)
            {
                AspNetCoreInstrumentationEventSource.Log.EnrichmentException(nameof(HttpInListener), nameof(this.OnStartActivity), activity.OperationName, ex);
            }
        }
    }

    public void OnStopActivity(Activity activity, object? payload)
    {
        if (activity.IsAllDataRequested)
        {
            if (payload is not HttpContext context)
            {
                AspNetCoreInstrumentationEventSource.Log.NullPayload(nameof(HttpInListener), nameof(this.OnStopActivity), activity.OperationName);
                return;
            }

            var response = context.Response;

            // <-----------------stf, if the request is for a static file, then the request won't be processed by routing middleware, so routePattern will be null
            var routePattern = (context.Features.Get<IExceptionHandlerPathFeature>()?.Endpoint as RouteEndpoint ?? context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (!string.IsNullOrEmpty(routePattern))
            {
                /*
                  Jaeger shows e.g. "GET publish-message f771806" instead of "Microsoft.AspNetCore.Hosting.HttpRequestIn" becuase of this SetActivityDisplayName call
                */
                RequestMethodHelper.SetActivityDisplayName(activity, context.Request.Method, routePattern);  // <-----------------------------------------sadn
                
                activity.SetTag(SemanticConventions.AttributeHttpRoute, routePattern);  // <---------set tag:  http.route: /api/WeatherForecast/{city}
                                                                                        
            }

            activity.SetTag(SemanticConventions.AttributeHttpResponseStatusCode, TelemetryHelper.GetBoxedStatusCode(response.StatusCode));

            if (this.options.EnableGrpcAspNetCoreSupport && TryGetGrpcMethod(activity, out var grpcMethod))
                AddGrpcAttributes(activity, grpcMethod, context);

            if (activity.Status == ActivityStatusCode.Unset)
                activity.SetStatus(SpanHelper.ResolveActivityStatusForHttpStatusCode(activity.Kind, response.StatusCode));

            try
            {
                this.options.EnrichWithHttpResponse?.Invoke(activity, response);
            }
            catch (Exception ex)
            {
                AspNetCoreInstrumentationEventSource.Log.EnrichmentException(nameof(HttpInListener), nameof(this.OnStopActivity), activity.OperationName, ex);
            }
        }

        object? tagValue;
        if (Net7OrGreater)
            tagValue = activity.GetTagValue("IsCreatedByInstrumentation");
        else
            _ = activity.TryCheckFirstTag("IsCreatedByInstrumentation", out tagValue);

        if (ReferenceEquals(tagValue, bool.TrueString))
        {
            activity.SetTag("IsCreatedByInstrumentation", null);
            activity.Stop();
        }
    }

    public void OnException(Activity activity, object? payload)
    {
        if (activity.IsAllDataRequested)
        {
            // we need to use reflection here as the payload type is not a defined public type.
            if (!TryFetchException(payload, out var exc))
            {
                AspNetCoreInstrumentationEventSource.Log.NullPayload(nameof(HttpInListener), nameof(this.OnException), activity.OperationName);
                return;
            }

            activity.SetTag(SemanticConventions.AttributeErrorType, exc.GetType().FullName);

            if (this.options.RecordException)  // <--------------------------oe
                activity.AddException(exc);

            activity.SetStatus(ActivityStatusCode.Error);

            try
            {
                this.options.EnrichWithException?.Invoke(activity, exc);
            }
            catch (Exception ex)
            {
                AspNetCoreInstrumentationEventSource.Log.EnrichmentException(nameof(HttpInListener), nameof(this.OnException), activity.OperationName, ex);
            }
        }
    }

    private static bool TryGetGrpcMethod(Activity activity, [NotNullWhen(true)] out string? grpcMethod)
    {
        grpcMethod = GrpcTagHelper.GetGrpcMethodFromActivity(activity);
        return !string.IsNullOrEmpty(grpcMethod);
    }

    private static void AddGrpcAttributes(Activity activity, string grpcMethod, HttpContext context)
    {
        activity.DisplayName = grpcMethod.TrimStart('/');

        activity.SetTag(SemanticConventions.AttributeRpcSystem, GrpcTagHelper.RpcSystemGrpc);

        if (context.Connection.RemoteIpAddress != null)
            activity.SetTag(SemanticConventions.AttributeClientAddress, context.Connection.RemoteIpAddress.ToString());

        activity.SetTag(SemanticConventions.AttributeClientPort, context.Connection.RemotePort);

        var validConversion = GrpcTagHelper.TryGetGrpcStatusCodeFromActivity(activity, out var status);
        if (validConversion)
            activity.SetStatus(GrpcTagHelper.ResolveSpanStatusForGrpcStatusCode(status));

        if (GrpcTagHelper.TryParseRpcServiceAndRpcMethod(grpcMethod, out var rpcService, out var rpcMethod))
        {
            activity.SetTag(SemanticConventions.AttributeRpcService, rpcService);
            activity.SetTag(SemanticConventions.AttributeRpcMethod, rpcMethod);

            // Remove the grpc.method tag added by the gRPC .NET library
            activity.SetTag(GrpcTagHelper.GrpcMethodTagName, null);

            // Remove the grpc.status_code tag added by the gRPC .NET library
            activity.SetTag(GrpcTagHelper.GrpcStatusCodeTagName, null);

            if (validConversion)
            {
                // setting rpc.grpc.status_code
                activity.SetTag(SemanticConventions.AttributeRpcGrpcStatusCode, status);
            }
        }
    }
}
//---------------------------Ʌ

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

    public OpenTelemetryBuilder WithMetrics(Action<MeterProviderBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.WithMetrics(this, configure);
        return this;
    }

    public OpenTelemetryBuilder WithTracing() => this.WithTracing(b => { });

    public OpenTelemetryBuilder WithTracing(Action<TracerProviderBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.WithTracing(this, configure);  // <------------------------ote2.0
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

    public static IOpenTelemetryBuilder WithTracing(this IOpenTelemetryBuilder builder) => WithTracing(builder, b => { });

    public static IOpenTelemetryBuilder WithTracing(this IOpenTelemetryBuilder builder, Action<TracerProviderBuilder> configure) // <----------ote2.0, pip
    {
        var tracerProviderBuilder = new TracerProviderBuilderBase(builder.Services);   // <-------------------ote2.1, pip

        configure(tracerProviderBuilder);  // <-------------------ote2.2.

        return builder;
    }

    public static IOpenTelemetryBuilder WithLogging(this IOpenTelemetryBuilder builder)  => WithLogging(builder, configureBuilder: null, configureOptions: null);

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

//----------------------------------------------------------V
internal sealed class TracerProviderServiceCollectionBuilder : TracerProviderBuilder, ITracerProviderBuilder
{
    public TracerProviderServiceCollectionBuilder(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider((sp, builder) => this.Services = null);

        this.Services = services;
    }

    public IServiceCollection? Services { get; set; }

    public TracerProvider? Provider => null;

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)  // <----------------ote4.2
    {
        this.ConfigureBuilderInternal((sp, builder) =>
        {
            builder.AddInstrumentation(instrumentationFactory);
        });

        return this;
    }

    public override TracerProviderBuilder AddSource(params string[] names)
    {
        this.ConfigureBuilderInternal((sp, builder) =>
        {
            builder.AddSource(names);
        });

        return this;
    }

    public override TracerProviderBuilder AddLegacySource(string operationName)
    {
        this.ConfigureBuilderInternal((sp, builder) =>
        {
            builder.AddLegacySource(operationName);
        });

        return this;
    }

    public TracerProviderBuilder ConfigureServices(Action<IServiceCollection> configure)
        => this.ConfigureServicesInternal(configure);

    public TracerProviderBuilder ConfigureBuilder(Action<IServiceProvider, TracerProviderBuilder> configure)
        => this.ConfigureBuilderInternal(configure);

    TracerProviderBuilder IDeferredTracerProviderBuilder.Configure(Action<IServiceProvider, TracerProviderBuilder> configure)
        => this.ConfigureBuilderInternal(configure);

    private TracerProviderServiceCollectionBuilder ConfigureBuilderInternal(Action<IServiceProvider, TracerProviderBuilder> configure)  // <--------------------ote4.3
    {
        var services = this.Services ?? throw new NotSupportedException("Builder cannot be configured during TracerProvider construction.");

        services.ConfigureOpenTelemetryTracerProvider(configure);  // <----------------------------------ote4.4, configure is
                                                                   //  (sp, builder) => builder.AddInstrumentation(sp => new AspNetCoreInstrumentation(new HttpInListener(options)))
        return this;
    }

    private TracerProviderServiceCollectionBuilder ConfigureServicesInternal(Action<IServiceCollection> configure)
    {
        var services = this.Services
            ?? throw new NotSupportedException("Services cannot be configured during TracerProvider construction.");

        configure(services);

        return this;
    }
}
//----------------------------------------------------------Ʌ

//------------------------------------V
public class TracerProviderBuilderBase : TracerProviderBuilder, ITracerProviderBuilder
{
    private readonly bool allowBuild;
    private readonly TracerProviderServiceCollectionBuilder innerBuilder;

    /*
    public TracerProviderBuilderBase()  // <--------this constructor is only used by console app where it need to create a new ServiceCollection
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
    */

    internal TracerProviderBuilderBase(IServiceCollection services)
    {
        services
            .AddOpenTelemetryTracerProviderBuilderServices()
            .TryAddSingleton<TracerProvider>(sp => new TracerProviderSdk(sp, ownsServiceProvider: false));  // <-----------------pip

        this.innerBuilder = new TracerProviderServiceCollectionBuilder(services);

        this.allowBuild = false;
    }

    TracerProvider? ITracerProviderBuilder.Provider => null;

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)  // <---------------------------ote4.0
    {
        this.innerBuilder.AddInstrumentation(instrumentationFactory);  // <-------ote4.1, instrumentationFactory is sp => new AspNetCoreInstrumentation(new HttpInListener(options))

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

    internal TracerProvider InvokeBuild() => this.Build();  // <--------------pip

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

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)  // <--------------------------ote4.8
    {
        return this.AddInstrumentation(
            typeof(TInstrumentation).Name,
            typeof(TInstrumentation).Assembly.GetName().Version?.ToString() ?? DefaultInstrumentationVersion,
            instrumentationFactory!());
    }

    public TracerProviderBuilder AddInstrumentation(string instrumentationName, string instrumentationVersion, object? instrumentation)
    {
        this.Instrumentation.Add(new InstrumentationRegistration(instrumentationName, instrumentationVersion, instrumentation)); // <--------------------------ote4.9.

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

    public override TracerProviderBuilder AddSource(params string[] names)  // <---------------------
    {
        foreach (var name in names!)
        {
            this.Sources.Add(name);
        }

        return this;
    }

    public TracerProviderBuilder AddProcessor(BaseProcessor<Activity> processor)  // <---------------------------------coe1.1
    {
        this.Processors.Add(processor!);   // <---------------------------------coe1.2  processor is new SimpleActivityExportProcessor(new ConsoleActivityExporter(options))

        return this;
    }

    public TracerProviderBuilder SetSampler(Sampler sampler)
    {
        this.Sampler = sampler;  // <-------------------------sam0.2.

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
    private readonly ActivityListener listener; // <-------------------
    private readonly Sampler sampler; // <-----------------------------
    private readonly Action<Activity> getRequestedDataAction;
    private readonly bool supportLegacyActivity;
    private BaseProcessor<Activity>? processor;

    internal TracerProviderSdk(IServiceProvider serviceProvider, bool ownsServiceProvider)
    {
        TracerProviderBuilderSdk state = serviceProvider!.GetRequiredService<TracerProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
        {
            this.OwnedServiceProvider = serviceProvider as IDisposable;
        }

        OpenTelemetrySdkEventSource.Log.TracerProviderSdkEvent("Building TracerProvider.");

        var configureProviderBuilders = serviceProvider!.GetServices<IConfigureTracerProviderBuilder>();   // <-----------------------------ote4.6
        foreach (var configureProviderBuilder in configureProviderBuilders)
        {
            configureProviderBuilder.ConfigureBuilder(serviceProvider!, state);  // <--------------------------ote4.7
        }

        StringBuilder processorsAdded = new StringBuilder();
        StringBuilder instrumentationFactoriesAdded = new StringBuilder();

        var resourceBuilder = state.ResourceBuilder ?? ResourceBuilder.CreateDefault();
        resourceBuilder.ServiceProvider = serviceProvider;
        this.Resource = resourceBuilder.Build();  // <-------------------------------rb3

        this.sampler = GetSampler(serviceProvider!.GetRequiredService<IConfiguration>(), state.Sampler);  // <----------------sam0
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

        var activityListener = new ActivityListener();  // <-----------------------!!aact, tpsact

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
                            this.getRequestedDataAction!(activity);  // <---------------------
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
                    this.processor?.OnStart(activity);   // <-----------------------------pro, note that OnStart does nothing
            };

            activityListener.ActivityStopped = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStopped(activity);

                if (string.IsNullOrEmpty(activity.Source.Name) && !legacyActivityPredicate(activity))
                    return;

                if (!activity.IsAllDataRequested)
                    return;

                if (SuppressInstrumentationScope.DecrementIfTriggered() == 0)
                    this.processor?.OnEnd(activity);   // <------------pro, OnEnd calls OnExport, that's why Console print childActivity before parentActiviy, see actp
            };
        }
        else
        {
            activityListener.ActivityStarted = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStarted(activity);

                if (activity.IsAllDataRequested && ...)  // <---------------------! the activity won't be send to processor if IsAllDataRequested is false
                {
                    this.processor?.OnStart(activity);
                }
            };

            activityListener.ActivityStopped = activity =>
            {
                OpenTelemetrySdkEventSource.Log.ActivityStopped(activity);

                if (!activity.IsAllDataRequested)  //<--------------------------sam
                {
                    return;
                }

                if (SuppressInstrumentationScope.DecrementIfTriggered() == 0)
                {
                    this.processor?.OnEnd(activity);  // <--------------------------! send activity to processor
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
            activityListener.Sample = (ref ActivityCreationOptions<ActivityContext> options) =>      // <---------------------sam, sample is called at ActivitySource.StartActivity
                !Sdk.SuppressInstrumentation ? ComputeActivitySamplingResult(ref options, this.sampler) : ActivitySamplingResult.None;

            this.getRequestedDataAction = this.RunGetRequestedDataOtherSampler;  // this.getRequestedDataAction is for legcy activities not created by ActivitySource, 
                                                                                 // the sampling decision cannot be made at creation time.
                                                                                 //  Instead, it must be applied after the activity is started.
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
                /* 
                   each call such as AddAspNetCoreInstrumentation() and AddHttpClientInstrumentation() add a source on TracerProviderBuilderSdk.Sources
                */
                var activitySources = new HashSet<string>(state.Sources, StringComparer.OrdinalIgnoreCase);  // <--------------------------acts

                if (this.supportLegacyActivity)
                {
                    activitySources.Add(string.Empty);
                }

                // function which takes ActivitySource and returns true/false to indicate if it should be subscribed to or not.
                activityListener.ShouldListenTo = activitySource => activitySources.Contains(activitySource.Name);  // <--------------------------acts
            }
        }
        else
        {
            if (this.supportLegacyActivity)
            {
                activityListener.ShouldListenTo = activitySource => string.IsNullOrEmpty(activitySource.Name);
            }
        }

        ActivitySource.AddActivityListener(activityListener);   // <--------------------------acts
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
            this.processor = newCompositeProcessor;  // <----------------------------pro
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

    private static ActivitySamplingResult ComputeActivitySamplingResult(ref ActivityCreationOptions<ActivityContext> options, Sampler sampler)  // <------------------! casr
    {
        var samplingParameters = new SamplingParameters(options.Parent, options.TraceId, options.Name, options.Kind, options.Tags, options.Links); 
        /*
          you might ask why SamplingParameters needs to take both ActivityContext(options.Parent) and ActivityTraceId (options.TraceId) as parameter 
          as you can get traceId from ActivityContext. But for root span, options.Parent is default ActivityContext with traceId not being set at all,
          and if you look at the ActivityCreationOptions struct, you will see the TraceId property check if the Parent is default ActivityContext, if it is
          ActivityCreationOptions.TraceId will create a brand new traceId, that's why ParentBasedSampler checks on options.Parent (because it wants to know 
          if it is rootspan case) while TraceIdRatioBasedSampler checks on options.TraceId
        */

        SamplingResult samplingResult = sampler.ShouldSample(samplingParameters);  // <--------------sam

        var activitySamplingResult = samplingResult.Decision switch
        {
            SamplingDecision.RecordAndSample => ActivitySamplingResult.AllDataAndRecorded,
            SamplingDecision.RecordOnly => ActivitySamplingResult.AllData,
            _ => PropagateOrIgnoreData(ref options),  // return ActivitySamplingResult.PropagationData for rootSpan or ActivitySamplingResult.None (when SamplingDecision.Drop)
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

        switch (samplingResult.Decision)  // <------------------samd
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

//--------------------------V
public enum SamplingDecision
{
    Drop,  // The activity will be created but not recorded. Activity.IsAllDataRequested will return false.

    RecordOnly,  // The activity will be created and recorded, but sampling flag will not be set.
                 // Activity.IsAllDataRequested will return true. Activity.Recorded will return false.
    
    RecordAndSample,    // The activity will be created, recorded, and sampling flag will be set.
                        // Activity.IsAllDataRequested will return true. Activity.Recorded will return true.            
}
//--------------------------Ʌ

//----------------------------V
public readonly struct Baggage : IEquatable<Baggage>
{
    private static readonly RuntimeContextSlot<BaggageHolder> RuntimeContextSlot = RuntimeContext.RegisterSlot<BaggageHolder>("otel.baggage");  // <------use AsyncLocal
    private static readonly Dictionary<string, string> EmptyBaggage = [];

    private readonly Dictionary<string, string> baggage;

    internal Baggage(Dictionary<string, string> baggage)
    {
        this.baggage = baggage;
    }

    public static Baggage Current
    {
        get => RuntimeContextSlot.Get()?.Baggage ?? default;
        set => EnsureBaggageHolder().Baggage = value;
    }

    public static bool operator ==(Baggage left, Baggage right) => left.Equals(right);

    public static Baggage Create(Dictionary<string, string>? baggageItems = null)
    {
        if (baggageItems == null)
        {
            return default;
        }

        Dictionary<string, string> baggageCopy = new Dictionary<string, string>(baggageItems.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> baggageItem in baggageItems)
        {
            if (string.IsNullOrEmpty(baggageItem.Value))
            {
                baggageCopy.Remove(baggageItem.Key);
                continue;
            }

            baggageCopy[baggageItem.Key] = baggageItem.Value;
        }

        return new Baggage(baggageCopy);
    }

    public static IReadOnlyDictionary<string, string> GetBaggage(Baggage baggage = default) => baggage == default ? Current.GetBaggage() : baggage.GetBaggage();

    public static Dictionary<string, string>.Enumerator GetEnumerator(Baggage baggage = default) => baggage == default ? Current.GetEnumerator() : baggage.GetEnumerator();

    public static string? GetBaggage(string name, Baggage baggage = default) => baggage == default ? Current.GetBaggage(name) : baggage.GetBaggage(name);

    public static Baggage SetBaggage(string name, string? value, Baggage baggage = default)
    {
        var baggageHolder = EnsureBaggageHolder();
        lock (baggageHolder)
        {
            return baggageHolder.Baggage = baggage == default
                ? baggageHolder.Baggage.SetBaggage(name, value)
                : baggage.SetBaggage(name, value);
        }
    }

    public static Baggage SetBaggage(IEnumerable<KeyValuePair<string, string?>> baggageItems, Baggage baggage = default)
    {
        var baggageHolder = EnsureBaggageHolder();
        lock (baggageHolder)
        {
            return baggageHolder.Baggage = baggage == default
                ? baggageHolder.Baggage.SetBaggage(baggageItems)
                : baggage.SetBaggage(baggageItems);
        }
    }

    public static Baggage RemoveBaggage(string name, Baggage baggage = default)
    {
        var baggageHolder = EnsureBaggageHolder();
        lock (baggageHolder)
        {
            return baggageHolder.Baggage = baggage == default ? baggageHolder.Baggage.RemoveBaggage(name) : baggage.RemoveBaggage(name);
        }
    }

    public static Baggage ClearBaggage(Baggage baggage = default)
    {
        var baggageHolder = EnsureBaggageHolder();
        lock (baggageHolder)
        {
            return baggageHolder.Baggage = baggage == default ? baggageHolder.Baggage.ClearBaggage() : baggage.ClearBaggage();
        }
    }

    public IReadOnlyDictionary<string, string> GetBaggage() => this.baggage ?? EmptyBaggage;

    public string? GetBaggage(string name)
    {
        return this.baggage != null && this.baggage.TryGetValue(name, out string? value) ? value : null;
    }

    public Baggage SetBaggage(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return this.RemoveBaggage(name);

        return new Baggage(
            new Dictionary<string, string>(this.baggage ?? EmptyBaggage, StringComparer.OrdinalIgnoreCase)
            {
                [name] = value!,
            });
    }

    public Baggage SetBaggage(params KeyValuePair<string, string?>[] baggageItems) => this.SetBaggage((IEnumerable<KeyValuePair<string, string?>>)baggageItems);

    public Baggage SetBaggage(IEnumerable<KeyValuePair<string, string?>> baggageItems)
    {
        if (baggageItems?.Any() != true)
            return this;

        var newBaggage = new Dictionary<string, string>(this.baggage ?? EmptyBaggage, StringComparer.OrdinalIgnoreCase);

        foreach (var item in baggageItems)
        {
            if (string.IsNullOrEmpty(item.Value))
                newBaggage.Remove(item.Key);
            else
                newBaggage[item.Key] = item.Value!;
        }

        return new Baggage(newBaggage);
    }

    public Baggage RemoveBaggage(string name)
    {
        var baggage = new Dictionary<string, string>(this.baggage ?? EmptyBaggage, StringComparer.OrdinalIgnoreCase);
        baggage.Remove(name);

        return new Baggage(baggage);
    }

    public Dictionary<string, string>.Enumerator GetEnumerator() => (this.baggage ?? EmptyBaggage).GetEnumerator();

    public bool Equals(Baggage other) { ... }

    public override bool Equals(object? obj) => (obj is Baggage baggage) && this.Equals(baggage);

    public override int GetHashCode() { ... }

    private static BaggageHolder EnsureBaggageHolder()
    {
        var baggageHolder = RuntimeContextSlot.Get();
        if (baggageHolder == null)
        {
            baggageHolder = new BaggageHolder();
            RuntimeContextSlot.Set(baggageHolder);
        }

        return baggageHolder;
    }

    private sealed class BaggageHolder
    {
        public Baggage Baggage;
    }
}
//----------------------------Ʌ

//--------------------------V
public class ResourceBuilder
{
    internal readonly List<IResourceDetector> ResourceDetectors = [];
    private static readonly Resource DefaultResource = PrepareDefaultResource();

    private ResourceBuilder() { }

    internal IServiceProvider? ServiceProvider { get; set; }

    public static ResourceBuilder CreateDefault()  // <-------------------rb0
        => new ResourceBuilder()
            .AddResource(DefaultResource)          
            .AddTelemetrySdk()                     // <-------------------rb1
            .AddEnvironmentVariableDetector();     // <-------------------rb2

    public static ResourceBuilder CreateEmpty() => new();

    public ResourceBuilder Clear()
    {
        this.ResourceDetectors.Clear();

        return this;
    }

    public Resource Build()
    {
        Resource finalResource = Resource.Empty;

        foreach (IResourceDetector resourceDetector in this.ResourceDetectors)
        {
            if (resourceDetector is ResolvingResourceDetector resolvingResourceDetector)
            {
                resolvingResourceDetector.Resolve(this.ServiceProvider);
            }

            var resource = resourceDetector.Detect();
            if (resource != null)
            {
                finalResource = finalResource.Merge(resource);
            }
        }

        return finalResource;
    }

    public ResourceBuilder AddDetector(IResourceDetector resourceDetector)
    {
        this.ResourceDetectors.Add(resourceDetector);

        return this;
    }

    public ResourceBuilder AddDetector(Func<IServiceProvider, IResourceDetector> resourceDetectorFactory)
    {
        return this.AddDetectorInternal(sp =>
        {
            if (sp == null)
                throw new NotSupportedException("IResourceDetector factory pattern is not supported when calling ResourceBuilder.Build() directly.");

            return resourceDetectorFactory(sp);
        });
    }

    internal ResourceBuilder AddDetectorInternal(Func<IServiceProvider?, IResourceDetector> resourceDetectorFactory)
    {
        this.ResourceDetectors.Add(new ResolvingResourceDetector(resourceDetectorFactory));

        return this;
    }

    internal ResourceBuilder AddResource(Resource resource)
    {
        this.ResourceDetectors.Add(new WrapperResourceDetector(resource));

        return this;
    }

    private static Resource PrepareDefaultResource()
    {
        var defaultServiceName = "unknown_service";

        try
        {
            var processName = Process.GetCurrentProcess().ProcessName;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                defaultServiceName = $"{defaultServiceName}:{processName}";
            }
        }
        catch
        {
            // GetCurrentProcess can throw PlatformNotSupportedException
        }

        return new Resource(new Dictionary<string, object>
        {
            [ResourceSemanticConventions.AttributeServiceName] = defaultServiceName,
        });
    }

    internal sealed class WrapperResourceDetector : IResourceDetector
    {
        private readonly Resource resource;

        public WrapperResourceDetector(Resource resource)
        {
            this.resource = resource;
        }

        public Resource Detect() => this.resource;
    }

    private sealed class ResolvingResourceDetector : IResourceDetector
    {
        private readonly Func<IServiceProvider?, IResourceDetector> resourceDetectorFactory;
        private IResourceDetector? resourceDetector;

        public ResolvingResourceDetector(Func<IServiceProvider?, IResourceDetector> resourceDetectorFactory)
        {
            this.resourceDetectorFactory = resourceDetectorFactory;
        }

        public void Resolve(IServiceProvider? serviceProvider)
        {
            this.resourceDetector = this.resourceDetectorFactory(serviceProvider)
                ?? throw new InvalidOperationException("ResourceDetector factory did not return a ResourceDetector instance.");
        }

        public Resource Detect()
        {
            var detector = this.resourceDetector;

            return detector?.Detect() ?? Resource.Empty;
        }
    }
}
//--------------------------Ʌ

//-------------------V
public class Resource
{
    public Resource(IEnumerable<KeyValuePair<string, object>> attributes)
    {
        if (attributes == null)
        {
            OpenTelemetrySdkEventSource.Log.InvalidArgument("Create resource", "attributes", "are null");
            this.Attributes = [];
            return;
        }

        // resource creation is expected to be done a few times during app startup i.e. not on the hot path, we can copy attributes.
        this.Attributes = attributes.Select(SanitizeAttribute).ToList();
    }
    public static Resource Empty { get; } = new([]);
    public IEnumerable<KeyValuePair<string, object>> Attributes { get; }

    public Resource Merge(Resource other)
    {
        var newAttributes = new Dictionary<string, object>();

        if (other != null)
        {
            foreach (var attribute in other.Attributes)
            {
                if (!newAttributes.TryGetValue(attribute.Key, out _))
                    newAttributes[attribute.Key] = attribute.Value;
            }
        }

        foreach (var attribute in this.Attributes)
        {
            if (!newAttributes.TryGetValue(attribute.Key, out _))
                newAttributes[attribute.Key] = attribute.Value;
        }

        return new Resource(newAttributes);
    }

    private static KeyValuePair<string, object> SanitizeAttribute(KeyValuePair<string, object> attribute)
    {
        string sanitizedKey;
        if (attribute.Key == null)
        {
            OpenTelemetrySdkEventSource.Log.InvalidArgument("Create resource", "attribute key", "Attribute key should be non-null string.");
            sanitizedKey = string.Empty;
        }
        else
            sanitizedKey = attribute.Key;

        var sanitizedValue = SanitizeValue(attribute.Value, sanitizedKey);
        return new KeyValuePair<string, object>(sanitizedKey, sanitizedValue);
    }

    private static object SanitizeValue(object value, string keyName)
    {
        return value switch
        {
            string => value,
            bool => value,
            double => value,
            int => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            int[] v => Array.ConvertAll(v, Convert.ToInt64),
            // ...
            _ => throw new ArgumentException("Attribute value type is not an accepted primitive", keyName),
        };
    }
}
//-------------------Ʌ

//-------------------------------------------V
public class AspNetCoreInstrumentationOptions
{
    public Func<HttpContext, bool> Filter { get; set; }   // <----------------------hil

    public Action<Activity, HttpRequest> EnrichWithHttpRequest { get; set; }

    public Action<Activity, HttpResponse> EnrichWithHttpResponse { get; set; }

    public Action<Activity, Exception> EnrichWithException { get; set; }

    public bool RecordException { get; set; }

    public bool EnableGrpcAspNetCoreSupport { get; set; } = true;
}
//-------------------------------------------Ʌ

//-------------------------------------------V
public static class ResourceBuilderExtensions
{
    private static readonly string InstanceId = Guid.NewGuid().ToString();

    private static Resource TelemetryResource { get; } = new Resource(new Dictionary<string, object>  // <---------------------rb1
    {
        [ResourceSemanticConventions.AttributeTelemetrySdkName] = "opentelemetry",
        [ResourceSemanticConventions.AttributeTelemetrySdkLanguage] = "dotnet",
        [ResourceSemanticConventions.AttributeTelemetrySdkVersion] = Sdk.InformationalVersion,
    });

    public static ResourceBuilder AddService(
        this ResourceBuilder resourceBuilder,
        string serviceName,
        string? serviceNamespace = null,
        string? serviceVersion = null,
        bool autoGenerateServiceInstanceId = true,
        string? serviceInstanceId = null)
    {
        Dictionary<string, object> resourceAttributes = new Dictionary<string, object>();


        resourceAttributes.Add(ResourceSemanticConventions.AttributeServiceName, serviceName);

        if (!string.IsNullOrEmpty(serviceNamespace))
            resourceAttributes.Add(ResourceSemanticConventions.AttributeServiceNamespace, serviceNamespace!);

        if (!string.IsNullOrEmpty(serviceVersion))
            resourceAttributes.Add(ResourceSemanticConventions.AttributeServiceVersion, serviceVersion!);

        if (serviceInstanceId == null && autoGenerateServiceInstanceId)
            serviceInstanceId = InstanceId;

        if (serviceInstanceId != null)
            resourceAttributes.Add(ResourceSemanticConventions.AttributeServiceInstance, serviceInstanceId);

        return resourceBuilder.AddResource(new Resource(resourceAttributes));
    }

    public static ResourceBuilder AddTelemetrySdk(this ResourceBuilder resourceBuilder)   // <---------------------rb1
    {
        return resourceBuilder.AddResource(TelemetryResource);
    }

    public static ResourceBuilder AddAttributes(this ResourceBuilder resourceBuilder, IEnumerable<KeyValuePair<string, object>> attributes)  // <---------------
    {
        return resourceBuilder.AddResource(new Resource(attributes)); 
    }

    public static ResourceBuilder AddEnvironmentVariableDetector(this ResourceBuilder resourceBuilder)  // <---------------------rb2.0
    {
        Lazy<IConfiguration> configuration = new Lazy<IConfiguration>(() => new ConfigurationBuilder().AddEnvironmentVariables().Build());

        return resourceBuilder
            .AddDetectorInternal(sp => new OtelEnvResourceDetector(sp?.GetService<IConfiguration>() ?? configuration.Value))
            .AddDetectorInternal(sp => new OtelServiceNameEnvVarDetector(sp?.GetService<IConfiguration>() ?? configuration.Value)); // <---------------------rb2.1
    }
}
//-------------------------------------------Ʌ

//-------------------------------------------------V
internal sealed class OtelServiceNameEnvVarDetector : IResourceDetector
{
    public const string EnvVarKey = "OTEL_SERVICE_NAME";

    private readonly IConfiguration configuration;

    public OtelServiceNameEnvVarDetector(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public Resource Detect()
    {
        var resource = Resource.Empty;

        if (this.configuration.TryGetStringValue(EnvVarKey, out string? envResourceAttributeValue))
        {
            resource = new Resource(new Dictionary<string, object>
            {
                [ResourceSemanticConventions.AttributeServiceName] = envResourceAttributeValue,
               // <--------------------------"service.name"
            });
        }

        return resource;
    }
}
//-------------------------------------------------Ʌ

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

//--------------------------------V
public class CompositeProcessor<T> : BaseProcessor<T>
{
    internal readonly DoublyLinkedListNode Head;
    private DoublyLinkedListNode tail;
    private bool disposed;

    public CompositeProcessor(IEnumerable<BaseProcessor<T>> processors)
    {
        using var iter = processors.GetEnumerator();
        if (!iter.MoveNext())
        {
            throw new ArgumentException($"'{iter}' is null or empty", nameof(processors));
        }

        this.Head = new DoublyLinkedListNode(iter.Current);
        this.tail = this.Head;

        while (iter.MoveNext())
        {
            this.AddProcessor(iter.Current);
        }
    }

    public CompositeProcessor<T> AddProcessor(BaseProcessor<T> processor)
    {
        Guard.ThrowIfNull(processor);

        var node = new DoublyLinkedListNode(processor)
        {
            Previous = this.tail,
        };
        this.tail.Next = node;
        this.tail = node;

        return this;
    }

    public override void OnEnd(T data)
    {
        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            cur.Value.OnEnd(data);
        }
    }

    public override void OnStart(T data)
    {
        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            cur.Value.OnStart(data);   // <---------------------------pro
        }
    }

    internal override void SetParentProvider(BaseProvider parentProvider)
    {
        base.SetParentProvider(parentProvider);

        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            cur.Value.SetParentProvider(parentProvider);
        }
    }

    internal IReadOnlyList<BaseProcessor<T>> ToReadOnlyList()
    {
        var list = new List<BaseProcessor<T>>();

        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            list.Add(cur.Value);
        }

        return list;
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        var result = true;
        var sw = timeoutMilliseconds == Timeout.Infinite
            ? null
            : Stopwatch.StartNew();

        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            if (sw == null)
            {
                result = cur.Value.ForceFlush() && result;
            }
            else
            {
                var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

                // notify all the processors, even if we run overtime
                result = cur.Value.ForceFlush((int)Math.Max(timeout, 0)) && result;
            }
        }

        return result;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = true;
        var sw = timeoutMilliseconds == Timeout.Infinite
            ? null
            : Stopwatch.StartNew();

        for (var cur = this.Head; cur != null; cur = cur.Next)
        {
            if (sw == null)
            {
                result = cur.Value.Shutdown() && result;
            }
            else
            {
                var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

                // notify all the processors, even if we run overtime
                result = cur.Value.Shutdown((int)Math.Max(timeout, 0)) && result;
            }
        }

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                for (var cur = this.Head; cur != null; cur = cur.Next)
                {
                    try
                    {
                        cur.Value.Dispose();
                    }
                    catch (Exception ex)
                    {
                        OpenTelemetrySdkEventSource.Log.SpanProcessorException(nameof(this.Dispose), ex);
                    }
                }
            }

            this.disposed = true;
        }

        base.Dispose(disposing);
    }

    internal sealed class DoublyLinkedListNode
    {
        public readonly BaseProcessor<T> Value;

        public DoublyLinkedListNode(BaseProcessor<T> value)
        {
            this.Value = value;
        }

        public DoublyLinkedListNode? Previous { get; set; }

        public DoublyLinkedListNode? Next { get; set; }
    }
}
//--------------------------------Ʌ

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

    public sealed override void OnStart(T data) { }  // <-----------------do nothing

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
    public BatchActivityExportProcessor(BaseExporter<Activity> exporter, int maxQueueSize = DefaultMaxQueueSize, int scheduledDelayMilliseconds = DefaultScheduledDelayMilliseconds,
                                        int exporterTimeoutMilliseconds = DefaultExporterTimeoutMilliseconds, int maxExportBatchSize = DefaultMaxExportBatchSize)
    : base(exporter, maxQueueSize, scheduledDelayMilliseconds, exporterTimeoutMilliseconds, maxExportBatchSize) { }

    public override void OnEnd(Activity data)
    {
        if (!data.Recorded)  // <----------sam, only export ActivitySamplingResult.AllDataAndRecorded, not ActivitySamplingResult.AllData
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
            case "System.Net.Http.Exception":  // <--------------------oe
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

        RequestMethodHelper.SetActivityDisplayName(activity, request2.Method.Method);
        if (!IsNet7OrGreater)
        {
            ActivityInstrumentationHelper.SetActivitySourceProperty(activity, ActivitySource);
            ActivityInstrumentationHelper.SetKindProperty(activity, ActivityKind.Client);
        }

        if (!IsNet9OrGreater)
        {
            RequestMethodHelper.SetHttpMethodTag(activity, request2.Method.Method);
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

        if (options.RecordException)  // <--------------------------------
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
    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder) => AddConsoleExporter(builder, name: null, configure: null);  // <--------coe0

    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder, Action<ConsoleExporterOptions> configure)
        => AddConsoleExporter(builder, name: null, configure);

    public static TracerProviderBuilder AddConsoleExporter(this TracerProviderBuilder builder, string? name, Action<ConsoleExporterOptions>? configure)
    {
        name ??= Options.DefaultName;

        if (configure != null)
            builder.ConfigureServices(services => services.Configure(name, configure));

        return builder.AddProcessor(sp =>   // <-----------------------------------------------------coe0.1.
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

            var resource = this.ParentProvider.GetResource();  // <-----------rb4, Activity doesn't contain any "resource related" tags, only exporter takes in Resource 
                                                               // and show them in Console or APM backend
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
```yml
# actp, childActivity is printed before parentActivity because Console only logs Activity when it is stopped
...
Activity.TraceId:            37285f6aa3b5b2cd9bee843c581e9368
Activity.SpanId:             fd793a06db01a828
Activity.TraceFlags:         Recorded
Activity.ParentSpanId:       d0017112214dc65f  # <---------------childActivity
Activity.DisplayName:        AnotherOne
Activity.Kind:               Internal
Activity.StartTime:          2025-05-16T12:59:10.4698506Z
Activity.Duration:           00:00:01.2607058
Instrumentation scope (ActivitySource):
    Name: Tracing.NET
Resource associated with Activity:
    telemetry.sdk.name: opentelemetry
    telemetry.sdk.language: dotnet
    telemetry.sdk.version: 1.12.0
    service.name: unknown_service:WeatherForecastSimpleTracing  # <-----------------------check WeatherForecastSimpleTracing VS solution for complete code
    

Activity.TraceId:            37285f6aa3b5b2cd9bee843c581e9368
Activity.SpanId:             d0017112214dc65f   # <---------------parentActivity
Activity.TraceFlags:         Recorded
Activity.DisplayName:        GET WeatherForecast/OutgoingHttp
Activity.Kind:               Server
Activity.StartTime:          2025-05-16T12:59:10.4533497Z
Activity.Duration:           00:00:01.2822832
Activity.Tags:
    server.address: localhost
    server.port: 5164
    http.request.method: GET
    url.scheme: http
    url.path: /weatherforecast/OutgoingHttp
    network.protocol.version: 1.1
    user_agent.original: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36
    http.route: WeatherForecast/OutgoingHttp
    http.response.status_code: 200
Instrumentation scope (ActivitySource):
    Name: Microsoft.AspNetCore
Resource associated with Activity:
    telemetry.sdk.name: opentelemetry
    telemetry.sdk.language: dotnet
    telemetry.sdk.version: 1.12.0
    service.name: unknown_service:WeatherForecastSimpleTracing
```

```C#
//---------------------------------------------------V
public static class OtlpTraceExporterHelperExtensions
{
    public static TracerProviderBuilder AddOtlpExporter(this TracerProviderBuilder builder)
        => AddOtlpExporter(builder, name: null, configure: null);

    public static TracerProviderBuilder AddOtlpExporter(this TracerProviderBuilder builder, Action<OtlpExporterOptions> configure)
        => AddOtlpExporter(builder, name: null, configure);

    public static TracerProviderBuilder AddOtlpExporter(this TracerProviderBuilder builder, string? name,Action<OtlpExporterOptions>? configure)
    {
        var finalOptionsName = name ?? Options.DefaultName;

        builder.ConfigureServices(services =>
        {
            if (name != null && configure != null)
            {
                // If we are using named options we register the
                // configuration delegate into options pipeline.
                services.Configure(finalOptionsName, configure);
            }

            services.AddOtlpExporterTracingServices();
        });

        return builder.AddProcessor(sp =>
        {
            OtlpExporterOptions exporterOptions;

            if (name == null)
            {
                exporterOptions = sp.GetRequiredService<IOptionsFactory<OtlpExporterOptions>>().Create(finalOptionsName);

                // Configuration delegate is executed inline on the fresh instance.
                configure?.Invoke(exporterOptions);
            }
            else
            {
                // When using named options we can properly utilize Options
                // API to create or reuse an instance.
                exporterOptions = sp.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(finalOptionsName);
            }

            // Note: Not using finalOptionsName here for SdkLimitOptions.
            // There should only be one provider for a given service
            // collection so SdkLimitOptions is treated as a single default
            // instance.
            var sdkLimitOptions = sp.GetRequiredService<IOptionsMonitor<SdkLimitOptions>>().CurrentValue;

            return BuildOtlpExporterProcessor(
                sp,
                exporterOptions,
                sdkLimitOptions,
                sp.GetRequiredService<IOptionsMonitor<ExperimentalOptions>>().Get(finalOptionsName));
        });
    }

    internal static BaseProcessor<Activity> BuildOtlpExporterProcessor(
        IServiceProvider serviceProvider,
        OtlpExporterOptions exporterOptions,
        SdkLimitOptions sdkLimitOptions,
        ExperimentalOptions experimentalOptions,
        Func<BaseExporter<Activity>, BaseExporter<Activity>>? configureExporterInstance = null)
        => BuildOtlpExporterProcessor(
            serviceProvider,
            exporterOptions,
            sdkLimitOptions,
            experimentalOptions,
            exporterOptions.ExportProcessorType,
            exporterOptions.BatchExportProcessorOptions ?? new BatchExportActivityProcessorOptions(),
            skipUseOtlpExporterRegistrationCheck: false,
            configureExporterInstance: configureExporterInstance);

    internal static BaseProcessor<Activity> BuildOtlpExporterProcessor(
        IServiceProvider serviceProvider,
        OtlpExporterOptions exporterOptions,
        SdkLimitOptions sdkLimitOptions,
        ExperimentalOptions experimentalOptions,
        ExportProcessorType exportProcessorType,
        BatchExportProcessorOptions<Activity> batchExportProcessorOptions,
        bool skipUseOtlpExporterRegistrationCheck = false,
        Func<BaseExporter<Activity>, BaseExporter<Activity>>? configureExporterInstance = null)
    {
        if (!skipUseOtlpExporterRegistrationCheck)
            serviceProvider!.EnsureNoUseOtlpExporterRegistrations();

        BaseExporter<Activity> otlpExporter = new OtlpTraceExporter(exporterOptions!, sdkLimitOptions!, experimentalOptions!);  // <-------------------------

        if (configureExporterInstance != null)
            otlpExporter = configureExporterInstance(otlpExporter);

        if (exportProcessorType == ExportProcessorType.Simple)
        {
            return new SimpleActivityExportProcessor(otlpExporter);
        }
        else
        {
            return new BatchActivityExportProcessor(
                otlpExporter,
                batchExportProcessorOptions!.MaxQueueSize,
                batchExportProcessorOptions.ScheduledDelayMilliseconds,
                batchExportProcessorOptions.ExporterTimeoutMilliseconds,
                batchExportProcessorOptions.MaxExportBatchSize);
        }
    }
}
//---------------------------------------------------Ʌ

//-----------------------------------------------------V
public class OtlpExporterOptions : IOtlpExporterOptions
{
    internal const string DefaultGrpcEndpoint = "http://localhost:4317";
    internal const string DefaultHttpEndpoint = "http://localhost:4318";
    internal const OtlpExportProtocol DefaultOtlpExportProtocol = OtlpExportProtocol.Grpc;  // <-----------------------Grpc is the default
    /*
    public enum OtlpExportProtocol : byte
    {
        Grpc = 0,
        HttpProtobuf = 1,
    }
    */

    internal static readonly KeyValuePair<string, string>[] StandardHeaders = new KeyValuePair<string, string>[]
    {
        new("User-Agent", GetUserAgentString()),
    };

    internal readonly Func<HttpClient> DefaultHttpClientFactory;

    private OtlpExportProtocol? protocol;
    private Uri? endpoint;
    private int? timeoutMilliseconds;
    private Func<HttpClient>? httpClientFactory;

    public OtlpExporterOptions() : this(OtlpExporterOptionsConfigurationType.Default) { }

    internal OtlpExporterOptions(OtlpExporterOptionsConfigurationType configurationType) : this(
       configuration: new ConfigurationBuilder().AddEnvironmentVariables().Build(),
       configurationType,
       defaultBatchOptions: new()) { }

    internal OtlpExporterOptions(IConfiguration configuration, OtlpExporterOptionsConfigurationType configurationType, BatchExportActivityProcessorOptions defaultBatchOptions)
    {
        this.ApplyConfiguration(configuration, configurationType);

        this.DefaultHttpClientFactory = () =>
        {
            return new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(this.TimeoutMilliseconds),
            };
        };

        this.BatchExportProcessorOptions = defaultBatchOptions!;
    }

    public Uri Endpoint
    {
        get
        {
            if (this.endpoint == null)
                return this.Protocol == OtlpExportProtocol.Grpc ? new Uri(DefaultGrpcEndpoint) : new Uri(DefaultHttpEndpoint);

            return this.endpoint;
        }
        set
        {
            this.endpoint = value;
            this.AppendSignalPathToEndpoint = false;
        }
    }

    public string? Headers { get; set; }

    public int TimeoutMilliseconds
    {
        get => this.timeoutMilliseconds ?? 10000;
        set => this.timeoutMilliseconds = value;
    }

    public OtlpExportProtocol Protocol
    {
        get => this.protocol ?? DefaultOtlpExportProtocol;
        set => this.protocol = value;
    }

    public ExportProcessorType ExportProcessorType { get; set; } = ExportProcessorType.Batch;

    public BatchExportProcessorOptions<Activity> BatchExportProcessorOptions { get; set; }

    public Func<HttpClient> HttpClientFactory
    {
        get => this.httpClientFactory ?? this.DefaultHttpClientFactory;
        set
        {
            this.httpClientFactory = value;
        }
    }

    internal bool AppendSignalPathToEndpoint { get; private set; } = true;

    internal bool HasData => this.protocol.HasValue || this.endpoint != null || this.timeoutMilliseconds.HasValue || this.httpClientFactory != null;

    internal static OtlpExporterOptions CreateOtlpExporterOptions(IServiceProvider serviceProvider, IConfiguration configuration, string name)
        => new(
            configuration,
            OtlpExporterOptionsConfigurationType.Default,
            serviceProvider.GetRequiredService<IOptionsMonitor<BatchExportActivityProcessorOptions>>().Get(name));

    internal void ApplyConfigurationUsingSpecificationEnvVars(
        IConfiguration configuration,
        string endpointEnvVarKey,
        bool appendSignalPathToEndpoint,
        string protocolEnvVarKey,
        string headersEnvVarKey,
        string timeoutEnvVarKey)
    {
        if (configuration.TryGetUriValue(OpenTelemetryProtocolExporterEventSource.Log, endpointEnvVarKey, out var endpoint))
        {
            this.endpoint = endpoint;
            this.AppendSignalPathToEndpoint = appendSignalPathToEndpoint;
        }

        if (configuration.TryGetValue<OtlpExportProtocol>(
            OpenTelemetryProtocolExporterEventSource.Log,
            protocolEnvVarKey,
            OtlpExportProtocolParser.TryParse,
            out var protocol))
        {
            this.Protocol = protocol;
        }

        if (configuration.TryGetStringValue(headersEnvVarKey, out var headers))
        {
            this.Headers = headers;
        }

        if (configuration.TryGetIntValue(OpenTelemetryProtocolExporterEventSource.Log, timeoutEnvVarKey, out var timeout))
        {
            this.TimeoutMilliseconds = timeout;
        }
    }

    internal OtlpExporterOptions ApplyDefaults(OtlpExporterOptions defaultExporterOptions)
    {
        this.protocol ??= defaultExporterOptions.protocol;

        this.endpoint ??= defaultExporterOptions.endpoint;

        // Note: We leave AppendSignalPathToEndpoint set to true here because we
        // want to append the signal if the endpoint came from the default
        // endpoint.

        this.Headers ??= defaultExporterOptions.Headers;

        this.timeoutMilliseconds ??= defaultExporterOptions.timeoutMilliseconds;

        this.httpClientFactory ??= defaultExporterOptions.httpClientFactory;

        return this;
    }

    private static string GetUserAgentString()
    {
        var assembly = typeof(OtlpExporterOptions).Assembly;
        return $"OTel-OTLP-Exporter-Dotnet/{assembly.GetPackageVersion()}";
    }

    private void ApplyConfiguration(IConfiguration configuration, OtlpExporterOptionsConfigurationType configurationType)
    {
        // Note: When using the "AddOtlpExporter" extensions configurationType
        // never has a value other than "Default" because OtlpExporterOptions is
        // shared by all signals and there is no way to differentiate which
        // signal is being constructed.
        if (configurationType == OtlpExporterOptionsConfigurationType.Default)
        {
            this.ApplyConfigurationUsingSpecificationEnvVars(
                configuration!,
                OtlpSpecConfigDefinitions.DefaultEndpointEnvVarName,
                appendSignalPathToEndpoint: true,
                OtlpSpecConfigDefinitions.DefaultProtocolEnvVarName,
                OtlpSpecConfigDefinitions.DefaultHeadersEnvVarName,
                OtlpSpecConfigDefinitions.DefaultTimeoutEnvVarName);
        }
        else if (configurationType == OtlpExporterOptionsConfigurationType.Logs)
        {
            this.ApplyConfigurationUsingSpecificationEnvVars(
                configuration!,
                OtlpSpecConfigDefinitions.LogsEndpointEnvVarName,
                appendSignalPathToEndpoint: false,
                OtlpSpecConfigDefinitions.LogsProtocolEnvVarName,
                OtlpSpecConfigDefinitions.LogsHeadersEnvVarName,
                OtlpSpecConfigDefinitions.LogsTimeoutEnvVarName);
        }
        else if (configurationType == OtlpExporterOptionsConfigurationType.Metrics)
        {
            // ...
        }
        else if (configurationType == OtlpExporterOptionsConfigurationType.Traces)
        {
            // ...
        }
        else
        {
            throw new NotSupportedException($"OtlpExporterOptionsConfigurationType '{configurationType}' is not supported.");
        }
    }
}
//-----------------------------------------------------Ʌ

//----------------------------V
public class OtlpTraceExporter : BaseExporter<Activity>
{
    private const int GrpcStartWritePosition = 5;
    private readonly SdkLimitOptions sdkLimitOptions;
    private readonly OtlpExporterTransmissionHandler transmissionHandler;
    private readonly int startWritePosition;

    private Resource? resource;

    private byte[] buffer = new byte[750000];

    public OtlpTraceExporter(OtlpExporterOptions options): this(options, sdkLimitOptions: new(), experimentalOptions: new(), transmissionHandler: null) { }

    internal OtlpTraceExporter(
        OtlpExporterOptions exporterOptions,
        SdkLimitOptions sdkLimitOptions,
        ExperimentalOptions experimentalOptions,
        OtlpExporterTransmissionHandler? transmissionHandler = null)
    {
        this.sdkLimitOptions = sdkLimitOptions!;
        this.startWritePosition = exporterOptions!.Protocol == OtlpExportProtocol.Grpc ? GrpcStartWritePosition : 0;
        this.transmissionHandler = transmissionHandler ?? exporterOptions!.GetExportTransmissionHandler(experimentalOptions, OtlpSignalType.Traces);  // <----------------
        // <--------------------OtlpExporterOptions's endpoint such as "http://localhost:4317" is "baked" into OtlpExporterTransmissionHandler
    }

    internal Resource Resource => this.resource ??= this.ParentProvider.GetResource();

    public override ExportResult Export(in Batch<Activity> activityBatch)   // serialize Activities to OTLP Protobuf and send to endpoint
    {
        // Prevents the exporter's gRPC and HTTP operations from being instrumented.
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            int writePosition = ProtobufOtlpTraceSerializer.WriteTraceData(
                ref this.buffer, 
                this.startWritePosition, 
                this.sdkLimitOptions, 
                this.Resource,   // <---------------------------rb4, Activity doesn't contain any "resource related" tags, only exporter takes in Resource and show in APM backend
                activityBatch);

            if (this.startWritePosition == GrpcStartWritePosition)
            {
                // Grpc payload consists of 3 parts byte 0 - Specifying if the payload is compressed.
                //  1-4 byte - Specifies the length of payload in big endian format.
                // 5 and above -  Protobuf serialized data.
                Span<byte> data = new Span<byte>(this.buffer, 1, 4);
                var dataLength = writePosition - GrpcStartWritePosition;
                BinaryPrimitives.WriteUInt32BigEndian(data, (uint)dataLength);
            }

            if (!this.transmissionHandler.TrySubmitRequest(this.buffer, writePosition))  // <------------------------------------
            {
                return ExportResult.Failure;
            }
        }
        catch (Exception ex)
        {
            OpenTelemetryProtocolExporterEventSource.Log.ExportMethodException(ex);
            return ExportResult.Failure;
        }

        return ExportResult.Success;
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => this.transmissionHandler.Shutdown(timeoutMilliseconds);
}
//----------------------------Ʌ

//--------------------------------------------V
internal class OtlpExporterTransmissionHandler : IDisposable
{
    public OtlpExporterTransmissionHandler(IExportClient exportClient, double timeoutMilliseconds)
    {
        this.ExportClient = exportClient;
        this.TimeoutMilliseconds = timeoutMilliseconds;
    }

    internal IExportClient ExportClient { get; }

    internal double TimeoutMilliseconds { get; }

    public bool TrySubmitRequest(byte[] request, int contentLength)  // <----------------------
    {
        try
        {
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(this.TimeoutMilliseconds);
            var response = this.ExportClient.SendExportRequest(request, contentLength, deadlineUtc);  // <---------------------
            if (response.Success)
            {
                return true;
            }

            return this.OnSubmitRequestFailure(request, contentLength, response);
        }
        catch (Exception ex)
        {
            OpenTelemetryProtocolExporterEventSource.Log.TrySubmitRequestException(ex);
            return false;
        }
    }

    public bool Shutdown(int timeoutMilliseconds)
    {
        var sw = timeoutMilliseconds == Timeout.Infinite ? null : Stopwatch.StartNew();

        this.OnShutdown(timeoutMilliseconds);

        if (sw != null)
        {
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

            return this.ExportClient.Shutdown((int)Math.Max(timeout, 0));
        }

        return this.ExportClient.Shutdown(timeoutMilliseconds);
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void OnShutdown(int timeoutMilliseconds) { }

    protected virtual bool OnSubmitRequestFailure(byte[] request, int contentLength, ExportClientResponse response) => false;

    protected bool TryRetryRequest(byte[] request, int contentLength, DateTime deadlineUtc, out ExportClientResponse response)
    {
        response = this.ExportClient.SendExportRequest(request, contentLength, deadlineUtc);
        return response.Success;
    }

    protected virtual void Dispose(bool disposing) { }
}
//--------------------------------------------Ʌ

//-------------------------------------------------V
internal static class OtlpExporterOptionsExtensions
{
    private const string TraceGrpcServicePath = "opentelemetry.proto.collector.trace.v1.TraceService/Export";
    private const string MetricsGrpcServicePath = "opentelemetry.proto.collector.metrics.v1.MetricsService/Export";
    private const string LogsGrpcServicePath = "opentelemetry.proto.collector.logs.v1.LogsService/Export";

    private const string TraceHttpServicePath = "v1/traces";
    private const string MetricsHttpServicePath = "v1/metrics";
    private const string LogsHttpServicePath = "v1/logs";

    public static THeaders GetHeaders<THeaders>(this OtlpExporterOptions options, Action<THeaders, string, string> addHeader) where THeaders : new()
    {
        var optionHeaders = options.Headers;
        var headers = new THeaders();
        if (!string.IsNullOrEmpty(optionHeaders))
        {
            // According to the specification, URL-encoded headers must be supported.
            optionHeaders = Uri.UnescapeDataString(optionHeaders);
            ReadOnlySpan<char> headersSpan = optionHeaders.AsSpan();

            while (!headersSpan.IsEmpty)
            {
                int commaIndex = headersSpan.IndexOf(',');
                ReadOnlySpan<char> pair;
                if (commaIndex == -1)
                {
                    pair = headersSpan;
                    headersSpan = [];
                }
                else
                {
                    pair = headersSpan.Slice(0, commaIndex);
                    headersSpan = headersSpan.Slice(commaIndex + 1);
                }

                int equalIndex = pair.IndexOf('=');
                if (equalIndex == -1)
                {
                    throw new ArgumentException("Headers provided in an invalid format.");
                }

                var key = pair.Slice(0, equalIndex).Trim().ToString();
                var value = pair.Slice(equalIndex + 1).Trim().ToString();
                addHeader(headers, key, value);
            }
        }

        foreach (var header in OtlpExporterOptions.StandardHeaders)
        {
            addHeader(headers, header.Key, header.Value);
        }

        return headers;
    }

    public static OtlpExporterTransmissionHandler GetExportTransmissionHandler(this OtlpExporterOptions options, ExperimentalOptions experimentalOptions, OtlpSignalType otlpSignalType)
    {
        IExportClient exportClient = GetExportClient(options, otlpSignalType);  // <---------------------------

        double timeoutMilliseconds = exportClient is OtlpHttpExportClient httpTraceExportClient
            ? httpTraceExportClient.HttpClient.Timeout.TotalMilliseconds
            : options.TimeoutMilliseconds;

        if (experimentalOptions.EnableInMemoryRetry)
        {
            return new OtlpExporterRetryTransmissionHandler(exportClient, timeoutMilliseconds);
        }
        else if (experimentalOptions.EnableDiskRetry)
        {
            return new OtlpExporterPersistentStorageTransmissionHandler(
                exportClient,
                timeoutMilliseconds,
                Path.Combine(experimentalOptions.DiskRetryDirectoryPath, "traces"));
        }
        else
        {
            return new OtlpExporterTransmissionHandler(exportClient, timeoutMilliseconds);
        }
    }

    public static IExportClient GetExportClient(this OtlpExporterOptions options, OtlpSignalType otlpSignalType)
    {
        var httpClient = options.HttpClientFactory?.Invoke() ?? throw new InvalidOperationException("OtlpExporterOptions was missing HttpClientFactory or it returned null.");

        if (options.Protocol != OtlpExportProtocol.Grpc && options.Protocol != OtlpExportProtocol.HttpProtobuf)
            throw new NotSupportedException($"Protocol {options.Protocol} is not supported.");

        return otlpSignalType switch
        {
            OtlpSignalType.Traces => options.Protocol == OtlpExportProtocol.Grpc
                ? new OtlpGrpcExportClient(options, httpClient, TraceGrpcServicePath)
                : new OtlpHttpExportClient(options, httpClient, TraceHttpServicePath),

            OtlpSignalType.Metrics => options.Protocol == OtlpExportProtocol.Grpc
                ? new OtlpGrpcExportClient(options, httpClient, MetricsGrpcServicePath)
                : new OtlpHttpExportClient(options, httpClient, MetricsHttpServicePath),

            OtlpSignalType.Logs => options.Protocol == OtlpExportProtocol.Grpc
                ? new OtlpGrpcExportClient(options, httpClient, LogsGrpcServicePath)
                : new OtlpHttpExportClient(options, httpClient, LogsHttpServicePath),

            _ => throw new NotSupportedException($"OtlpSignalType {otlpSignalType} is not supported."),
        };
    }

    public static void TryEnableIHttpClientFactoryIntegration(this OtlpExporterOptions options, IServiceProvider serviceProvider, string httpClientName)
    {
        if (serviceProvider != null && options.Protocol == OtlpExportProtocol.HttpProtobuf && options.HttpClientFactory == options.DefaultHttpClientFactory)
        {
            options.HttpClientFactory = () =>
            {
                Type? httpClientFactoryType = Type.GetType("System.Net.Http.IHttpClientFactory, Microsoft.Extensions.Http", throwOnError: false);
                if (httpClientFactoryType != null)
                {
                    object? httpClientFactory = serviceProvider.GetService(httpClientFactoryType);
                    if (httpClientFactory != null)
                    {
                        MethodInfo? createClientMethod = httpClientFactoryType.GetMethod(
                            "CreateClient",
                            BindingFlags.Public | BindingFlags.Instance,
                            binder: null,
                            [typeof(string)],
                            modifiers: null);
                        if (createClientMethod != null)
                        {
                            HttpClient? client = (HttpClient?)createClientMethod.Invoke(httpClientFactory, [httpClientName]);

                            if (client != null)
                            {
                                client.Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds);

                                return client;
                            }
                        }
                    }
                }

                return options.DefaultHttpClientFactory();
            };
        }
    }

    internal static Uri AppendPathIfNotPresent(this Uri uri, string path);
}
//-------------------------------------------------Ʌ

//--------------------------------------V
internal abstract class OtlpExportClient : IExportClient
{
    private static readonly Version Http2RequestVersion = new(2, 0);

    protected OtlpExportClient(OtlpExporterOptions options, HttpClient httpClient, string signalPath)
    {
        Uri exporterEndpoint;
        if (options.Protocol == OtlpExportProtocol.Grpc)
        {
            exporterEndpoint = options.Endpoint.AppendPathIfNotPresent(signalPath);
        }
        else
        {
            exporterEndpoint = options.AppendSignalPathToEndpoint
                ? options.Endpoint.AppendPathIfNotPresent(signalPath)
                : options.Endpoint;
        }

        this.Endpoint = new UriBuilder(exporterEndpoint).Uri;
        this.Headers = options.GetHeaders<Dictionary<string, string>>((d, k, v) => d.Add(k, v));
        this.HttpClient = httpClient;
    }

    internal HttpClient HttpClient { get; }

    internal Uri Endpoint { get; }

    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal abstract MediaTypeHeaderValue MediaTypeHeader { get; }

    internal virtual bool RequireHttp2 => false;

    public abstract ExportClientResponse SendExportRequest(byte[] buffer, int contentLength, DateTime deadlineUtc, CancellationToken cancellationToken = default);

    public bool Shutdown(int timeoutMilliseconds)
    {
        this.HttpClient.CancelPendingRequests();
        return true;
    }

    protected HttpRequestMessage CreateHttpRequest(byte[] buffer, int contentLength)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, this.Endpoint);

        if (this.RequireHttp2)
        {
            request.Version = Http2RequestVersion;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        foreach (var header in this.Headers)
        {
            request.Headers.Add(header.Key, header.Value);
        }

        // TODO: Support compression.

        request.Content = new ByteArrayContent(buffer, 0, contentLength);
        request.Content.Headers.ContentType = this.MediaTypeHeader;

        return request;
    }

    protected HttpResponseMessage SendHttpRequest(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return this.HttpClient.SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }
}
//--------------------------------------Ʌ

//----------------------------------------V
internal sealed class OtlpHttpExportClient : OtlpExportClient
{
    internal static readonly MediaTypeHeaderValue MediaHeaderValue = new("application/x-protobuf");
    private static readonly ExportClientHttpResponse SuccessExportResponse = new(success: true, deadlineUtc: default, response: null, exception: null);

    internal OtlpHttpExportClient(OtlpExporterOptions options, HttpClient httpClient, string signalPath): base(options, httpClient, signalPath) { }

    internal override MediaTypeHeaderValue MediaTypeHeader => MediaHeaderValue;

    public override ExportClientResponse SendExportRequest(byte[] buffer, int contentLength, DateTime deadlineUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpRequest = this.CreateHttpRequest(buffer, contentLength);  // <------------------
            using var httpResponse = this.SendHttpRequest(httpRequest, cancellationToken);

            try
            {
                httpResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                OpenTelemetryProtocolExporterEventSource.Log.HttpRequestFailed(this.Endpoint, ex);
                return new ExportClientHttpResponse(success: false, deadlineUtc: deadlineUtc, response: httpResponse, ex);
            }

            return SuccessExportResponse;
        }
        catch (HttpRequestException ex)
        {
            OpenTelemetryProtocolExporterEventSource.Log.FailedToReachCollector(this.Endpoint, ex);
            return new ExportClientHttpResponse(success: false, deadlineUtc: deadlineUtc, response: null, exception: ex);
        }
    }
}

internal sealed class OtlpGrpcExportClient : OtlpExportClient
{
    // ...
}
//----------------------------------------Ʌ

//------------------------------------------------V
public static class JaegerExporterHelperExtensions
{
    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder)
        => AddJaegerExporter(builder, name: null, configure: null);

    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder, Action<JaegerExporterOptions> configure)
        => AddJaegerExporter(builder, name: null, configure);

    public static TracerProviderBuilder AddJaegerExporter(this TracerProviderBuilder builder, string name, Action<JaegerExporterOptions> configure)
    {
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
                var jaegerSpan = activity.ToJaegerSpan();  // <-------------------! add tags such as "otel.status_code" and "otel.status_description"
                                                           // based on the activity's properties
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

//--------------------------------------------V
internal static class JaegerActivityExtensions
{
    internal const string JaegerErrorFlagTagName = "error";

    // ...

    public static JaegerSpan ToJaegerSpan(this Activity activity)
    {
        var jaegerTags = new TagEnumerationState
        {
            Tags = PooledList<JaegerTag>.Create(),
        };

        jaegerTags.EnumerateTags(activity);

        if (activity.Status != ActivityStatusCode.Unset)
        {
            if (activity.Status == ActivityStatusCode.Ok)
            {
                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(SpanAttributeConstants.StatusCodeKey, JaegerTagType.STRING, vStr: "OK"));
            }
            else
            {
                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(SpanAttributeConstants.StatusCodeKey, JaegerTagType.STRING, vStr: "ERROR"));
                    // <----------SpanAttributeConstants.StatusCodeKey is "otel.status_code"

                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(JaegerErrorFlagTagName, JaegerTagType.BOOL, vBool: true));

                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(SpanAttributeConstants.StatusDescriptionKey, JaegerTagType.STRING, vStr: activity.StatusDescription ?? string.Empty));
                    // <----------SpanAttributeConstants.StatusDescriptionKey is "otel.status_description"
            }
        }
        else if (jaegerTags.StatusCode.HasValue && jaegerTags.StatusCode != StatusCode.Unset)
        {
            PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(
                        SpanAttributeConstants.StatusCodeKey,
                        JaegerTagType.STRING,
                        vStr: StatusHelper.GetTagValueForStatusCode(jaegerTags.StatusCode.Value)));

            if (jaegerTags.StatusCode == StatusCode.Error)
            {
                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(JaegerErrorFlagTagName, JaegerTagType.BOOL, vBool: true));

                PooledList<JaegerTag>.Add(
                    ref jaegerTags.Tags,
                    new JaegerTag(SpanAttributeConstants.StatusDescriptionKey, JaegerTagType.STRING, vStr: jaegerTags.StatusDescription ?? string.Empty));
            }
        }

        string peerServiceName = null;
        if (activity.Kind == ActivityKind.Client || activity.Kind == ActivityKind.Producer)
        {
            PeerServiceResolver.Resolve(ref jaegerTags, out peerServiceName, out bool addAsTag);

            if (peerServiceName != null && addAsTag)
            {
                PooledList<JaegerTag>.Add(ref jaegerTags.Tags, new JaegerTag(SemanticConventions.AttributePeerService, JaegerTagType.STRING, vStr: peerServiceName));
            }
        }

        if (activity.Kind != ActivityKind.Internal)
        {
            string spanKind = null;

            if (activity.Kind == ActivityKind.Server)
                spanKind = "server";
            else if (activity.Kind == ActivityKind.Client)
                spanKind = "client";
            else if (activity.Kind == ActivityKind.Consumer)
                spanKind = "consumer";
            else if (activity.Kind == ActivityKind.Producer)
                spanKind = "producer";

            if (spanKind != null)
                PooledList<JaegerTag>.Add(ref jaegerTags.Tags, new JaegerTag("span.kind", JaegerTagType.STRING, vStr: spanKind));
        }

        var activitySource = activity.Source;
        if (!string.IsNullOrEmpty(activitySource.Name))
        {
            PooledList<JaegerTag>.Add(ref jaegerTags.Tags, new JaegerTag("otel.library.name", JaegerTagType.STRING, vStr: activitySource.Name));
            if (!string.IsNullOrEmpty(activitySource.Version))
            {
                PooledList<JaegerTag>.Add(ref jaegerTags.Tags, new JaegerTag("otel.library.version", JaegerTagType.STRING, vStr: activitySource.Version));
            }
        }

        var traceId = Int128.Empty;
        var spanId = Int128.Empty;
        var parentSpanId = Int128.Empty;

        if (activity.IdFormat == ActivityIdFormat.W3C)
        {
            // TODO: The check above should be enforced by the usage of the exporter. Perhaps enforce at higher-level.
            traceId = new Int128(activity.TraceId);
            spanId = new Int128(activity.SpanId);
            if (activity.ParentSpanId != default)
            {
                parentSpanId = new Int128(activity.ParentSpanId);
            }
        }

        return new JaegerSpan(
            peerServiceName: peerServiceName,
            traceIdLow: traceId.Low,
            traceIdHigh: traceId.High,
            spanId: spanId.Low,
            parentSpanId: parentSpanId.Low,
            operationName: activity.DisplayName,
            flags: (activity.Context.TraceFlags & ActivityTraceFlags.Recorded) > 0 ? 0x1 : 0,
            startTime: ToEpochMicroseconds(activity.StartTimeUtc),
            duration: activity.Duration.Ticks / TicksPerMicrosecond,
            references: activity.ToJaegerSpanRefs(),
            tags: jaegerTags.Tags,
            logs: activity.ToJaegerLogs());
    }

    // ...
}
//--------------------------------------------Ʌ
```

```C#
//-----------------------------------V
public readonly struct SamplingResult : IEquatable<SamplingResult>
{
    public SamplingResult(SamplingDecision decision) : this(decision, attributes: null, traceStateString: null) { }
    public SamplingResult(bool isSampled) : this(decision: isSampled ? SamplingDecision.RecordAndSample : SamplingDecision.Drop, attributes: null, traceStateString: null) { }
    public SamplingResult(SamplingDecision decision, IEnumerable<KeyValuePair<string, object>> attributes) : this(decision, attributes, traceStateString: null) { }
    public SamplingResult(SamplingDecision decision, string traceStateString) : this(decision, attributes: null, traceStateString) { }

   
    public SamplingResult(SamplingDecision decision, IEnumerable<KeyValuePair<string, object>>? attributes, string? traceStateString)
    {
        this.Decision = decision;

        this.Attributes = attributes ?? Enumerable.Empty<KeyValuePair<string, object>>();

        this.TraceStateString = traceStateString;
    }

    public SamplingDecision Decision { get; }

    public IEnumerable<KeyValuePair<string, object>> Attributes { get; }

    public string? TraceStateString { get; }

    public static bool operator ==(SamplingResult decision1, SamplingResult decision2) => decision1.Equals(decision2);

    public static bool operator !=(SamplingResult decision1, SamplingResult decision2) => !decision1.Equals(decision2);

    public override bool Equals(object? obj) => obj is SamplingResult other && this.Equals(other);

    public override int GetHashCode()
    {
        HashCode hashCode = default;
        hashCode.Add(this.Decision);
        hashCode.Add(this.Attributes);
        hashCode.Add(this.TraceStateString);

        var hash = hashCode.ToHashCode();

        return hash;
    }

    public bool Equals(SamplingResult other)
    {
        return this.Decision == other.Decision && this.Attributes.SequenceEqual(other.Attributes) && this.TraceStateString == other.TraceStateString;
    }
}
//-----------------------------------Ʌ

//----------------------------------V
public sealed class AlwaysOffSampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        return new SamplingResult(SamplingDecision.Drop);
    }
}
//----------------------------------Ʌ

//------------------------------------V
public sealed class ParentBasedSampler : Sampler
{
    private readonly Sampler rootSampler;

    private readonly Sampler remoteParentSampled;
    private readonly Sampler remoteParentNotSampled;
    private readonly Sampler localParentSampled;
    private readonly Sampler localParentNotSampled;

    public ParentBasedSampler(Sampler rootSampler)
    {
        this.rootSampler = rootSampler;
        this.Description = $"ParentBased{{{rootSampler.Description}}}";

        this.remoteParentSampled = new AlwaysOnSampler();
        this.remoteParentNotSampled = new AlwaysOffSampler();
        this.localParentSampled = new AlwaysOnSampler();  // <-----------------
        this.localParentNotSampled = new AlwaysOffSampler();
    }

    public ParentBasedSampler(Sampler rootSampler, Sampler? remoteParentSampled = null, Sampler? remoteParentNotSampled = null, Sampler? localParentSampled = null,  Sampler? localParentNotSampled = null) : this(rootSampler)
    {
        this.remoteParentSampled = remoteParentSampled ?? new AlwaysOnSampler();
        this.remoteParentNotSampled = remoteParentNotSampled ?? new AlwaysOffSampler();
        this.localParentSampled = localParentSampled ?? new AlwaysOnSampler();
        this.localParentNotSampled = localParentNotSampled ?? new AlwaysOffSampler();
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var parentContext = samplingParameters.ParentContext;
        if (parentContext.TraceId == default)
        {
            // If no parent, use the rootSampler to determine sampling.
            return this.rootSampler.ShouldSample(samplingParameters);
        }
      
        if ((parentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0)  // when parent is sampled
        {
            if (parentContext.IsRemote)
                return this.remoteParentSampled.ShouldSample(samplingParameters);
            else
                return this.localParentSampled.ShouldSample(samplingParameters);  // localParentSampled is AlwaysOnSampler by default
        }

        // If parent is not sampled => delegate to the "not sampled" inner samplers.
        if (parentContext.IsRemote)
            return this.remoteParentNotSampled.ShouldSample(samplingParameters);
        else
            return this.localParentNotSampled.ShouldSample(samplingParameters);
    }
}
//------------------------------------Ʌ

//------------------------------------------V
public sealed class TraceIdRatioBasedSampler : Sampler
{
    private readonly long idUpperBound;
    private readonly double probability;

    public TraceIdRatioBasedSampler(double probability)
    {
        this.probability = probability;

        // The expected description is like TraceIdRatioBasedSampler{0.000100}
        this.Description = "TraceIdRatioBasedSampler{" + this.probability.ToString("F6", CultureInfo.InvariantCulture) + "}";

        if (this.probability == 0.0)
        {
            this.idUpperBound = long.MinValue;
        }
        else if (this.probability == 1.0)
        {
            this.idUpperBound = long.MaxValue;
        }
        else
        {
            this.idUpperBound = (long)(probability * long.MaxValue);
        }
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        Span<byte> traceIdBytes = stackalloc byte[16];
        samplingParameters.TraceId.CopyTo(traceIdBytes);
        return new SamplingResult(Math.Abs(GetLowerLong(traceIdBytes)) < this.idUpperBound);
    }

    private static long GetLowerLong(ReadOnlySpan<byte> bytes)
    {
        long result = 0;
        for (var i = 0; i < 8; i++)
        {
            result <<= 8;
            result |= bytes[i] & 0xff;
        }

        return result;
    }
}
//------------------------------------------Ʌ

//---------------------------------------V
public readonly struct SamplingParameters
{
    public SamplingParameters(ActivityContext parentContext, ActivityTraceId traceId, string name, ActivityKind kind, IEnumerable<KeyValuePair<string, object?>>? tags = null,
                              IEnumerable<ActivityLink>? links = null)
    {
        this.ParentContext = parentContext;
        this.TraceId = traceId;
        this.Kind = kind;
        this.Tags = tags;
        this.Links = links;

        this.Name = name ?? string.Empty;
    }

    public ActivityContext ParentContext { get; }

    public ActivityTraceId TraceId { get; }

    public string Name { get; }

    public ActivityKind Kind { get; }

    public IEnumerable<KeyValuePair<string, object?>>? Tags { get; }

    public IEnumerable<ActivityLink>? Links { get; }
}
//---------------------------------------Ʌ
```

===============================================================================


```C#
//-----------------------------V
public static class Propagators
{
    private static readonly TextMapPropagator Noop = new NoopTextMapPropagator();

    public static TextMapPropagator DefaultTextMapPropagator { get; internal set; } = Noop;

    internal static void Reset()
    {
        DefaultTextMapPropagator = Noop;
    }
}
//-----------------------------Ʌ

//---------------------------------------V
public readonly struct PropagationContext : IEquatable<PropagationContext>
{
    public PropagationContext(ActivityContext activityContext, Baggage baggage)
    {
        this.ActivityContext = activityContext;
        this.Baggage = baggage;
    }

    public ActivityContext ActivityContext { get; }

    public Baggage Baggage { get; }

    public bool Equals(PropagationContext value)
    {
        return this.ActivityContext == value.ActivityContext && this.Baggage == value.Baggage;
    }

    // ...
}
//---------------------------------------Ʌ

//-------------------------------------V
public abstract class TextMapPropagator
{
    public abstract ISet<string>? Fields { get; }

    public abstract void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter);

    public abstract PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter);
}
//-------------------------------------Ʌ

//-----------------------------------------V
internal sealed class NoopTextMapPropagator : TextMapPropagator
{
    private static readonly PropagationContext DefaultPropagationContext = default;

    public override ISet<string>? Fields => null;

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
    {
        return DefaultPropagationContext;
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter) { }
}
//-----------------------------------------Ʌ

//-------------------------------------V
public class CompositeTextMapPropagator : TextMapPropagator
{
    private readonly List<TextMapPropagator> propagators;
    private readonly ISet<string> allFields;

    public CompositeTextMapPropagator(IEnumerable<TextMapPropagator> propagators)
    {
        Guard.ThrowIfNull(propagators);

        var propagatorsList = new List<TextMapPropagator>();

        foreach (var propagator in propagators)
        {
            if (propagator is not null)
            {
                propagatorsList.Add(propagator);
            }
        }

        this.propagators = propagatorsList;

        // For efficiency, we resolve the fields from all propagators only once, as they are
        // not expected to change (although the implementation doesn't strictly prevent that).
        if (this.propagators.Count == 0)
        {
            // Use a new empty HashSet for each instance to avoid any potential mutation issues.
            this.allFields = new HashSet<string>();
        }
        else
        {
            ISet<string>? fields = this.propagators[0].Fields;

            var output = fields is not null
                ? new HashSet<string>(fields)
                : [];

            for (int i = 1; i < this.propagators.Count; i++)
            {
                fields = this.propagators[i].Fields;
                if (fields is not null)
                {
                    output.UnionWith(fields);
                }
            }

            this.allFields = output;
        }
    }

    public override ISet<string> Fields => this.allFields;

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
    {
        for (int i = 0; i < this.propagators.Count; i++)
        {
            context = this.propagators[i].Extract(context, carrier, getter);
        }

        return context;
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
    {
        for (int i = 0; i < this.propagators.Count; i++)
        {
            this.propagators[i].Inject(context, carrier, setter);
        }
    }
}
//-------------------------------------Ʌ

//---------------------------------V
public class TraceContextPropagator : TextMapPropagator
{
    private const string TraceParent = "traceparent";
    private const string TraceState = "tracestate";

    private const int TraceStateKeyMaxLength = 256;
    private const int TraceStateKeyTenantMaxLength = 241;
    private const int TraceStateKeyVendorMaxLength = 14;
    private const int TraceStateValueMaxLength = 256;

    private static readonly int VersionPrefixIdLength = "00-".Length;
    private static readonly int TraceIdLength = "0af7651916cd43dd8448eb211c80319c".Length;
    private static readonly int VersionAndTraceIdLength = "00-0af7651916cd43dd8448eb211c80319c-".Length;
    private static readonly int SpanIdLength = "00f067aa0ba902b7".Length;
    private static readonly int VersionAndTraceIdAndSpanIdLength = "00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-".Length;
    private static readonly int OptionsLength = "00".Length;
    private static readonly int TraceparentLengthV0 = "00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-00".Length;

    public override ISet<string> Fields => new HashSet<string> { TraceState, TraceParent };

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
    {
        if (context.ActivityContext.IsValid())
        {
            // if a valid context has already been extracted, perform a noop.
            return context;
        }

        // ...

        try
        {
            var traceparentCollection = getter(carrier, TraceParent);

            // There must be a single traceparent
            if (traceparentCollection == null || traceparentCollection.Count() != 1)
            {
                return context;
            }

            var traceparent = traceparentCollection.First();
            var traceparentParsed = TryExtractTraceparent(traceparent, out var traceId, out var spanId, out var traceoptions);

            if (!traceparentParsed)
            {
                return context;
            }

            string? tracestate = null;
            var tracestateCollection = getter(carrier, TraceState);
            if (tracestateCollection?.Any() ?? false)
            {
                TryExtractTracestate([.. tracestateCollection], out tracestate);
            }

            return new PropagationContext(
                new ActivityContext(traceId, spanId, traceoptions, tracestate, isRemote: true),
                context.Baggage);
        }
        catch (Exception ex)
        {
            OpenTelemetryApiEventSource.Log.ActivityContextExtractException(nameof(TraceContextPropagator), ex);
        }

        // in case of exception indicate to upstream that there is no parseable context from the top
        return context;
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
    {
        // ...
        var traceparent = string.Concat("00-", context.ActivityContext.TraceId.ToHexString(), "-", context.ActivityContext.SpanId.ToHexString());
        traceparent = string.Concat(traceparent, (context.ActivityContext.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? "-01" : "-00");

        setter(carrier, "traceparent", traceparent);

        string? tracestateStr = context.ActivityContext.TraceState;
        if (tracestateStr?.Length > 0)
        {
            setter(carrier, "tracestate", tracestateStr);
        }
    }

    internal static bool TryExtractTraceparent(string traceparent, out ActivityTraceId traceId, out ActivitySpanId spanId, out ActivityTraceFlags traceOptions)
    {
        // from https://github.com/w3c/distributed-tracing/blob/master/trace_context/HTTP_HEADER_FORMAT.md
        // traceparent: 00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-01

        traceId = default;
        spanId = default;
        traceOptions = default;
        var bestAttempt = false;

        if (string.IsNullOrWhiteSpace(traceparent) || traceparent.Length < TraceparentLengthV0)
        {
            return false;
        }

        // if version does not end with delimiter
        if (traceparent[VersionPrefixIdLength - 1] != '-')
        {
            return false;
        }

        // or version is not a hex (will throw)
        var version0 = HexCharToByte(traceparent[0]);
        var version1 = HexCharToByte(traceparent[1]);

        if (version0 == 0xf && version1 == 0xf)
        {
            return false;
        }

        if (version0 > 0)
        {
            // expected version is 00
            // for higher versions - best attempt parsing of trace id, span id, etc.
            bestAttempt = true;
        }

        if (traceparent[VersionAndTraceIdLength - 1] != '-')
        {
            return false;
        }

        try
        {
            traceId = ActivityTraceId.CreateFromString(traceparent.AsSpan().Slice(VersionPrefixIdLength, TraceIdLength));
        }
        catch (ArgumentOutOfRangeException)
        {
            // it's ok to still parse tracestate
            return false;
        }

        if (traceparent[VersionAndTraceIdAndSpanIdLength - 1] != '-')
        {
            return false;
        }

        byte optionsLowByte;
        try
        {
            spanId = ActivitySpanId.CreateFromString(traceparent.AsSpan().Slice(VersionAndTraceIdLength, SpanIdLength));
            _ = HexCharToByte(traceparent[VersionAndTraceIdAndSpanIdLength]); // to verify if there is no bad chars on options position
            optionsLowByte = HexCharToByte(traceparent[VersionAndTraceIdAndSpanIdLength + 1]);
        }
        catch (ArgumentOutOfRangeException)
        {
            // it's ok to still parse tracestate
            return false;
        }

        if ((optionsLowByte & 1) == 1)
        {
            traceOptions |= ActivityTraceFlags.Recorded;
        }

        if ((!bestAttempt) && (traceparent.Length != VersionAndTraceIdAndSpanIdLength + OptionsLength))
        {
            return false;
        }

        if (bestAttempt)
        {
            if ((traceparent.Length > TraceparentLengthV0) && (traceparent[TraceparentLengthV0] != '-'))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryExtractTracestate(string[] tracestateCollection, out string tracestateResult)
    {
        tracestateResult = string.Empty;

        if (tracestateCollection != null)
        {
            var keySet = new HashSet<string>();
            var result = new StringBuilder();
            for (int i = 0; i < tracestateCollection.Length; ++i)
            {
                var tracestate = tracestateCollection[i].AsSpan();
                int begin = 0;
                while (begin < tracestate.Length)
                {
                    int length = tracestate.Slice(begin).IndexOf(',');
                    ReadOnlySpan<char> listMember;
                    if (length != -1)
                    {
                        listMember = tracestate.Slice(begin, length).Trim();
                        begin += length + 1;
                    }
                    else
                    {
                        listMember = tracestate.Slice(begin).Trim();
                        begin = tracestate.Length;
                    }

                    if (listMember.IsEmpty)
                    {
                        // Empty and whitespace - only list members are allowed.
                        // Vendors MUST accept empty tracestate headers but SHOULD avoid sending them.
                        continue;
                    }

                    if (keySet.Count >= 32)
                    {
                        return false;
                    }

                    int keyLength = listMember.IndexOf('=');
                    if (keyLength == listMember.Length || keyLength == -1)
                    {
                        // Missing key or value in tracestate
                        return false;
                    }

                    var key = listMember.Slice(0, keyLength);
                    if (!ValidateKey(key))
                    {
                        // test_tracestate_key_illegal_characters in https://github.com/w3c/trace-context/blob/master/test/test.py
                        // test_tracestate_key_length_limit
                        // test_tracestate_key_illegal_vendor_format
                        return false;
                    }

                    var value = listMember.Slice(keyLength + 1);
                    if (!ValidateValue(value))
                    {
                        // test_tracestate_value_illegal_characters
                        return false;
                    }

                    // ValidateKey() call above has ensured the key does not contain upper case letters.
                    if (!keySet.Add(key.ToString()))
                    {
                        // test_tracestate_duplicated_keys
                        return false;
                    }

                    if (result.Length > 0)
                    {
                        result.Append(',');
                    }

                    result.Append(listMember);
                }
            }

            tracestateResult = result.ToString();
        }

        return true;
    }

    private static byte HexCharToByte(char c)
    {
        if ((c >= '0') && (c <= '9'))
            return (byte)(c - '0');

        if ((c >= 'a') && (c <= 'f'))
            return (byte)(c - 'a' + 10);

        throw new ArgumentOutOfRangeException(nameof(c), c, "Must be within: [0-9] or [a-f]");
    }

    private static bool ValidateKey(ReadOnlySpan<char> key)
    {
        if (key.Length <= 0 || key.Length > TraceStateKeyMaxLength)
        {
            return false;
        }

        if (!IsLowerAlphaDigit(key[0]))
        {
            return false;
        }

        int tenantLength = -1;
        for (int i = 1; i < key.Length; ++i)
        {
            char ch = key[i];
            if (ch == '@')
            {
                tenantLength = i;
                break;
            }

            if (!(IsLowerAlphaDigit(ch) || ch == '_' || ch == '-' || ch == '*' || ch == '/'))
                return false;
        }

        if (tenantLength == -1)
        {
            // There is no "@" sign. The key follow the first format.
            return true;
        }

        if (tenantLength == 0 || tenantLength > TraceStateKeyTenantMaxLength)
        {
            return false;
        }

        int vendorLength = key.Length - tenantLength - 1;
        if (vendorLength == 0 || vendorLength > TraceStateKeyVendorMaxLength)
        {
            return false;
        }

        for (int i = tenantLength + 1; i < key.Length; ++i)
        {
            char ch = key[i];
            if (!(IsLowerAlphaDigit(ch) || ch == '_' || ch == '-' || ch == '*' || ch == '/'))
                return false;
        }

        return true;
    }

    private static bool ValidateValue(ReadOnlySpan<char> value)
    {
        if (value.Length <= 0 || value.Length > TraceStateValueMaxLength)
            return false;

        for (int i = 0; i < value.Length - 1; ++i)
        {
            char c = value[i];
            if (!(c >= 0x20 && c <= 0x7E && c != 0x2C && c != 0x3D))
            {
                return false;
            }
        }

        char last = value[value.Length - 1];
        return last >= 0x21 && last <= 0x7E && last != 0x2C && last != 0x3D;
    }

    private static bool IsLowerAlphaDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z');
   

    private static void WriteTraceParentIntoSpan(Span<char> destination, ActivityContext context)
    {
        "00-".CopyTo(destination);
        context.TraceId.ToHexString().CopyTo(destination.Slice(3));
        destination[35] = '-';
        context.SpanId.ToHexString().CopyTo(destination.Slice(36));
        if ((context.TraceFlags & ActivityTraceFlags.Recorded) != 0)
        {
            "-01".CopyTo(destination.Slice(52));
        }
        else
        {
            "-00".CopyTo(destination.Slice(52));
        }
    }
}
//---------------------------------Ʌ

//----------------------------V
public class BaggagePropagator : TextMapPropagator
{
    internal const string BaggageHeaderName = "baggage";

    private const int MaxBaggageLength = 8192;
    private const int MaxBaggageItems = 180;

    private static readonly char[] EqualSignSeparator = ['='];
    private static readonly char[] CommaSignSeparator = [','];

    public override ISet<string> Fields => new HashSet<string> { BaggageHeaderName };

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
    {
        if (context.Baggage != default)
        {
            // If baggage has already been extracted, perform a noop.
            return context;
        }

        if (carrier == null)
        {
            OpenTelemetryApiEventSource.Log.FailedToExtractBaggage(nameof(BaggagePropagator), "null carrier");
            return context;
        }

        if (getter == null)
        {
            OpenTelemetryApiEventSource.Log.FailedToExtractBaggage(nameof(BaggagePropagator), "null getter");
            return context;
        }

        try
        {
            var baggageCollection = getter(carrier, BaggageHeaderName);
            if (baggageCollection?.Any() ?? false)
            {
                if (TryExtractBaggage([.. baggageCollection], out var baggage))
                {
                    return new PropagationContext(context.ActivityContext, new Baggage(baggage!));
                }
            }

            return new PropagationContext(context.ActivityContext, context.Baggage);
        }
        catch (Exception ex)
        {
            OpenTelemetryApiEventSource.Log.BaggageExtractException(nameof(BaggagePropagator), ex);
        }

        return context;
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
    {
        if (carrier == null)
        {
            OpenTelemetryApiEventSource.Log.FailedToInjectBaggage(nameof(BaggagePropagator), "null carrier");
            return;
        }

        if (setter == null)
        {
            OpenTelemetryApiEventSource.Log.FailedToInjectBaggage(nameof(BaggagePropagator), "null setter");
            return;
        }

        using var e = context.Baggage.GetEnumerator();

        if (e.MoveNext() == true)
        {
            int itemCount = 0;
            StringBuilder baggage = new StringBuilder();
            do
            {
                KeyValuePair<string, string> item = e.Current;
                if (string.IsNullOrEmpty(item.Value))
                {
                    continue;
                }

                baggage.Append(WebUtility.UrlEncode(item.Key)).Append('=').Append(WebUtility.UrlEncode(item.Value)).Append(',');
            }
            while (e.MoveNext() && ++itemCount < MaxBaggageItems && baggage.Length < MaxBaggageLength);
            baggage.Remove(baggage.Length - 1, 1);
            setter(carrier, BaggageHeaderName, baggage.ToString());
        }
    }

    internal static bool TryExtractBaggage(string[] baggageCollection, out Dictionary<string, string>? baggage)
    {
        int baggageLength = -1;
        bool done = false;
        Dictionary<string, string>? baggageDictionary = null;

        foreach (var item in baggageCollection)
        {
            if (done)
                break;

            if (string.IsNullOrEmpty(item))
                continue;

            foreach (var pair in item.Split(CommaSignSeparator))
            {
                baggageLength += pair.Length + 1; // pair and comma

                if (baggageLength >= MaxBaggageLength || baggageDictionary?.Count >= MaxBaggageItems)
                {
                    done = true;
                    break;
                }

                if (pair.IndexOf('=') < 0)
                    continue;

                var parts = pair.Split(EqualSignSeparator, 2);
                if (parts.Length != 2)
                    continue;

                var key = WebUtility.UrlDecode(parts[0]);
                var value = WebUtility.UrlDecode(parts[1]);

                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    continue;

                baggageDictionary ??= [];

                baggageDictionary[key] = value;
            }
        }

        baggage = baggageDictionary;
        return baggageDictionary != null;
    }
}
//----------------------------Ʌ
```

================================================================================

```C#
//------------------------------------------V
namespace OpenTelemetry.Trace;

internal static class SemanticConventions
{
    public const string AttributeNetTransport = "net.transport";
    public const string AttributeNetPeerIp = "net.peer.ip";
    public const string AttributeNetPeerPort = "net.peer.port";
    public const string AttributeNetPeerName = "net.peer.name";
    public const string AttributeNetHostIp = "net.host.ip";
    public const string AttributeNetHostPort = "net.host.port";
    public const string AttributeNetHostName = "net.host.name";

    public const string AttributeEnduserId = "enduser.id";
    public const string AttributeEnduserRole = "enduser.role";
    public const string AttributeEnduserScope = "enduser.scope";

    public const string AttributeHttpMethod = "http.method";
    public const string AttributeHttpUrl = "http.url";
    public const string AttributeHttpTarget = "http.target";
    public const string AttributeHttpHost = "http.host";
    public const string AttributeHttpScheme = "http.scheme";
    public const string AttributeHttpStatusCode = "http.status_code";
    public const string AttributeHttpStatusText = "http.status_text";
    public const string AttributeHttpFlavor = "http.flavor";
    public const string AttributeHttpServerName = "http.server_name";
    public const string AttributeHttpRoute = "http.route";  // <------------------------------------
    public const string AttributeHttpClientIP = "http.client_ip";
    public const string AttributeHttpUserAgent = "http.user_agent";
    public const string AttributeHttpRequestContentLength = "http.request_content_length";
    public const string AttributeHttpRequestContentLengthUncompressed = "http.request_content_length_uncompressed";
    public const string AttributeHttpResponseContentLength = "http.response_content_length";
    public const string AttributeHttpResponseContentLengthUncompressed = "http.response_content_length_uncompressed";

    public const string AttributeDbConnectionString = "db.connection_string";
    public const string AttributeDbUser = "db.user";
    public const string AttributeDbMsSqlInstanceName = "db.mssql.instance_name";
    public const string AttributeDbJdbcDriverClassName = "db.jdbc.driver_classname";
    public const string AttributeDbName = "db.name";
    public const string AttributeDbStatement = "db.statement";
    public const string AttributeDbSystem = "db.system";
    public const string AttributeDbOperation = "db.operation";
    public const string AttributeDbInstance = "db.instance";
    public const string AttributeDbCassandraKeyspace = "db.cassandra.keyspace";
    public const string AttributeDbHBaseNamespace = "db.hbase.namespace";
    public const string AttributeDbRedisDatabaseIndex = "db.redis.database_index";
    public const string AttributeDbMongoDbCollection = "db.mongodb.collection";

    public const string AttributeRpcSystem = "rpc.system";
    public const string AttributeRpcService = "rpc.service";
    public const string AttributeRpcMethod = "rpc.method";
    public const string AttributeRpcGrpcStatusCode = "rpc.grpc.status_code";

    public const string AttributeMessageType = "message.type";
    public const string AttributeMessageId = "message.id";
    public const string AttributeMessageCompressedSize = "message.compressed_size";
    public const string AttributeMessageUncompressedSize = "message.uncompressed_size";

    public const string AttributeFaasTrigger = "faas.trigger";
    public const string AttributeFaasExecution = "faas.execution";
    public const string AttributeFaasDocumentCollection = "faas.document.collection";
    public const string AttributeFaasDocumentOperation = "faas.document.operation";
    public const string AttributeFaasDocumentTime = "faas.document.time";
    public const string AttributeFaasDocumentName = "faas.document.name";
    public const string AttributeFaasTime = "faas.time";
    public const string AttributeFaasCron = "faas.cron";

    public const string AttributeMessagingSystem = "messaging.system";
    public const string AttributeMessagingDestination = "messaging.destination";
    public const string AttributeMessagingDestinationKind = "messaging.destination_kind";
    public const string AttributeMessagingTempDestination = "messaging.temp_destination";
    public const string AttributeMessagingProtocol = "messaging.protocol";
    public const string AttributeMessagingProtocolVersion = "messaging.protocol_version";
    public const string AttributeMessagingUrl = "messaging.url";
    public const string AttributeMessagingMessageId = "messaging.message_id";
    public const string AttributeMessagingConversationId = "messaging.conversation_id";
    public const string AttributeMessagingPayloadSize = "messaging.message_payload_size_bytes";
    public const string AttributeMessagingPayloadCompressedSize = "messaging.message_payload_compressed_size_bytes";
    public const string AttributeMessagingOperation = "messaging.operation";

    public const string AttributeExceptionEventName = "exception";
    public const string AttributeExceptionType = "exception.type";
    public const string AttributeExceptionMessage = "exception.message";
    public const string AttributeExceptionStacktrace = "exception.stacktrace";
    public const string AttributeErrorType = "error.type";

    public const string AttributeHttpRequestMethod = "http.request.method"; // replaces: "http.method" (AttributeHttpMethod)
    public const string AttributeHttpRequestMethodOriginal = "http.request.method_original";
    public const string AttributeHttpResponseStatusCode = "http.response.status_code"; // replaces: "http.status_code" (AttributeHttpStatusCode)
    public const string AttributeUrlScheme = "url.scheme"; // replaces: "http.scheme" (AttributeHttpScheme)
    public const string AttributeUrlFull = "url.full"; // replaces: "http.url" (AttributeHttpUrl)
    public const string AttributeUrlPath = "url.path"; // replaces: "http.target" (AttributeHttpTarget)
    public const string AttributeUrlQuery = "url.query"; // replaces: "http.target" (AttributeHttpTarget)
    public const string AttributeServerSocketAddress = "server.socket.address"; // replaces: "net.peer.ip" (AttributeNetPeerIp)

    public const string AttributeClientAddress = "client.address";
    public const string AttributeClientPort = "client.port";
    public const string AttributeNetworkProtocolVersion = "network.protocol.version"; // replaces: "http.flavor" (AttributeHttpFlavor)
    public const string AttributeNetworkProtocolName = "network.protocol.name";
    public const string AttributeServerAddress = "server.address"; // replaces: "net.host.name" (AttributeNetHostName)
    public const string AttributeServerPort = "server.port"; // replaces: "net.host.port" (AttributeNetHostPort)
    public const string AttributeUserAgentOriginal = "user_agent.original"; // replaces: http.user_agent (AttributeHttpUserAgent)

    // v1.23.0 Database spans
    public const string AttributeNetworkPeerAddress = "network.peer.address"; // replaces: "net.peer.ip" (AttributeNetPeerIp)
    public const string AttributeNetworkPeerPort = "network.peer.port"; // replaces: "net.peer.port" (AttributeNetPeerPort)

    // v1.24.0 Messaging spans
    public const string AttributeMessagingClientId = "messaging.client_id";
    public const string AttributeMessagingDestinationName = "messaging.destination.name";

    // v1.24.0 Messaging metrics
    public const string MetricMessagingPublishDuration = "messaging.publish.duration";
    public const string MetricMessagingPublishMessages = "messaging.publish.messages";
    public const string MetricMessagingReceiveDuration = "messaging.receive.duration";
    public const string MetricMessagingReceiveMessages = "messaging.receive.messages";

    // v1.24.0 Messaging (Kafka)
    public const string AttributeMessagingKafkaConsumerGroup = "messaging.kafka.consumer.group";
    public const string AttributeMessagingKafkaDestinationPartition = "messaging.kafka.destination.partition";
    public const string AttributeMessagingKafkaMessageKey = "messaging.kafka.message.key";
    public const string AttributeMessagingKafkaMessageOffset = "messaging.kafka.message.offset";

    // New database conventions:
    public const string AttributeDbCollectionName = "db.collection.name";
    public const string AttributeDbOperationName = "db.operation.name";
    public const string AttributeDbSystemName = "db.system.name";
    public const string AttributeDbNamespace = "db.namespace";
    public const string AttributeDbResponseStatusCode = "db.response.status_code";
    public const string AttributeDbOperationBatchSize = "db.operation.batch.size";
    public const string AttributeDbQuerySummary = "db.query.summary";
    public const string AttributeDbQueryText = "db.query.text";
    public const string AttributeDbStoredProcedureName = "db.stored_procedure.name";
}
//------------------------------------------Ʌ

//-----------------------------------------------V
internal static class ResourceSemanticConventions
{
    public const string AttributeServiceName = "service.name";  // <------------------------------
    public const string AttributeServiceNamespace = "service.namespace";
    public const string AttributeServiceInstance = "service.instance.id";
    public const string AttributeServiceVersion = "service.version";

    public const string AttributeTelemetrySdkName = "telemetry.sdk.name";
    public const string AttributeTelemetrySdkLanguage = "telemetry.sdk.language";
    public const string AttributeTelemetrySdkVersion = "telemetry.sdk.version";

    public const string AttributeContainerName = "container.name";
    public const string AttributeContainerImage = "container.image.name";
    public const string AttributeContainerTag = "container.image.tag";

    public const string AttributeFaasName = "faas.name";
    public const string AttributeFaasId = "faas.id";
    public const string AttributeFaasVersion = "faas.version";
    public const string AttributeFaasInstance = "faas.instance";

    public const string AttributeK8sCluster = "k8s.cluster.name";
    public const string AttributeK8sNamespace = "k8s.namespace.name";
    public const string AttributeK8sPod = "k8s.pod.name";
    public const string AttributeK8sDeployment = "k8s.deployment.name";

    public const string AttributeHostHostname = "host.hostname";
    public const string AttributeHostId = "host.id";
    public const string AttributeHostName = "host.name";
    public const string AttributeHostType = "host.type";
    public const string AttributeHostImageName = "host.image.name";
    public const string AttributeHostImageId = "host.image.id";
    public const string AttributeHostImageVersion = "host.image.version";

    public const string AttributeProcessId = "process.id";
    public const string AttributeProcessExecutableName = "process.executable.name";
    public const string AttributeProcessExecutablePath = "process.executable.path";
    public const string AttributeProcessCommand = "process.command";
    public const string AttributeProcessCommandLine = "process.command_line";
    public const string AttributeProcessUsername = "process.username";

    public const string AttributeCloudProvider = "cloud.provider";
    public const string AttributeCloudAccount = "cloud.account.id";
    public const string AttributeCloudRegion = "cloud.region";
    public const string AttributeCloudZone = "cloud.zone";
    public const string AttributeComponent = "component";
}
//-----------------------------------------------Ʌ

//--------------------------------V
public static class OtelAttributes
{
    public const string AttributeOtelComponentName = "otel.component.name";
    public const string AttributeOtelComponentType = "otel.component.type";
    public const string AttributeOtelScopeName = "otel.scope.name";
    public const string AttributeOtelScopeVersion = "otel.scope.version";
    public const string AttributeOtelSpanSamplingResult = "otel.span.sampling_result";
    public const string AttributeOtelStatusCode = "otel.status_code";
    public const string AttributeOtelStatusDescription = "otel.status_description";

    public static class OtelComponentTypeValues
    {
        public const string BatchingSpanProcessor = "batching_span_processor";
        public const string SimpleSpanProcessor = "simple_span_processor";
        public const string BatchingLogProcessor = "batching_log_processor";
        public const string SimpleLogProcessor = "simple_log_processor";
        public const string OtlpGrpcSpanExporter = "otlp_grpc_span_exporter";
        public const string OtlpHttpSpanExporter = "otlp_http_span_exporter";
        public const string OtlpHttpJsonSpanExporter = "otlp_http_json_span_exporter";
        public const string OtlpGrpcLogExporter = "otlp_grpc_log_exporter";
        public const string OtlpHttpLogExporter = "otlp_http_log_exporter";
        public const string OtlpHttpJsonLogExporter = "otlp_http_json_log_exporter";
        public const string PeriodicMetricReader = "periodic_metric_reader";
        public const string OtlpGrpcMetricExporter = "otlp_grpc_metric_exporter";
        public const string OtlpHttpMetricExporter = "otlp_http_metric_exporter";
        public const string OtlpHttpJsonMetricExporter = "otlp_http_json_metric_exporter";
    }

    public static class OtelSpanSamplingResultValues
    {
        public const string RecordAndSample = "RECORD_AND_SAMPLE";
    }

    public static class OtelStatusCodeValues
    {
        public const string Ok = "OK";
        public const string Error = "ERROR";
    }
}
//--------------------------------Ʌ

//------------------------------------------V
internal static class SpanAttributeConstants
{
    public const string StatusCodeKey = "otel.status_code";
    public const string StatusDescriptionKey = "otel.status_description";
}
//------------------------------------------Ʌ
```