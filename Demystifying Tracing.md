## Demystifying Distributed Tracing in .NET

The word "telemetry" originates from the Greek roots "tele" meaning "remote" or "far," and "metron," meaning "measure". Oxford Advanced Learner's Dictionary defines *Telemetry* as: the process of using special equipment to send, receive and measure scientific data over long distances

`Telemetry` in .NET refers to the process of collecting and analyzing data about an application's behavior and performance.

Telemetry signals: 
* `logs`:
* `events`
* `metrics`
* `counters`
* `tracing`

in term of metrics, **metrics are numerical measurements that represent the state or performance of a system over time**.

Types of Metrics:
1. System Metrics: Measure the performance of the underlying infrastructure.
   Examples:
      CPU usage: 75%
      Memory consumption: 2GB
      Disk I/O: 100MB/s
2. Application Metrics: Measure the performance and behavior of the application.
   Examples:
      Request latency: 200ms
      Number of active users: 500
      Error rate: 2%
3. Business Metrics: Measure business-related outcomes.
   Examples:
      Number of orders processed: 1,000
      Revenue generated: $10,000


**Distributed tracing** is a technique that brings structure, correlation and causation to collected telemetry. It defines a special event called `span` and specifies causal relationships between spans.

A `span` describes an operation such as an incoming or outgoing HTTP request, a database call, an expensive I/O call etc. **A span is a unit of tracing**, and to trace more complex operations, we need multiple spans to form a trace

For example, a user may attempt to get an image and send a request to the service. The image is not cached, and the service requests it from the cold storage. To make this operation debuggable, we should report multiple spans:

1. The incoming request

2. The attempt to get the image from the cache

3. Image retrieval from the cold storage

4. Caching the image

**These 4 spans form a trace**. 

A `trace` is a set of related spans fully describing a logical end-to-end operation sharing the same "trace-id"

`instrumentation` refers to the process of adding code or tools to an application to collect telemetry data (such as traces, metrics, and logs) about its behavior and performance. 

**In .NET world, a `span` is represented by an `Activity`, System.Span class is not related to distributed tracing.**



## W3C Trace Context

`traceparent: {version}-{trace-id}-{parent-span-id}-{sampling-state}`

`b3: {trace-id}-{span-id}-{sampling-state}-{parent-span-id}` 
{parent-span-id} is optional for root span OpenTelemetry and .NET ignore {parent-span-id}
Service A ──►  b3: abc123-11111111-1 ──► Service B ──► abc123-22222222-1-11111111 ──► Service C ──► abc123-33333333-1-22222222 ──► Downstream Services



## Activity revisit

```C#
class Program
{
    private static readonly ActivitySource ActivitySource = new ActivitySource("DemoApp.Tracing");
    static void Main(string[] args)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DemoApp.Tracing", // Listen only to our ActivitySource
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => Console.WriteLine($"Activity Started: {activity.DisplayName}"),
            ActivityStopped = activity =>
            {
                Console.WriteLine($"Activity Stopped: {activity.DisplayName}");
                foreach (var tag in activity.Tags)
                {
                    Console.WriteLine($"Tag: {tag.Key} = {tag.Value}");
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        using (Activity parentActivity = ActivitySource.StartActivity("ParentActivity"))   // start a parent activity
        {
            parentActivity.SetTag("rootTag", "This is the root activity.");    // set some additional information on the root activity

            Console.WriteLine($"RA Id: {parentActivity.Id}"); Console.WriteLine($"RA RootId: {parentActivity.RootId}");
            Console.WriteLine($"RA TraceId: {parentActivity.TraceId}"); Console.WriteLine($"RA ParentId: {parentActivity.ParentId}");
            Console.WriteLine($"RA ParentSpanId: {parentActivity.ParentSpanId}"); Console.WriteLine($"RA SpanId: {parentActivity.SpanId}");

            //Activity.Current = parentActivity; // <---------no need to explictly set it, the ActivitySource automatically sets the Activity.Current to the newly created activity

            using (Activity childActivity = ActivitySource.StartActivity("ChildActivity"))  // Additional child activity within the root activity
            {
                childActivity.SetTag("childTag", "This is another child activity.");

                Console.WriteLine($"CA Id: {childActivity.Id}"); Console.WriteLine($"CA RootId: {childActivity.RootId}");
                Console.WriteLine($"CA TraceId: {childActivity.TraceId}"); Console.WriteLine($"CA ParentId: {childActivity.ParentId}");
                Console.WriteLine($"CA ParentSpanId: {childActivity.ParentSpanId}"); Console.WriteLine($"CA SpanId: {childActivity.SpanId}");
            }
        }
    }
    /*
        
    Activity Started: ParentActivity
        RA Id: 00-b62b47e1c2515b0eae286671696abc91-f5daa41dee1c2b7b-01
        RA RootId: b62b47e1c2515b0eae286671696abc91
        RA TraceId: b62b47e1c2515b0eae286671696abc91
        RA ParentId:
        RA ParentSpanId: 0000000000000000
        RA SpanId: f5daa41dee1c2b7b

    Activity Started: ChildActivity
        CA Id: 00-b62b47e1c2515b0eae286671696abc91-b5dc34121e2bc5e1-01
        CA RootId: b62b47e1c2515b0eae286671696abc91
        CA TraceId: b62b47e1c2515b0eae286671696abc91
        CA ParentId: 00-b62b47e1c2515b0eae286671696abc91-f5daa41dee1c2b7b-01
        CA ParentSpanId: f5daa41dee1c2b7b
        CA SpanId: b5dc34121e2bc5e1

    Activity Stopped: ChildActivity
        Tag: childTag = This is another child activity.
    Activity Stopped: ParentActivity
        Tag: rootTag = This is the root activity.
      
    */
}
```
see pact and cact how parentActivity's context passed to childActivity.



