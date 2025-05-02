using System.Diagnostics;

namespace ConsoleAppTracingDemo
{
    class Program2
    {
        private static readonly ActivitySource ActivitySource = new ActivitySource("DemoApp.Tracing");
        static void Main2(string[] args)
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
                        Console.WriteLine($"  Tag: {tag.Key} = {tag.Value}");
                    }
                }
            };

            ActivitySource.AddActivityListener(listener);

            using (var parentActivity = ActivitySource.StartActivity("ParentActivity")!)   // start a parent activity
            {
                parentActivity.SetTag("rootTag", "This is the root activity.");    // set some additional information on the root activity

                Console.WriteLine($"  RA Id: {parentActivity.Id}");
                Console.WriteLine($"  RA RootId: {parentActivity.RootId}");
                Console.WriteLine($"  RA TraceId: {parentActivity.TraceId}");
                Console.WriteLine($"  RA ParentId: {parentActivity.ParentId}");
                Console.WriteLine($"  RA ParentSpanId: {parentActivity.ParentSpanId}");
                Console.WriteLine($"  RA SpanId: {parentActivity.SpanId}");
                Console.WriteLine("\n");

                var childActivityContext = parentActivity.Context;

                using (var childActivity = ActivitySource.StartActivity("ChildActivity", ActivityKind.Internal, childActivityContext))  // Additional child activity within the root activity

                {
                    if (childActivity != null)
                    {
                        childActivity.SetTag("childTag", "This is another child activity.");

                        Console.WriteLine($"  CA Id: {parentActivity.Id}");
                        Console.WriteLine($"  CA RootId: {parentActivity.RootId}");
                        Console.WriteLine($"  CA TraceId: {parentActivity.TraceId}");
                        Console.WriteLine($"  CA ParentId: {parentActivity.ParentId}");
                        Console.WriteLine($"  CA ParentSpanId: {parentActivity.ParentSpanId}");
                        Console.WriteLine($"  CA SpanId: {parentActivity.SpanId}");
                        Console.WriteLine("\n");

                        Thread.Sleep(200);  // simulate work
                    }
                }
            }

            Console.WriteLine("\n");

            Console.WriteLine("Tracing has finished.");

            Console.ReadLine();
        }


        /*
         
        Activity Started: ParentActivity
          RA Id: 00-97ab06572c06915ee197b02d31ed1d41-e70191024a00e576-01
          RA RootId: 97ab06572c06915ee197b02d31ed1d41
          RA TraceId: 97ab06572c06915ee197b02d31ed1d41
          RA ParentId:
          RA ParentSpanId: 0000000000000000
          RA SpanId: e70191024a00e576


        Activity Started: ChildActivity
          CA Id: 00-97ab06572c06915ee197b02d31ed1d41-e70191024a00e576-01
          CA RootId: 97ab06572c06915ee197b02d31ed1d41
          CA TraceId: 97ab06572c06915ee197b02d31ed1d41
          CA ParentId:
          CA ParentSpanId: 0000000000000000
          CA SpanId: e70191024a00e576
         
        */



















        //static void DoSomeWorkWithActivity()
        //{           
        //    ActivitySource activitySource = new ActivitySource("MyActivitySource");    // create a new child activity (span)

        //    using (var childActivity = activitySource.StartActivity("ChildActivity1"))
        //    {
        //        if (childActivity != null)
        //        {                
        //            childActivity.SetTag("operation", "Some work is being done.");    // set some tags for the child activity
        //            Console.WriteLine($"Child Activity 1 Started: {childActivity.Id}");

        //            Thread.Sleep(300);   // simulate some work in the child activity
        //        }
        //    }
        //}
    }

}
