using frontend;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Reflection;

DiagnosticListener.AllListeners.Subscribe(new Subscriber());

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

var storageConfig = builder.Configuration.GetSection("Storage");
var storageEndpoint = storageConfig?.GetValue<string>("Endpoint") ?? "http://localhost:5050";
builder.Services.AddHttpClient("storage", httpClient =>
{
    httpClient.BaseAddress = new Uri(storageEndpoint);
});

builder.Services.AddSingleton<StorageService>();

ConfigureTelemetry(builder);

var app = builder.Build();
app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.UseStatusCodePagesWithRedirects("/errors/{0}");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// return context to the caller with W3C traceresponse (draft specification)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Add("traceresponse", Activity.Current?.Id);
    await next.Invoke();
});


app.UseStaticFiles();

app.UseRouting();

app.MapRazorPages();

app.Run();

static void ConfigureTelemetry(WebApplicationBuilder builder)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracerProviderBuilder => tracerProviderBuilder
            .AddJaegerExporter()
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation())
        .WithMetrics(meterProviderBuilder => meterProviderBuilder
            .AddPrometheusExporter()
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddProcessInstrumentation()
            .AddRuntimeInstrumentation());
}

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

// you need to send a new request (refresh the page) and app is started, do not just run the application and debug
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