## How OpenTelemetry Fits in >NET

                         .net app                                      
┌─────────────────────────────────────────────────────────────────────┐
│                                                                     │
│           HostingApplication creates HttpContext                    │
│                            │                                        │
│                            ▼                                        │
│ HostingApplication calls HostingApplicationDiagnostics.BeginRequest │
│                            │                                        │
│                            ▼                                        │
│          BeginRequest creates and starts an Activity                │
│                            │                                        │
│                            ▼                                        │
│       tracer's sample called to set act's trace flag                │
│                            │                                        │
│                            ▼                                        │
│          tracerListener's ActivityStarted called                    │
│                            │                                        │
│                            ▼                                        │
│           BatchExportProcessor.OnStart called                       │
│                            │                                        │
│                            ▼                                        │
│           HttpInListener.OnStartActivity called                     │
│                            │                                        │
│                            ▼                                        │
│               EnrichWithHttpRequest invoked                         │
│                            │                                        │
│                            ▼                                        │
│  HostingApplication executes middlewares pipeline with HttpContext  │
│                            │                                        │
│                            ▼                                        │
│                   request pipeline ends                             │
│                            │                                        │
│                            ▼                                        │
│  HostingApplication calls HostingApplicationDiagnostics.RequestEnd  │
│                            │                                        │
│                            ▼                                        │
│             HttpInListener.OnStopActivity called                    │   HttpInListener.OnStopActivity called ─► activity stopped ─► tracerListener's ActivityStopped called
│                            │                                        │                                          compared to
│                            ▼                                        │   activity started ─► tracerListener's ActivityStarted called ─► HttpInListener.OnStartActivity called
│             EnrichWithHttpResponse invoked                          │
│                            │                                        │
│                            ▼                                        │
│     act stopped by HostingApplicationDiagnostics.RequestEnd         │   
│                            │                                        │
│                            ▼                                        │
│          tracerListener's ActivityStopped called                    │   
│                            │                                        │
│                            ▼                                        │
│            BatchExportProcessor.OnEnd called                        │
│                            │                                        │
│                            ▼                                        │
│ ┌─────── OtlpTraceExporter.Export(activityBatch)                    │
│ │                          Or                                       │
│ │ ┌────────JaegerExporter.Export(activityBatch)                     │
│ │ │                                                                 │
└─┼─┼─────────────────────────────────────────────────────────────────┘
  │ │                                                                  
  │ │                OpenTelemetry Collector Pipeline                                                                                   
  │ │                                                                  
  │ │    Receivers               Processors               Exporters    
  │ │ ┌────────────────┐       ┌─────────────┐        ┌───────────────┐
  │ │ │ ┌──────┐       │       │ ┌─────────┐ │        │   ┌──────┐    │
  └─┼─┼►│ OLTP │       │       │ │Filtering│ │   ┌───►│   │ OLTP │    │
    │ │ └──────┘       │       │ └────┬────┘ │   │    │   └──────┘    │
    │ │                │       │      │      │   │    │               │
    │ │ ┌───────┐      │       │ ┌────▼────┐ │   │    │   ┌──────┐    │
    └─┼►│ Jaeger│      ├──────►│ │Redaction│ ├───┼───►│   │Jaeger│    │
      │ └───────┘      │       │ └────┬────┘ │   │    │   └──────┘    │
      │                │       │      │      │   │    │               │
      │ ┌────────────┐ │       │ ┌────▼────┐ │   │    │ ┌───────────┐ │
      │ │ Prometheus │ │       │ │Batching │ │   └───►│ │ Promethus │ │
      │ └────────────┘ │       │ └─────────┘ │        │ └───────────┘ │
      │    ...         │       │    ...      │        │    ...        │
      └────────────────┘       └─────────────┘        └───────────────┘


