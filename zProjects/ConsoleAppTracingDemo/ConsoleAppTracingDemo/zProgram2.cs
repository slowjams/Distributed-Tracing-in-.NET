using OpenTelemetry;
using System.Diagnostics;


public class Worker
{
    private static readonly ActivitySource Source = new ActivitySource("Worker");

    public static void DoWork()
    {
        var work = Source.StartActivity();
    }
}

internal class Program
{
    static void Main(string[] args)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(listener);

        Worker.DoWork();
        Worker.DoWork();
    }
}
