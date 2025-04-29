## Source Code


```C#
//--------------------------------------V
internal sealed class DiagnosticsHandler : HttpMessageHandlerStage
{
    private static readonly DiagnosticListener s_diagnosticListener = new DiagnosticListener(DiagnosticsHandlerLoggingStrings.DiagnosticListenerName);
    internal static readonly ActivitySource s_activitySource = new ActivitySource(DiagnosticsHandlerLoggingStrings.RequestNamespace);

    private readonly HttpMessageHandler _innerHandler;
    private readonly DistributedContextPropagator _propagator;
    private readonly HeaderDescriptor[]? _propagatorFields;

    public DiagnosticsHandler(HttpMessageHandler innerHandler, DistributedContextPropagator propagator, bool autoRedirect = false)
    {
        Debug.Assert(GlobalHttpSettings.DiagnosticsHandler.EnableActivityPropagation);
        Debug.Assert(innerHandler is not null && propagator is not null);

        _innerHandler = innerHandler;
        _propagator = propagator;

        // Prepare HeaderDescriptors for fields we need to clear when following redirects
        if (autoRedirect && _propagator.Fields is IReadOnlyCollection<string> fields && fields.Count > 0)
        {
            var fieldDescriptors = new List<HeaderDescriptor>(fields.Count);
            foreach (string field in fields)
            {
                if (field is not null && HeaderDescriptor.TryGet(field, out HeaderDescriptor descriptor))
                {
                    fieldDescriptors.Add(descriptor);
                }
            }
            _propagatorFields = fieldDescriptors.ToArray();
        }
    }

    private static bool IsEnabled()
    {
        // check if there is a parent Activity or if someone listens to "System.Net.Http" ActivitySource or "HttpHandlerDiagnosticListener" DiagnosticListener.
        return Activity.Current != null ||
               s_activitySource.HasListeners() ||
               s_diagnosticListener.IsEnabled();
    }

    internal override ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
    {
        if (IsEnabled())
        {
            return SendAsyncCore(request, async, cancellationToken);
        }
        else
        {
            return async ?
                new ValueTask<HttpResponseMessage>(_innerHandler.SendAsync(request, cancellationToken)) :
                new ValueTask<HttpResponseMessage>(_innerHandler.Send(request, cancellationToken));
        }
    }
    
    private static Activity? StartActivity(HttpRequestMessage request)  // <-----------------at1.1
    {
        Activity? activity = null;
        if (s_activitySource.HasListeners())
        {
            activity = s_activitySource.StartActivity(DiagnosticsHandlerLoggingStrings.RequestActivityName, ActivityKind.Client);
        }

        if (activity is null &&
            (Activity.Current is not null ||
            s_diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.RequestActivityName, request)))
        {
                                    // System.Net.Http.HttpRequestOut
            activity = new Activity(DiagnosticsHandlerLoggingStrings.RequestActivityName).Start();
        }

        return activity;
    }

    private async ValueTask<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, bool async, CancellationToken cancellationToken)  // <-----------at1.0
    {
        // ...
        DiagnosticListener diagnosticListener = s_diagnosticListener;

        Guid loggingRequestId = Guid.Empty;
        Activity? activity = StartActivity(request);   // <-----------------at1.1

        if (activity is not null)
        {
            // https://github.com/open-telemetry/semantic-conventions/blob/release/v1.23.x/docs/http/http-spans.md#name
            activity.DisplayName = HttpMethod.GetKnownMethod(request.Method.Method)?.Method ?? "HTTP";

            if (activity.IsAllDataRequested)
            {
                // Add standard tags known before sending the request.
                KeyValuePair<string, object?> methodTag = DiagnosticsHelper.GetMethodTag(request.Method, out bool isUnknownMethod);
                activity.SetTag(methodTag.Key, methodTag.Value);
                if (isUnknownMethod)
                {
                    activity.SetTag("http.request.method_original", request.Method.Method);
                }

                if (request.RequestUri is Uri requestUri && requestUri.IsAbsoluteUri)
                {
                    activity.SetTag("server.address", requestUri.Host);
                    activity.SetTag("server.port", requestUri.Port);
                    activity.SetTag("url.full", UriRedactionHelper.GetRedactedUriString(requestUri));
                }
            }

            // Only send start event to users who subscribed for it.
            if (diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.RequestActivityStartName))
            {
                Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.RequestActivityStartName, new ActivityStartData(request));
            }
        }

        // Try to write System.Net.Http.Request event (deprecated)
        if (diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.RequestWriteNameDeprecated))
        {
            long timestamp = Stopwatch.GetTimestamp();
            loggingRequestId = Guid.NewGuid();
            Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.RequestWriteNameDeprecated,
                new RequestData(
                    request,
                    loggingRequestId,
                    timestamp));
        }

        if (activity is not null)
        {
            InjectHeaders(activity, request);
        }

        HttpResponseMessage? response = null;
        Exception? exception = null;
        TaskStatus taskStatus = TaskStatus.RanToCompletion;
        try
        {
            response = async ?
                await _innerHandler.SendAsync(request, cancellationToken).ConfigureAwait(false) :
                _innerHandler.Send(request, cancellationToken);
            return response;
        }
        catch (OperationCanceledException)
        {
            taskStatus = TaskStatus.Canceled;

            // we'll report task status in HttpRequestOut.Stop
            throw;
        }
        catch (Exception ex)
        {
            taskStatus = TaskStatus.Faulted;
            exception = ex;

            if (diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.ExceptionEventName))
            {
                // If request was initially instrumented, Activity.Current has all necessary context for logging
                // Request is passed to provide some context if instrumentation was disabled and to avoid
                // extensive Activity.Tags usage to tunnel request properties
                Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.ExceptionEventName, new ExceptionData(ex, request));
            }
            throw;
        }
        finally
        {
            // Always stop activity if it was started.
            if (activity is not null)
            {
                activity.SetEndTime(DateTime.UtcNow);

                if (activity.IsAllDataRequested)
                {
                    // Add standard tags known at request completion.
                    if (response is not null)
                    {
                        activity.SetTag("http.response.status_code", DiagnosticsHelper.GetBoxedInt32((int)response.StatusCode));
                        activity.SetTag("network.protocol.version", DiagnosticsHelper.GetProtocolVersionString(response.Version));
                    }

                    if (DiagnosticsHelper.TryGetErrorType(response, exception, out string? errorType))
                    {
                        activity.SetTag("error.type", errorType);

                        // The presence of error.type indicates that the conditions for setting Error status are also met.
                        // https://github.com/open-telemetry/semantic-conventions/blob/v1.26.0/docs/http/http-spans.md#status
                        activity.SetStatus(ActivityStatusCode.Error);
                    }
                }

                // Only send stop event to users who subscribed for it.
                if (diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.RequestActivityStopName))
                {
                    Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.RequestActivityStopName, new ActivityStopData(response, request, taskStatus));
                }

                activity.Stop();
            }

            // Try to write System.Net.Http.Response event (deprecated)
            if (diagnosticListener.IsEnabled(DiagnosticsHandlerLoggingStrings.ResponseWriteNameDeprecated))
            {
                long timestamp = Stopwatch.GetTimestamp();
                Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.ResponseWriteNameDeprecated,
                    new ResponseData(
                        response,
                        loggingRequestId,
                        timestamp,
                        taskStatus));
            }
        }
    }

    protected override void Dispose(bool disposing) => ...;

    private sealed class ActivityStartData
    {
        internal ActivityStartData(HttpRequestMessage request)
        {
            Request = request;
        }

        public HttpRequestMessage Request { get; }

        public override string ToString() => $"{{ {nameof(Request)} = {Request} }}";
    }

    private sealed class ActivityStopData
    {
        internal ActivityStopData(HttpResponseMessage? response, HttpRequestMessage request, TaskStatus requestTaskStatus)
        {
            Response = response;
            Request = request;
            RequestTaskStatus = requestTaskStatus;
        }

        public HttpResponseMessage? Response { get; }
        public HttpRequestMessage Request { get; }
        public TaskStatus RequestTaskStatus { get; }

        public override string ToString() => $"{{ {nameof(Response)} = {Response}, {nameof(Request)} = {Request}, {nameof(RequestTaskStatus)} = {RequestTaskStatus} }}";
    }

	// ...
}
//--------------------------------------Ʌ

//--------------------------------------------V
public abstract partial class DiagnosticSource
{
    internal const string WriteRequiresUnreferencedCode = "The type of object being written to DiagnosticSource cannot be discovered statically.";
    internal const string WriteOfTRequiresUnreferencedCode = "Only the properties of the T type will be preserved. Properties of referenced types and properties of derived types may be trimmed.";
 
    public abstract void Write(string name, object? value);
    public void Write<T>(string name, T value) => Write(name, (object?)value);
    public abstract bool IsEnabled(string name);
    public virtual bool IsEnabled(string name, object? arg1, object? arg2 = null)
    {
        return IsEnabled(name);
    }
}
//--------------------------------------------Ʌ

//-------------------------------------V
public partial class DiagnosticListener : DiagnosticSource, IObservable<KeyValuePair<string, object?>>, IDisposable
{
    public DiagnosticListener(string name);

    public static IObservable<DiagnosticListener> AllListeners { get; }
    public string Name { get; }

    public bool IsEnabled();
    public override void OnActivityExport(Activity activity, object? payload);
    public override void OnActivityImport(Activity activity, object? payload);
    public virtual IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Func<string, object?, object?, bool>? isEnabled, Action<Activity, object?>? onActivityImport = null, Action<Activity, object?>? onActivityExport = null);
    
    // ...
}
//-------------------------------------Ʌ

//----------------------------------------------------V
internal static class DiagnosticsHandlerLoggingStrings
{
    public const string DiagnosticListenerName         = "HttpHandlerDiagnosticListener";
    public const string RequestNamespace               = "System.Net.Http";
    public const string RequestWriteNameDeprecated     = RequestNamespace + ".Request";
    public const string ResponseWriteNameDeprecated    = RequestNamespace + ".Response";
    public const string ExceptionEventName             = RequestNamespace + ".Exception";
    public const string RequestActivityName            = RequestNamespace + ".HttpRequestOut";
    public const string RequestActivityStartName       = RequestActivityName + ".Start";
    public const string RequestActivityStopName        = RequestActivityName + ".Stop";

    public const string ConnectionsNamespace           = "Experimental.System.Net.Http.Connections";
    public const string ConnectionSetupActivityName    = ConnectionsNamespace + ".ConnectionSetup";
    public const string WaitForConnectionActivityName  = ConnectionsNamespace + ".WaitForConnection";
}
//----------------------------------------------------Ʌ
```