```yml
# otel-collector-config.yml for OpenTelemetry Collector
receivers:
  docker_stats:
    api_version: 1.41
    env_vars_to_metric_labels:
      OTEL_SERVICE_NAME: service.name
  otlp:
    protocols:
      http:
      grpc:

exporters:
  jaeger:
    endpoint: jaeger:14250
    tls:
      insecure: true
  prometheus:
    endpoint: "0.0.0.0:8889"
    resource_to_telemetry_conversion:
      enabled: true
  logging:
    verbosity: detailed

processors:
  batch:
  resourcedetection/docker:
    detectors: [env, docker]
  tail_sampling:
    decision_wait: 2s
    expected_new_traces_per_sec: 500
    policies:
      [
        {
          name: limit-rate,
          type: rate_limiting,
          rate_limiting: {spans_per_second: 50}
        }
      ]
service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [tail_sampling, batch, resourcedetection/docker]
      exporters: [jaeger]
    metrics:
      receivers: [docker_stats, otlp]
      processors: [batch, resourcedetection/docker]
      exporters: [prometheus]
    logs:
      receivers: [otlp]
      processors: [batch, resourcedetection/docker]
      exporters: [logging]
```

There are 4 combinations between exporter and collector:

1. `AddOtlpExporter`, otel collector NOT involved

says you don't use OpenTelemetry Collector, you can do

```C#
.AddOtlpExporter(options => 
    options.Endpoint = new Uri("http://localhost:4317")
```

```yml
## docker-compose file
jaeger:
  image: jaegertracing/all-in-one:1.58.0
  ports:
    - "16686:16686" # Jaeger Web UI
    - "4317:4317" # OTLP format consumed by jaeger's collector <--------------------------
```

because jaeger can also OTEL messages then converts them to Jaeger compliant messages


2. `AddOtlpExporter`, otel collector involved

```C#
.AddOtlpExporter(options => 
    options.Endpoint = new Uri("http://localhost:4317"))  // <----------------4317 remains the same
```

messages consumed by otel collector's gRPC receiver on port 4317 (otel collector running as docker instance with otel-collector-config.yml)
otel collector sets exporter to be jaeger endpoint on port 14250:

```yml
## otel-collector-config.yml
receivers:
  otlp:
    protocols:
      grpc: # Jaeger gRPC receiver, listens on port 14250 by default

exporters:
  jaeger:
    endpoint: jaeger:14250
```

3. `AddJaegerExporter`, otel collector involved

```C#
.AddJaegerExporter(opts =>   
    opts.Endpoint = new Uri("http://localhost:14250/api/traces"));         
```
messages consumed by otel collector's Jaeger gRPC receiver on port 14250 (otel collector running as docker instance with otel-collector-config.yml)

```yml
## otel-collector-config.yml
receivers:
  jaeger:
    protocols:
      grpc:  # Jaeger gRPC receiver, listens on port 14250 by default

exporters:
  jaeger:
    endpoint: jaeger:14250  # Export to Jaeger collector's gRPC endpoint
```

4. `AddJaegerExporter`, otel collector NOT involved

```C#
.AddJaegerExporter(opts =>
    opts.Endpoint = new Uri("http://jaeger.mydomain.local:14250")); // bypass OpenTelemetry Collector, send traces to Jaeger's gRPC endpoint directly
```


