using System.Diagnostics;

namespace ConsoleAppTracingDemo
{
    class Programxx
    {
        private static readonly ActivitySource ActivitySource = new ActivitySource("DemoApp.Tracing");
        static void Mainzz(string[] args)
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "DemoApp.Tracing", // listen only to our ActivitySource
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
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

            using (Activity parentActivity = ActivitySource.StartActivity("ParentActivity")!)   // start a parent activity, its internal ActivityContext is null,  check dlrspecial
            {
                parentActivity.SetTag("myTags", "this is it");
                parentActivity.SetBaggage("UserId", "1234");

                parentActivity.SetTag("rootTag", "This is the root activity.");    // set some additional information on the root activity

                Console.WriteLine($"  RA Id: {parentActivity.Id}");
                Console.WriteLine($"  RA RootId: {parentActivity.RootId}");
                Console.WriteLine($"  RA TraceId: {parentActivity.TraceId}");
                Console.WriteLine($"  RA ParentId: {parentActivity.ParentId}");
                Console.WriteLine($"  RA ParentSpanId: {parentActivity.ParentSpanId}");
                Console.WriteLine($"  RA SpanId: {parentActivity.SpanId}");
                Console.WriteLine("\n");


                //Activity.Current = parentActivity;   // no need to explicit set parentActivity to Current,
                                                       // <----------cact3.0, see how Activity.Start() automatically set parentActivity to Activity.Current

                using (Activity childActivity = ActivitySource.StartActivity("ChildActivity")!)  // Additional child activity within the root activity, its internal ActivityContext is also null, check dlrspecial
                {
                    childActivity.SetTag("childTag", "This is another child activity.");

                    Console.WriteLine($"  CA Id: {childActivity.Id}");
                    Console.WriteLine($"  CA RootId: {childActivity.RootId}");
                    Console.WriteLine($"  CA TraceId: {childActivity.TraceId}");
                    Console.WriteLine($"  CA ParentId: {childActivity.ParentId}");
                    Console.WriteLine($"  CA ParentSpanId: {childActivity.ParentSpanId}");
                    Console.WriteLine($"  CA SpanId: {childActivity.SpanId}");
                    Console.WriteLine("\n");
                }
            }

            Console.WriteLine("\n");

            Console.WriteLine("Tracing has finished.");

            Console.ReadLine();
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
}
