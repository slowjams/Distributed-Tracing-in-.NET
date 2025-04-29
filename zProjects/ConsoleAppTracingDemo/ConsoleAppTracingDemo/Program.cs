using System.Diagnostics;

namespace ConsoleAppTracingDemo
{
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
                        Console.WriteLine($"  Tag: {tag.Key} = {tag.Value}");
                    }
                }
            };

            ActivitySource.AddActivityListener(listener);

            using (var parentActivity = ActivitySource.StartActivity("ParentActivity")!)   // start a parent activity
            {
                parentActivity.SetTag("rootTag", "This is the root activity.");    // set some additional information on the root activity

                Console.WriteLine($"Root Activity Started: {parentActivity.Id}");


                DoSomeWorkWithActivity();   // simulate some work with a child activity (Span)

                using (var childActivity = ActivitySource.StartActivity("ChildActivity2"))  // Additional child activity within the root activity

                {
                    if (childActivity != null)
                    {
                        childActivity.SetTag("childTag", "This is another child activity.");
                        Console.WriteLine($"Child Activity 2 Started: {childActivity.Id}");

                        Thread.Sleep(200);  // simulate work
                    }
                }
            }

            Console.WriteLine("Tracing has finished.");

            Console.ReadLine();
        }

        static void DoSomeWorkWithActivity()
        {           
            ActivitySource activitySource = new ActivitySource("MyActivitySource");    // create a new child activity (span)

            using (var childActivity = activitySource.StartActivity("ChildActivity1"))
            {
                if (childActivity != null)
                {                
                    childActivity.SetTag("operation", "Some work is being done.");    // set some tags for the child activity
                    Console.WriteLine($"Child Activity 1 Started: {childActivity.Id}");
                 
                    Thread.Sleep(300);   // simulate some work in the child activity
                }
            }
        }
    }

}