Let's revisit how asp.net create an activity for incoming request:

`HostingApplication` creates `HttpContext`  
                    🠋
calls `HostingApplicationDiagnostics.BeginRequest(httpContext, ...)`
                    🠋 does two things in order
a. `_activitySource(of "Microsoft.AspNetCore").CreateActivity("Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server, context)` **which calls `TracerProviderSdk.listener`'s Sampler** (check important note below) ─►  `TracerProviderSdk.listener`'s delegate `ActivityListener.ActivityStarted` is invoked ─► `compositeProcessor?.OnStart(activity)` (normally `OnStart` does nothing)

b. `diagnosticSource(of "Microsoft.AspNetCore").Write("Microsoft.AspNetCore.Hosting.BeginRequest", new DeprecatedRequestData(httpContext, startTimestamp))`
  ─►  `HttpInListener.OnEventWritten(OnStartEvent)` (mainly to enrich the activity created above by calling multiple activity.SetTag(...))
                    🠋

`HostingApplication` calls Middlewares wrapped `RequestDelegate` ─► calls `HostingApplication.DisposeContext()` is called after Http request ends,  which calls  `HostingApplicationDiagnostics.RequestEnd(httpContext, ...)` 
                    🠋

a. `diagnosticSource(of "Microsoft.AspNetCore").Write("Microsoft.AspNetCore.Hosting.EndRequest", new DeprecatedRequestData(httpContext, currentTimestamp))`
 ─► `HttpInListener.OnEventWritten(OnStopActivity)` (mainly to enrich the activity created above by calling multiple tags e.g `activity.SetTag("http.response.status_code", "200")`)

b. `HostingApplicationDiagnostics.StopActivity(httpContext,  activity, ...)` is called to stop the current activity
   ─►  `TracerProviderSdk.listener`'s delegate `ActivityListener.ActivityStopped` is invoked ─► `compositeProcessor?.OnEnd(activity)` (normally `BatchExportProcessor` is involved to batch each single activity into a batch)
   ─► `OtlpTraceExporter` or `JaegerExporter` receives activityBatch and do a for loop to transform each activity to Otlp-format protobuf/Jaeger-format span

check samd to see how `activity.IsAllDataRequested` and `activity.ActivityTraceFlags` being set when sampling result is `SamplingDecision.Drop`, `SamplingDecision.RecordOnly` and `SamplingDecision.RecordAndSample`

also note a funny fact, even a HTTP request's trace-flags (sampling bit) is passed, the downstream service will still run the sampler, it is there when sampling bit is checked, the process is different to what you might think that why not just don't run sampler at all when it is sampled out, but asp.net wants to give you granular control on overall process so it still run sampler which you can add different logic there

**it is important to note: `TracerProviderSdk.listener.Sample` is called inside `ActivitySource.CreateActivity()`**, the `SamplingDecision` will be mapped to e.g. `ActivitySamplingResult` so that when `Activity.Create(..., ActivitySamplingResult samplingResult, ...)` is called (inside `ActivitySource.CreateActivity()`), the samplingResult will be used to initialize the newly create Activity's `IsAllDataRequested` and  `ActivityTraceFlags`, so that when the Activity is started, `TracerProviderSdk.listener.ActivityStarted()` runs first, when sends Activity to `processor.OnStart(activity)` (note that processor's OnStart normally does nothing), which means that process firstly received an unenriched activity (see procenrih), then `HttpInListener.OnStartActivity()` runs, again `Activity.IsAllDataRequested` can influence its `EnrichWithHttpRequest`

let's compare it with the stop of an activity, you will see why it has to be like this order and why stopping an activity is different than the starting an activity

