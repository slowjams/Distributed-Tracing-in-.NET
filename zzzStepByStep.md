## Step By Step Analysis

serviceA `frontend`, a Razor Page app:
```yaml
info: Microsoft.AspNetCore.Hosting.Diagnostics[1]   # <------------------dlr, dlr0 in GenericWebHostService shows why it is "Microsoft.AspNetCore.Hosting.Diagnostics" 
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme 
         # <-------------SpanId, TraceId, ParentId is from scolog
      RequestId:0HNC9SN7ISL8Q:00000002
      Request starting HTTP/1.1 GET http://localhost:5051/meme?name=dotnet # <------------------------- see rshm, called from HostingApplicationDiagnostics
info: System.Net.Http.HttpClient.storage.LogicalHandler[100]   # <-----------------------------------------loh
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme RequestId:0HNC9SN7ISL8Q:00000002 => /Meme => HTTP GET http://localhost:5050/memes/dotnet 
                                                 # <-------------------------lsch1.1, this scope is because of LoggingScopeHttpMessageHandler,
                                                 # LoggingScopeHttpMessageHandler creates a scope for HTTP {HttpMethod} {Uri},  same as below
      Start processing HTTP request GET http://localhost:5050/memes/dotnet  # <---------------lsch2.3, still from LoggingScopeHttpMessageHandler, no scoped involved
                                                                   
info: System.Net.Http.HttpClient.storage.ClientHandler[100]     # <----------------------------------------clh
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme RequestId:0HNC9SN7ISL8Q:00000002 => /Meme => HTTP GET http://localhost:5050/memes/dotnet
      Sending HTTP request GET http://localhost:5050/memes/dotnet
info: System.Net.Http.HttpClient.storage.ClientHandler[101]
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme RequestId:0HNC9SN7ISL8Q:00000002 => /Meme => HTTP GET http://localhost:5050/memes/dotnet
      Received HTTP response headers after 299.1532ms - 200
info: System.Net.Http.HttpClient.storage.LogicalHandler[101]
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme RequestId:0HNC9SN7ISL8Q:00000002 => /Meme => HTTP GET http://localhost:5050/memes/dotnet
      End processing HTTP request after 1398.4441ms - 200
info: Microsoft.AspNetCore.Hosting.Diagnostics[2]
      => SpanId:5b021b6713680cad, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:0000000000000000 => ConnectionId:0HNC9SN7ISL8Q => RequestPath:/meme RequestId:0HNC9SN7ISL8Q:00000002
      Request finished HTTP/1.1 GET http://localhost:5051/meme?name=dotnet - 200 - text/html;+charset=utf-8 4046.3552ms  # <---- see rshm, called from HostingApplicationDiagnostics
# to see why scope is represented as "=>", refer to scobol
```

serviceB (`storage`, Web Api):
```yml
info: Microsoft.AspNetCore.Hosting.Diagnostics[1]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001
      Request starting HTTP/1.1 GET http://localhost:5050/memes/dotnet - - -
info: Microsoft.AspNetCore.Routing.EndpointMiddleware[0]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001
      Executing endpoint 'storage.Controllers.MemesController.Get (storage)'
info: Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker[102]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      Route matched with {action = "Get", controller = "Memes"}. Executing controller action with signature System.Threading.Tasks.Task`1[Microsoft.AspNetCore.Mvc.ActionResult] Get(System.String, System.Threading.CancellationToken) on controller storage.Controllers.MemesController (storage).
info: Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker[101]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      Executing action method storage.Controllers.MemesController.Get (storage) - Validation state is Valid
info: storage.Controllers.MemesController[0]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      This is it------------------
info: storage.Controllers.MemesController[0]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      Returning 'dotnet', 1516 bytes
info: Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker[103]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      Executed action method storage.Controllers.MemesController.Get (storage), returned result Microsoft.AspNetCore.Mvc.FileStreamResult in 107.2915ms.
info: Microsoft.AspNetCore.Mvc.Infrastructure.FileStreamResultExecutor[1]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001 => storage.Controllers.MemesController.Get (storage)
      Executing FileStreamResult, sending file with download name '' ...
