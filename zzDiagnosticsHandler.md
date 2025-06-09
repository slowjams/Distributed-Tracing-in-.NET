## Source Code


```C#
//--------------------------------------V
internal sealed class DiagnosticsHandler : HttpMessageHandlerStage
{
    private static readonly DiagnosticListener s_diagnosticListener = new DiagnosticListener(DiagnosticsHandlerLoggingStrings.DiagnosticListenerName);
                                                                   // new DiagnosticListener("HttpHandlerDiagnosticListener")

    internal static readonly ActivitySource s_activitySource = new ActivitySource(DiagnosticsHandlerLoggingStrings.RequestNamespace);  // <--------------------!
                                                            // new ActivitySource("System.Net.Http")

    private readonly HttpMessageHandler _innerHandler;
    private readonly DistributedContextPropagator _propagator;
    private readonly HeaderDescriptor[]? _propagatorFields;

    public DiagnosticsHandler(HttpMessageHandler innerHandler, DistributedContextPropagator propagator, bool autoRedirect = false)
    {
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
    
    private static Activity? StartActivity(HttpRequestMessage request)  // <----------------------------at1.1
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
                    // new Activity("System.Net.Http.HttpRequestOut")
            activity = new Activity(DiagnosticsHandlerLoggingStrings.RequestActivityName).Start();  // <---------!dcp, this is the activity instance that passed to downstream
        }                                                                                           // because in the downstream webapi, the activitiy's operation name
                                                                                                    // is Microsoft.AspNetCore.Hosting.HttpRequestIn
        return activity;
    }

    private async ValueTask<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, bool async, CancellationToken cancellationToken)  // <-----------at1.0
    {
        // ...
        DiagnosticListener diagnosticListener = s_diagnosticListener;

        Guid loggingRequestId = Guid.Empty;
        Activity? activity = StartActivity(request);   // <-----------------at1.1, dcp

        if (activity is not null)
        {
            // https://github.com/open-telemetry/semantic-conventions/blob/release/v1.23.x/docs/http/http-spans.md#name
            activity.DisplayName = HttpMethod.GetKnownMethod(request.Method.Method)?.Method ?? "HTTP";

            if (activity.IsAllDataRequested)  // <----------------will not call following activity.SetTag("server.address", requestUri.Host) if IsAllDataRequested is false
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
                                       // "System.Net.Http.HttpRequestOut.Start"
                Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.RequestActivityStartName, new ActivityStartData(request));   // <---------------dcp
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
            InjectHeaders(activity, request);  // <---------------dcp
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
                                         // <---------------oe "System.Net.Http.Exception"
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
                                          // "System.Net.Http.HttpRequestOut.Stop"
                    Write(diagnosticListener, DiagnosticsHandlerLoggingStrings.RequestActivityStopName, new ActivityStopData(response, request, taskStatus)); // <-------------dcp
                }

                activity.Stop();  // <----------------------------------------
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

    private void InjectHeaders(Activity currentActivity, HttpRequestMessage request)
    {
        _propagator.Inject(currentActivity, request, static (carrier, key, value) =>   // <---------------dcp, carrier is HttpRequestMessage
        {                                                                              // key is "traceparent", value is activity.Id
            if (carrier is HttpRequestMessage request &&
                key is not null &&
                HeaderDescriptor.TryGet(key, out HeaderDescriptor descriptor) &&
                !request.Headers.TryGetHeaderValue(descriptor, out _))
            {
                request.Headers.TryAddWithoutValidation(descriptor, value);
            }
        });
        request.MarkPropagatorStateInjectedByDiagnosticsHandler();
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

//--------------------------------------------V
public abstract partial class DiagnosticSource
{
    public Activity StartActivity(Activity activity, object? args)
    {
        activity.Start();
        Write(activity.OperationName + ".Start", args);  // <----------------------dss
        return activity;
    }

    public void StopActivity(Activity activity, object? args)
    {
        // stop sets the end time if it was unset, but we want it set before we issue the write so we do it now.
        if (activity.Duration == TimeSpan.Zero)
            activity.SetEndTime(Activity.GetUtcNow());
        Write(activity.OperationName + ".Stop", args);  // <----------------------dss
        activity.Stop();    // resets Activity.Current (we want this after the Write)
    }

    public virtual void OnActivityImport(Activity activity, object? payload) { }
    public virtual void OnActivityExport(Activity activity, object? payload) { }

   
}
//--------------------------------------------Ʌ

//-------------------------------------V
public partial class DiagnosticListener : DiagnosticSource, IObservable<KeyValuePair<string, object?>>, IDisposable
{
    private volatile DiagnosticSubscription? _subscriptions;
    private DiagnosticListener? _next;      // keep a linked list of all NotificationListeners (s_allListeners)
    private bool _disposed;                    

    private static DiagnosticListener? s_allListeners;       // linked list of all instances of DiagnosticListeners.
    private static volatile AllListenerObservable? s_allListenerObservable;   // to make callbacks to this object when listeners come into existence.
    private static readonly object s_allListenersLock = new object();

    public string Name { get; }
    
    public DiagnosticListener(string name)
    {
        Name = name;

        // Insert myself into the list of all Listeners.
        lock (s_allListenersLock)
        {
            // Issue the callback for this new diagnostic listener.
            s_allListenerObservable?.OnNewDiagnosticListener(this);

            // And add it to the list of all past listeners.
            _next = s_allListeners;
            s_allListeners = this;
        }
    }

    public static IObservable<DiagnosticListener> AllListeners 
    {
        get {
             return 
                 s_allListenerObservable ??
                 Interlocked.CompareExchange(ref s_allListenerObservable, new AllListenerObservable(), null) ?? s_allListenerObservable;
        }
    }

    public virtual IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Predicate<string>? isEnabled)
    {
        IDisposable subscription;
        if (isEnabled == null)
        {
            subscription = SubscribeInternal(observer, null, null, null, null);
        }
        else
        {
            Predicate<string> localIsEnabled = isEnabled;
            subscription = SubscribeInternal(observer, isEnabled, (name, arg1, arg2) => localIsEnabled(name), null, null);
        }

        return subscription;
    }

    private DiagnosticSubscription SubscribeInternal(IObserver<KeyValuePair<string, object?>> observer,
            Predicate<string>? isEnabled1Arg, Func<string, object?, object?, bool>? isEnabled3Arg,
            Action<Activity, object?>? onActivityImport, Action<Activity, object?>? onActivityExport)
    {
        // If we have been disposed, we silently ignore any subscriptions.
        if (_disposed)
        {
            return new DiagnosticSubscription() { Owner = this };
        }
        DiagnosticSubscription newSubscription = new DiagnosticSubscription()
        {
            Observer = observer,
            IsEnabled1Arg = isEnabled1Arg,
            IsEnabled3Arg = isEnabled3Arg,
            OnActivityImport = onActivityImport,
            OnActivityExport = onActivityExport,
            Owner = this,
            Next = _subscriptions
        };

        while (Interlocked.CompareExchange(ref _subscriptions, newSubscription, newSubscription.Next) != newSubscription.Next)
            newSubscription.Next = _subscriptions;
        return newSubscription;
    }

    public virtual void Dispose()
    {
        // remove myself from the list of all listeners.
        lock (s_allListenersLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (s_allListeners == this)
                s_allListeners = s_allListeners._next;
            else
            {
                var cur = s_allListeners;
                while (cur != null)
                {
                    if (cur._next == this)
                    {
                        cur._next = _next;
                        break;
                    }
                    cur = cur._next;
                }
            }
            _next = null;
        }

        // indicate completion to all subscribers.
        DiagnosticSubscription? subscriber = null;
        subscriber = Interlocked.Exchange(ref _subscriptions, subscriber);
        while (subscriber != null)
        {
            subscriber.Observer.OnCompleted();
            subscriber = subscriber.Next;
        }
        // the code above also nulled out all subscriptions.
    }

    public bool IsEnabled() => _subscriptions != null;

    public override bool IsEnabled(string name, object? arg1, object? arg2 = null)
    {
        for (DiagnosticSubscription? curSubscription = _subscriptions; curSubscription != null; curSubscription = curSubscription.Next)
        {
            if (curSubscription.IsEnabled3Arg == null || curSubscription.IsEnabled3Arg(name, arg1, arg2))
                return true;
        }
        return false;
    }

    public override void Write(string name, object? value)   // <--------------------------
    {
        for (DiagnosticSubscription? curSubscription = _subscriptions; curSubscription != null; curSubscription = curSubscription.Next)
            curSubscription.Observer.OnNext(new KeyValuePair<string, object?>(name, value));
    }
        
    private sealed class DiagnosticSubscription : IDisposable
    {
        internal IObserver<KeyValuePair<string, object?>> Observer = null!;
        internal Predicate<string>? IsEnabled1Arg;
        internal Func<string, object?, object?, bool>? IsEnabled3Arg;
        internal Action<Activity, object?>? OnActivityImport;
        internal Action<Activity, object?>? OnActivityExport;

        internal DiagnosticListener Owner = null!;    // the DiagnosticListener this is a subscription for.
        internal DiagnosticSubscription? Next;        // linked list of subscribers

        public void Dispose()
        {
            // to keep this lock free and easy to analyze, the linked list is READ ONLY.   Thus we copy
            while (true)
            {
                DiagnosticSubscription? subscriptions = Owner._subscriptions;
                DiagnosticSubscription? newSubscriptions = Remove(subscriptions, this);    // Make a new list, with myself removed.

                // try to update, but if someone beat us to it, then retry.
                if (Interlocked.CompareExchange(ref Owner._subscriptions, newSubscriptions, subscriptions) == subscriptions)
                {
                    var cur = newSubscriptions;
                    while (cur != null)
                    {
                        cur = cur.Next;
                    }
                    break;
                }
            }
        }

        private static DiagnosticSubscription? Remove(DiagnosticSubscription? subscriptions, DiagnosticSubscription subscription)
        {
            if (subscriptions == null)
            {
                // May happen if the IDisposable returned from Subscribe is Dispose'd again
                return null;
            }

            if (subscriptions.Observer == subscription.Observer &&
                subscriptions.IsEnabled1Arg == subscription.IsEnabled1Arg &&
                subscriptions.IsEnabled3Arg == subscription.IsEnabled3Arg)
                return subscriptions.Next;

            return new DiagnosticSubscription() { Observer = subscriptions.Observer, Owner = subscriptions.Owner, IsEnabled1Arg = subscriptions.IsEnabled1Arg, IsEnabled3Arg = subscriptions.IsEnabled3Arg, Next = Remove(subscriptions.Next, subscription) };
        }
    }

    private sealed class AllListenerObservable : IObservable<DiagnosticListener>
    {
        private AllListenerSubscription? _subscriptions;

        public IDisposable Subscribe(IObserver<DiagnosticListener> observer)
        {
            lock (s_allListenersLock)
            {
                // Call back for each existing listener on the new callback (catch-up).
                for (DiagnosticListener? cur = s_allListeners; cur != null; cur = cur._next)
                    observer.OnNext(cur);

                // Add the observer to the list of subscribers.
                _subscriptions = new AllListenerSubscription(this, observer, _subscriptions);
                return _subscriptions;
            }
        }

        internal void OnNewDiagnosticListener(DiagnosticListener diagnosticListener)
        {
            Debug.Assert(Monitor.IsEntered(s_allListenersLock));     // We should only be called when we hold this lock

            // simply send a callback to every subscriber that we have a new listener
            for (var cur = _subscriptions; cur != null; cur = cur.Next)
                cur.Subscriber.OnNext(diagnosticListener);
        }

        private bool Remove(AllListenerSubscription subscription)
        {
            lock (s_allListenersLock)
            {
                if (_subscriptions == subscription)
                {
                    _subscriptions = subscription.Next;
                    return true;
                }
                else if (_subscriptions != null)
                {
                    for (var cur = _subscriptions; cur.Next != null; cur = cur.Next)
                    {
                        if (cur.Next == subscription)
                        {
                            cur.Next = cur.Next.Next;
                            return true;
                        }
                    }
                }

                // Subscriber likely disposed multiple times
                return false;
            }
        }

        internal sealed class AllListenerSubscription : IDisposable
        {
            internal AllListenerSubscription(AllListenerObservable owner, IObserver<DiagnosticListener> subscriber, AllListenerSubscription? next)
            {
                this._owner = owner;
                this.Subscriber = subscriber;
                this.Next = next;
            }

            public void Dispose()
            {
                if (_owner.Remove(this))
                {
                    Subscriber.OnCompleted();           // Called outside of a lock
                }
            }

            private readonly AllListenerObservable _owner;               // the list this is a member of.
            internal readonly IObserver<DiagnosticListener> Subscriber;
            internal AllListenerSubscription? Next;
        }
    }
}
//-------------------------------------Ʌ
//-------------------------------------V
public partial class DiagnosticListener
{
    public override void OnActivityImport(Activity activity, object? payload)
    {
        for (DiagnosticSubscription? curSubscription = _subscriptions; curSubscription != null; curSubscription = curSubscription.Next)
            curSubscription.OnActivityImport?.Invoke(activity, payload);
    }

    public override void OnActivityExport(Activity activity, object? payload)
    {
        for (DiagnosticSubscription? curSubscription = _subscriptions; curSubscription != null; curSubscription = curSubscription.Next)
            curSubscription.OnActivityExport?.Invoke(activity, payload);
    }

    public virtual IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Func<string, object?, object?, bool>? isEnabled,
        Action<Activity, object?>? onActivityImport = null, Action<Activity, object?>? onActivityExport = null)
    {
        return isEnabled == null ?
            SubscribeInternal(observer, null, null, onActivityImport, onActivityExport) :
            SubscribeInternal(observer, name => IsEnabled(name, null, null), isEnabled, onActivityImport, onActivityExport);
    }
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