```C#
internal sealed class HostingApplicationDiagnostics
{
    // ...
    public void BeginRequest(HttpContext httpContext, HostingApplication.Context context) 
    {
        if (loggingEnabled || diagnosticListenerActivityCreationEnabled || _activitySource.HasListeners())
        {
            context.Activity = StartActivity(httpContext, loggingEnabled, diagnosticListenerActivityCreationEnabled, out var hasDiagnosticListener);  
            // ...
        }
 
        if (diagnosticListenerEnabled)
        {
            if (_diagnosticListener.IsEnabled(DeprecatedDiagnosticsBeginRequestKey)) 
            {
                // ...
                RecordBeginRequestDiagnostics(httpContext, startTimestamp);  // trigger HttpInListener.OnStartActivity()
            }
        }
    }

    public void RequestEnd(HttpContext httpContext, Exception? exception, HostingApplication.Context context)
    {
        // ...
        if (_diagnosticListener.IsEnabled())
        {
             RecordEndRequestDiagnostics(httpContext, currentTimestamp);  // trigger HttpInListener.OnStopActivity()
        }

         var activity = context.Activity;
        // Always stop activity if it was started
        if (activity is not null)
        {
            StopActivity(httpContext, activity, context.HasDiagnosticListener);
        }
        // ...
    }
}
```
you can see that for BeginRequest, `StartActivity` calls before `RecordBeginRequestDiagnostics`, while for RequestEnd, the order is different, `RecordEndRequestDiagnostics` get called before `StopActivity`, that why the process.OnStart received an unenriched acitivity while process.OnEnd will received an full enriched acitivity.


you might ask why we need `HttpInListener` when we already can use `TracerProviderSdk.listener` can react when activity is started? The reason is `TracerProviderSdk.listener` "receives" Activity instance, while `HttpInListener` can recieve `HttpContext` (it access current activity using `Activity.Current`), you can combine Activity with HttpContext to further enrich the activity, for example, don't enrich activity is the http request is a static file request



## Sampling

* `Head-based` Sampling

The creation of `Activity` depends on the sampler's rule, keep or drop a trace is made at the very beginning of the trace

* Tail-based Sampling

You don't use any sampler in .net project and export all traces to OpenTelemetry Collector and configure sampling there.

So unless state explicitly, we are talking about Head-based Sampling.

`Consistent sampling` means that all spans in a trace are either all sampled in or all sampled out—there are no "partial traces" where only some spans are recorded. For example `ParentBasedSampler`  achieve consistent sampling as it use trace id for sampling decision, so all child spans in downstream services will have the same sampling result because same traice id is passed to all downstream services. `ParentBasedSampler` check the parent's activity context's trace-flags `-00` (sampled out) or `-01` (sampled in)


let's walkthroug how ParentBasedSampler works in details:

```C#
void ConfigureParentBasedSampler(WebApplicationBuilder builder)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tp => tp
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.2)))  // AlwaysOffSampler is the root Sampler
            .AddOtlpExporter());
}
```
0. Our WebApp (`w1`) starts, the request to /index is processed by asp.net, the w1 uses HttpClient to call a downstream microservice (`d1`)
1.  `DiagnosticsHandler.SendAsyncCore` calls `s_activitySource.StartActivity("System.Net.Http..HttpRequestOut", ActivityKind.Client);` which creates `new ActivityCreationOptions<ActivityContext>` based on `Activity.Current`, then the Sample delegate of the activityListener created in `TracerProviderSdk` will be called with the ActivityCreationOptions instance (contains trace id), so `ParentBasedSampler.ShouldSample(new SamplingParameters(cctivityCreationOptions.Parent, options.TraceId, ...))` will be called
via `TracerProviderSdk.ComputeActivitySamplingResult`. Only after Sampler's ShouldSample being called, an Activity instance (`actw1`, `new Activity("System.Net.Http.HttpRequestOut")`) is created depending on the SamplingResult (note that **Sampler's ShouldSample() is called before Activity instance creation**), if it is:

a. `SamplingDecision.Drop` can maps to:
   `ActivitySamplingResult.None`: no Activity/span will be created (unless it is root span or remote span)
   `ActivitySamplingResult.PropagationData`: for root span or remote span, only collect TraceId and SpanId, NOT tags, baggage etc
b. `SamplingDecision.RecordOnly` maps to `ActivitySamplingResult.AllData`: tags, baggage will be collected, NOT marked as sampled
c. `SamplingDecision.RecordAndSample` maps to `ActivitySamplingResult.AllDataAndRecorded`: above plus marked as sampled, will be exported to tracing backend

2. says the `actw1` is sampled in, in `d1`, `HostingApplicationDiagnostics` is determining whether to create an Activity instance, note that the decision is delegated to `ActivitySource.CreateActivity()` who will use Sampler like `ParentBasedSampler` to check the trace flags of parent Activity, since parent activity is sampled in and reflected by the 
trace flags (ptf), so this soon-to-be child activity span will be created with "sampled-in" trace flag `-01` so that next downstream service can honour the the sampled in decision on and on again

