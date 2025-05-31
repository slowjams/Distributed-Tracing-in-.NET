using System.Diagnostics;

namespace ConsoleAppTracingDemo
{
    class ProgramActivitySource
    {
        //private static readonly ActivitySource ActivitySource = new ActivitySource("System.Net.Http");
        static void Main(string[] args)
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    Console.WriteLine($"Activity Started: {activity.DisplayName}");
                },
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

            var httpClient = new HttpClient();
            var result = httpClient.GetAsync("https://kalapos.net").Result;

            Console.WriteLine("\n");

            Console.WriteLine("Tracing has finished.");

            Console.ReadLine();
        } 
    }
}
