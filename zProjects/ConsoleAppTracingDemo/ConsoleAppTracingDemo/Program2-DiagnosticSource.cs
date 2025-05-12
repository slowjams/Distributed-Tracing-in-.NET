using System.Diagnostics;
using System.Reflection;

namespace DiagnosticSourceSample
{
    class Program2
    {
        static void Main2(string[] args)
        {
            Subscribe();
            var mySampleLibrary = new MySampleLibrary();
            //var number = mySampleLibrary.GetRandomNumber();

            //var httpClient = new HttpClient();
            //var result = httpClient.GetAsync("https://kalapos.net").Result;
            mySampleLibrary.DoThingAsync(42).Wait();

            Console.WriteLine("Hello World");

            Console.ReadLine();
        }

        private static void Subscribe()
        {
            DiagnosticListener.AllListeners.Subscribe(new Subscriber());
        }
    }

    class Subscriber : IObserver<DiagnosticListener>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == typeof(MySampleLibrary).FullName)
            {
                listener.Subscribe(new MyLibraryListener()!);
            }

            //if (listener.Name == "HttpHandlerDiagnosticListener")
            //{
            //    listener.Subscribe(new HttpClientObserver()!);
            //}
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

    class MyLibraryListener: IObserver<KeyValuePair<string, object>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(KeyValuePair<string, object> keyValue)
        {  
            switch (keyValue.Key)
            {
                case "DoThingAsync.Start":  // <------------see dss
                    Console.WriteLine($"DoThingAsync.Start - activity id: {Activity.Current?.Id}");
                    break;                                              // 00-defe5d956fc4f9a29083d97ff4ce7191-8549d8fd2a698c51-00
                case "DoThingAsync.Stop":
                    Console.WriteLine($"DoThingAsync.End activity id: {Activity.Current?.Id}");
                                                                        // 00-defe5d956fc4f9a29083d97ff4ce7191-8549d8fd2a698c51-00 same as above
                    if (Activity.Current != null)
                    {
                        foreach (var tag in Activity.Current.Tags)
                        {
                            Console.WriteLine($"{tag.Key} - {tag.Value}");
                        }
                    }
                    break;

                case "DiagnosticSourceSample.MySampleLibrary.StartGenerateRandom":
                    Console.WriteLine("Start generating random number");
                    break;
                case "DiagnosticSourceSample.MySampleLibrary.EndGenerateRandom":
                    var randomValue = keyValue.Value.GetType().GetTypeInfo().GetDeclaredProperty("RandomNumber").GetValue(keyValue.Value);
                    Console.WriteLine($"Stop generating random number: {randomValue}");
                    break;
            }        
        }
    }


    //=======================Libray to be instrumented=========================================================
    public class MySampleLibrary
    {
        private static readonly DiagnosticSource diagnosticSource = new DiagnosticListener(typeof(MySampleLibrary).FullName!);
        //private static readonly DiagnosticSource diagnosticSource2 = new DiagnosticListener("test");

        public int GetRandomNumber()
        {
            if (diagnosticSource.IsEnabled(typeof(MySampleLibrary).FullName!))
            {
                diagnosticSource.Write($"{typeof(MySampleLibrary).FullName}.StartGenerateRandom", null);
            }
            var number = new Random().Next();

            if (diagnosticSource.IsEnabled(typeof(MySampleLibrary).FullName!))
            {
                diagnosticSource.Write($"{typeof(MySampleLibrary).FullName}.EndGenerateRandom",
                new { RandomNumber = number });
            }

            return number;
        }

        public async Task DoThingAsync(int id)
        {
            var activity = new Activity(nameof(DoThingAsync));

            if (diagnosticSource.IsEnabled(typeof(MySampleLibrary).FullName!))
            {
                diagnosticSource.StartActivity(activity, new { IdArg = id });  // <------------see dss, DiagnosticSource call Write(activity.OperationName + ".Start", args)
            }

            activity.AddTag("MyTagId", "ValueInTags");
            activity.AddBaggage("MyBaggageId", "ValueInBaggage");

            //var httpClient = new HttpClient();
            //await httpClient.GetAsync("https://kalapos.net");

            if (diagnosticSource.IsEnabled(typeof(MySampleLibrary).FullName))
            {
                diagnosticSource.StopActivity(activity, new { IdArg = id });
            }
        }
    }
}