```C#
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
        this.localParentSampled = new AlwaysOnSampler();  // <----------------------------
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
      
        // <---------------------check soon-to-be created activity's sampled bit, check samd to see further action on the SamplingResult
        if ((parentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0)  // <--------------------------- ptf, when parent is sampled
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
```

if you see `TraceIdRatioBasedSampler`usage like:

```C#
void ConfigureProbabilitySampler(WebApplicationBuilder builder)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tp => tp
            .SetSampler(new TraceIdRatioBasedSampler(0.2))
            .AddOtlpExporter());
}
```

you might ask how can child activity and parent activity be consistent, how can it prevent parent activity sampled in but child activity sampled out since the ratio means random, but this won't happen because `TraceIdRatioBasedSampler` purely use trace id to calculate the probability, since trace id is passed from parent activity to child activity, so the decisions alwasy match, i.e if parent activity is sampled in then child activities are also sampled in. So the only thing decide the final sampling decision is the trace id genereated by the parent activiy in the first place.


## Resource

In OpenTelemetry .NET, a **Resource** is a set of key-value attributes that describe the entity for which telemetry is collected. Resources provide context about the application or service, such as its name, version, environment, and other identifying information.

### Key Points

- **Resource** is not attached to individual spans/activities, but to the telemetry pipeline as a whole.
- It describes the service (e.g., `service.name`, `service.version`, `service.namespace`), environment (e.g., `env`), and other metadata.
- Resources are configured via `ResourceBuilder` when setting up OpenTelemetry:
- Resources are merged from multiple sources (environment variables, code, detectors).
- Exporters (like Jaeger, OTLP) use resource attributes to annotate telemetry data, making it easier to filter and analyze in observability backends.

**Example resource attributes:**
- `service.name`: Name of the service (e.g., "myService")
- `service.version`: Version of the service
- `env`: Environment (e.g., "Production")
- `telemetry.sdk.name`: "opentelemetry"
- `service.instance.id`: Unique instance identifier

**Summary:**  
A Resource in OpenTelemetry .NET is metadata about the service or process emitting telemetry, used to provide context for traces, metrics, and logs.

```C#
static void ConfigureTelemetry(WebApplicationBuilder builder)
{
    var env = new KeyValuePair<string, object>("env", builder.Environment.EnvironmentName);
    var resourceBuilder = ResourceBuilder.CreateDefault()
        //.AddService("frontend", serviceNamespace: "memes", serviceVersion: "1.0.0") <--------this overides OTEL_SERVICE_NAME in appsettings.json
        .AddAttributes(new []
        {
            new KeyValuePair<string, object>("service.version","1.0.0")   // <---------------AddService is just wrapper to call multiple AddAttributes
        })                                                                
        .AddAttributes(new[] { env });      
    var samplingProbability = builder.Configuration.GetSection("Sampling").GetValue<double>("Probability");

    builder.Services.AddOpenTelemetry()
        .WithTracing(builder => builder
            .SetSampler(new TraceIdRatioBasedSampler(samplingProbability))
            .AddProcessor<MemeNameEnrichingProcessor>()
            .SetResourceBuilder(resourceBuilder)
        // ...      
       
}
```

```C#
//-------------------V
public class Resource
{
    public Resource(IEnumerable<KeyValuePair<string, object>> attributes)  { ... }
    public IEnumerable<KeyValuePair<string, object>> Attributes { get; }

    public Resource Merge(Resource other) { ... }
}
//-------------------Ʌ

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

//-------------------------------------------V
public static class ResourceBuilderExtensions
{
    // ...
    public static ResourceBuilder AddAttributes(this ResourceBuilder resourceBuilder, IEnumerable<KeyValuePair<string, object>> attributes)  // <---------------
    {
        return resourceBuilder.AddResource(new Resource(attributes)); 
    }
}
//-------------------------------------------Ʌ
```

```json
// appsettings.json
{
    "OTEL_SERVICE_NAME": "myService"  // will be overridden by ResourceBuilder.AddService("frontend", ...)
}
```