info: Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker[105]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001
      Executed action storage.Controllers.MemesController.Get (storage) in 140.6465ms
info: Microsoft.AspNetCore.Routing.EndpointMiddleware[1]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001
      Executed endpoint 'storage.Controllers.MemesController.Get (storage)'
info: Microsoft.AspNetCore.Hosting.Diagnostics[2]
      => SpanId:78e407f51842e1ee, TraceId:59712acd268371d7c43bf11d87a3b1b1, ParentId:30ecfaffa66799af => ConnectionId:0HNCH8ULNKBL6 => RequestPath:/memes/dotnet RequestId:0HNCH8ULNKBL6:00000001
      Request finished HTTP/1.1 GET http://localhost:5050/memes/dotnet - 200 1516 image/png 196.9484ms
```

There are a couple of things to notices from above logging output:

1. The activiy with SpanId:a06b850952fdd956 is logged all over the place, this activity is created at dlr3.2, that is why ParentId is null because it is the first request in the pipeline

2. There is no logging passed in `HttpClient`, so you won't see any log regarding `HttpClient` (`LogicalHandler` and `ClientHandler` are `DelegatingHandler` that still logs SpanId, TraceID from the activity at dlr3.2 ). Then you can see the SpanId, TraceID from the activity that created in `HttpClient` via its `DiagnosticsHandler`? The answer is `HttpClientObserver` in the source code

3.  serviceB's Span Id "30ecfaffa66799af" is not showing anytyhere in serviceA's logging output despite the TraceId does match, the reason is explained above, if you use `HttpClientObserver` to write the activiy on the console, you will see that's the activity with  Span Id "30ecfaffa66799af" created by `HttpClient` via its `DiagnosticsHandler` in serviceA is the parent for the activity in serviceB.


`LoggingScopeHttpMessageHandler.SendAsync()` (`LogicalHandler`) ─► `LoggingHttpMessageHandler.SendAsync()` (`ClientHandler`) ─► `SocketsHttpHandler.SendAsync()` starts ─► `MetricsHandler.SendAsync()` ─► `DiagnosticsHandler.SendAsync()` ─► `HttpConnectionHandler.SendAsync()` ─► `HttpConnectionPoolManager.SendAsync()` ─► `HttpConnectionPool.SendAsync()` ─► `Http2Connection.SendAsync()` ─► `SocketsHttpHandler.SendAsync()` ends


To summarize with the project `SimpleMicroservicesVerifyCausation` to see how a "pair" of Activity instances being created:

There are two Web API microservices serviceA (`frontend`, Razor Page app) and serviceB (`storage`, Web Api), serviceA (runs on `http://localhost:5051/`), when users type in `http://localhost:5051/meme?name=dotnet`, serviceA uses `HttpClient` to calls serviceB's endpoint `http://localhost:5050`

let's talk about the moment serviceA is about to send a HTTP request to serviceB

1. serviceA is actively listening on request, and we type `http://localhost:5051/meme?name=dotnet` in browser

serviceA receives the HTTP request, `GenericWebHostService`'s `HostingApplication` creates an `HttpContext` (which contains the traceparent header, but it's traceparent is empty since this request is the first origin request ) ─► `HostingApplicationDiagnostics` calls `DistributedContextPropagator.ExtractTraceIdAndState()` (nothing to extract) on `HttpContext`'s headers and then creates `new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn")` (let's call it `activityA1`) with brand new traceid  (refer to dlr3.2) 