```yml
Activity.TraceId:            dbb7d6e94349fbc688e958bf5390b00c
Activity.SpanId:             528d47cc5e76079e
Activity.TraceFlags:         Recorded
Activity.DisplayName:        /
Activity.Kind:               Server
Activity.StartTime:          2025-06-08T12:56:09.5305830Z
Activity.Duration:           00:00:00.3249234
Activity.Tags:
    net.host.name: localhost
    net.host.port: 5051
    http.method: GET
    http.scheme: http
    http.target: /
    http.url: http://localhost:5051/
    http.flavor: 1.1
    http.user_agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36
    http.status_code: 200
Instrumentation scope (ActivitySource):
    Name: Microsoft.AspNetCore
Resource associated with Activity:
    env: Production             ## <-------------------------------------
    service.name: myService     ## <-------------------------------------
    service.namespace: memes
    service.version: 1.0.0
    service.instance.id: 0fb32038-bf96-46ce-8742-2a8dda0c3738
    ## ------------------------------V from rb1
    telemetry.sdk.name: opentelemetry
    telemetry.sdk.language: dotnet
    telemetry.sdk.version: 1.12.0
    ##-------------------------------Ʌ

Activity.TraceId:            dbb7d6e94349fbc688e958bf5390b00c
Activity.SpanId:             528d47cc5e76079e
Activity.TraceFlags:         Recorded
Activity.DisplayName:        /
Activity.Kind:               Server
Activity.StartTime:          2025-06-08T12:56:09.5305830Z
Activity.Duration:           00:00:00.3249234
Activity.Tags:
    net.host.name: localhost
    net.host.port: 5051
    http.method: GET
    http.scheme: http
    http.target: /
    http.url: http://localhost:5051/
    http.flavor: 1.1
    http.user_agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36
    http.status_code: 200
Instrumentation scope (ActivitySource):
    Name: Microsoft.AspNetCore
Resource associated with Activity:
    env: Production             ## <-------------------------------------
    service.name: myService     ## <-------------------------------------
    service.namespace: memes
    service.version: 1.0.0
    service.instance.id: 0fb32038-bf96-46ce-8742-2a8dda0c3738
    telemetry.sdk.name: opentelemetry
    telemetry.sdk.language: dotnet
    telemetry.sdk.version: 1.12.0
```

note that Activity doesn't contain any "resource related" tags, only exporter takes in Resource and show them in Console or APM backend, check rb4


## Processor

Sometimes you want to drop some activities e.g  those that represent retrieving static files, there are two options

Option1: Dropping them in the processor

```C#
Builder.Services.AddOpenTelemetry()
    .WithTracing(builder => builder
    .AddProcessor<StaticFilesFilteringProcessor>()  // <--------------execute first
    .AddProcessor<XXXProcessor>()                   // <--------------execute later, all the processors are wrapped in CompositeProcessor
    // ...
);

public class StaticFilesFilteringProcessor : BaseProcessor<Activity> 
{   
    public override void OnEnd(Activity activity)
    {
        if (activity.Kind == ActivityKind.Server && activity.GetTagItem("http.method") as string == "GET" && activity.GetTagItem("http.route") == null)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;   // make BatchActivityExportProcessor.OnEnd() "drop" the activity
        }
    }

    /* you cannot put the code in OnStart, because when activity is started, the request pipeline hasn't started yet, so you have to wail until
       request pipeline finsihed, which is OnEnd to be able to access activity.GetTagItem("http.route")
    public override void OnStart(Activity activity)
    { 
        if (activity.Kind == ActivityKind.Server && activity.GetTagItem("http.method") as string == "GET" && activity.GetTagItem("http.route") == null)
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
    */
}
// to see why use activity.GetTagItem("http.route") == null, check stf

public class BatchActivityExportProcessor : BatchExportProcessor<Activity>  // normally it is the last processor
{
    public BatchActivityExportProcessor(BaseExporter<Activity> exporter, ...) : base(exporter, ...) { }

    public override void OnEnd(Activity data)
    {
        if (!data.Recorded)  
        {
            return;
        }

        this.OnExport(data);  
    }
}
```

Option2: Prevent Activity being record by setting ` activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded` in the first place rather than in custom's processor via `AspNetCoreInstrumentationOptions.Filter`

```C#
Builder.Services.AddOpenTelemetry()
    .WithTracing(builder => builder
    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsStaticFile(ctx.Request.Path))  // <--------see ofl, 
    // ...
);

static bool IsStaticFile(PathString requestPath) => requestPath.HasValue && (requestPath.Value.EndsWith(".js") ||  requestPath.Value.EndsWith(".css"));
```


































===========================================================================================

```C#
//------------------V
class Program
{
    private static ActivitySource source = new ActivitySource("Sample.DistributedTracing", "1.0.0");

    static async Task Main(string[] args)
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MySample"))
            .AddSource("Sample.DistributedTracing")  // this has to be the same as the the name pass in the new ActivitySource(string name) above
            .AddConsoleExporter()
            .Build();  // create and return a TracerProviderSdk instance which register an ActivityListener by calling ActivitySource.AddActivityListener

        await DoSomeWork("banana", 8);
        Console.WriteLine("Example work done");

        Console.ReadLine();
    }

    static async Task DoSomeWork(string foo, int bar)
    {
        // In .NET world, a span is represented by an Activity
        using (Activity activity = source.StartActivity("SomeWork"))  // StartActivity might return null if there is no registered ActivityListener
        {
            activity?.SetTag("foo", foo);
            activity?.SetTag("bar", bar);
            await StepOne();
            activity?.AddEvent(new ActivityEvent("Part way there"));
            await StepTwo();
            activity?.AddEvent(new ActivityEvent("Done now"));

            // Pretend something went wrong
            activity?.SetTag("otel.status_code", "ERROR");
            activity?.SetTag("otel.status_description", "Use this text give more information about the error");
        }  // Activity implements IDisposable, so you don't need to call activity.Stop(), `using ()` will automatically close it for you
    }

    static async Task StepOne()
    {
        using (Activity activity = source.StartActivity("StepOne"))
        {
            await Task.Delay(500);
            await InnerOne();  // <----------------------create a child span
        }
    }

    static async Task StepTwo()
    {
        using (Activity activity = source.StartActivity("StepTwo"))
        {
            await Task.Delay(1000);
        }

    }

    static async Task InnerOne()
    {
        using (Activity activity = source.StartActivity("StepOneInner"))
        {
            await Task.Delay(1000);
        }

    }
}
//------------------Ʌ 
```

```yml
Activity.Id:          00-82cf6ea92661b84d9fd881731741d04e-33fff2835a03c041-01
Activity.DisplayName: SomeWork
Activity.Kind:        Internal
Activity.StartTime:   2021-03-18T10:39:10.6902609Z
Activity.Duration:    00:00:01.5147582
Activity.TagObjects:
    foo: banana
    bar: 8
Activity.Events:
    Part way there [3/18/2021 10:39:11 AM +00:00]
    Done now [3/18/2021 10:39:12 AM +00:00]
Resource associated with Activity:
    service.name: MySample
    service.instance.id: ea7f0fcb-3673-48e0-b6ce-e4af5a86ce4f

Example work done

DoSomeWork

Id       00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-72dc8c0dc24fc406-01  ## leading 00 is the version, and 01 is the trace flag, meaning "sampled" (tailing 00 mean not sampled), 
TraceId     bbd8c75b04cda7f7fe9ee03b54b2d7ba                      ## TraceId is 16 bytes in hex-encoded, so it has 32 characters
SpanId                                       72dc8c0dc24fc406
ParentId     null
ParentSpanId 0000000000000000


StepOne

Id         00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-51de8ae5367e022e-01
TraceId       bbd8c75b04cda7f7fe9ee03b54b2d7ba
SpanId                                         51de8ae5367e022e
ParentId    00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-72dc8c0dc24fc406-01
ParentSpanId 72dc8c0dc24fc406


InnerOne

Id         00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-0ff550a48811adfa-01
TraceId       bbd8c75b04cda7f7fe9ee03b54b2d7ba
SpanId                                         0ff550a48811adfa
ParentId    00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-51de8ae5367e022e-01
ParentSpanId 51de8ae5367e022e


StepTwo
Id         00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-750752b828701ec1-01
TraceId       bbd8c75b04cda7f7fe9ee03b54b2d7ba
SpanId                                         750752b828701ec1
ParentId    00-bbd8c75b04cda7f7fe9ee03b54b2d7ba-72dc8c0dc24fc406-01
ParentSpanId 72dc8c0dc24fc406
```

======================================================================================================