serviceA uses `HttpClient` to send request on `http://localhost:5050/memes/dotnet`, `HttpClient` uses `DiagnosticsHandler` to create `new Activity("System.Net.Http.HttpRequestOut")` (let's call it `activityA2`), **`activityA2`'s parent is set to be `activityA1`**, (refer to dcp). Then `DiagnosticsHandler` calls `DistributedContextPropagator.Inject()` with this Activity instance to set trace id in the request header.. Note that  `activityA1` and `activityA2` share same traceId, but they have different span id, only `activityA2` will be the parents of downstream services (i.e `activityA2`'s spanId will be passed to downstream services)


2. serviceB receives request (`http://localhost:5050/memes/dotnet`)

When  serviceB receives the HTTP request, `GenericWebHostService`'s `HostingApplication` creates an `HttpContext` (which contains the traceparent header) ─► `HostingApplicationDiagnostics` calls `DistributedContextPropagator.ExtractTraceIdAndState()` on `HttpContext`'s headers to extract the trace id and then
 creates `new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn")` (let's call it `activityB1`), `activityB1`'s parent is set to be `activityA2` (refer to dlr) 


=============================================================================================


## Source Code `SimpleMicroservicesVerifyCausation` 

```C#
class Subscriber : IObserver<DiagnosticListener>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void OnNext(DiagnosticListener listener)  // Microsoft.Extensions.Hosting, Microsoft.AspNetCore, HttpHandlerDiagnosticListener
    {
        if (listener.Name == "Microsoft.AspNetCore")
        {
            listener.Subscribe(new HostingApplicationDiagnosticsObserver()!);
        }

        if (listener.Name == "HttpHandlerDiagnosticListener")
        {
            listener.Subscribe(new HttpClientObserver()!);
        }
    }
}

class HttpClientObserver : IObserver<KeyValuePair<string, object>>
{
    Stopwatch _stopwatch = new Stopwatch();

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void OnNext(KeyValuePair<string, object> receivedEvent)
    {
        switch (receivedEvent.Key)
        {
            case "System.Net.Http.HttpRequestOut.Start":
                _stopwatch.Start();

                if (receivedEvent.Value.GetType().GetTypeInfo().GetDeclaredProperty("Request")
                    ?.GetValue(receivedEvent.Value) is HttpRequestMessage requestMessage)
                {
                    Console.WriteLine($"HTTP Request start: {requestMessage.Method} -" +
                        $" {requestMessage.RequestUri} - activity id: {Activity.Current.Id}, parentactivity Id: {Activity.Current.ParentId}");
                }

                break;
            case "System.Net.Http.HttpRequestOut.Stop":
                _stopwatch.Stop();

                if (receivedEvent.Value.GetType().GetTypeInfo().GetDeclaredProperty("Response")
                    ?.GetValue(receivedEvent.Value) is HttpResponseMessage responseMessage)
                {
                    Console.WriteLine($"HTTP Request finished: took " +
                        $"{_stopwatch.ElapsedMilliseconds}ms, status code:" +
                            $" {responseMessage.StatusCode} - activity id: {Activity.Current.Id}, parentactivity Id: {Activity.Current.ParentId}");
                }

                break;
        }
    }
}


class HostingApplicationDiagnosticsObserver : IObserver<KeyValuePair<string, object>>
{
    Stopwatch _stopwatch = new Stopwatch();

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void OnNext(KeyValuePair<string, object> receivedEvent)  // Microsoft.AspNetCore.Hosting.HttpRequestIn.Start, Microsoft.AspNetCore.Hosting.BeginRequest
    {
        switch (receivedEvent.Key)
        {
            case "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start":
                Console.WriteLine($"Activityy is: - activity id: {Activity.Current.Id}, parentactivity Id: {Activity.Current.ParentId}");

                break;

            case "Microsoft.AspNetCore.Hosting.BeginRequest":
                Console.WriteLine($"Activityy is: - activity id: {Activity.Current.Id}, parentactivity Id: {Activity.Current.ParentId}");

                break;
            // ...
        }
    }
}
```
