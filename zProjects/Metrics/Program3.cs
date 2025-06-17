using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

class Program
{
    // Create a Meter for your app
    private static readonly Meter MyMeter = new("MyCompany.MyApp", "1.0.0");

    // Create a Histogram to measure HTTP request durations in milliseconds
    private static readonly Histogram<double> RequestDuration = MyMeter.CreateHistogram<double>(
        name: "http.server.duration",
        unit: "ms",
        description: "Measures HTTP request duration in milliseconds");

    static void Main(string[] args)
    {
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("MyCompany.MyApp") 
            .AddConsoleExporter()
            .Build();

        var random = new Random();

        Console.WriteLine("Recording 10 sample durations...");

        // Simulate 10 HTTP requests
        for (int i = 0; i < 10; i++)
        {
            double simulatedDuration = random.Next(50, 500); // ms
            RequestDuration.Record(simulatedDuration,
                KeyValuePair.Create<string, object?>("method", "GET"));
            Console.WriteLine($"Recorded: {simulatedDuration} ms");
            Thread.Sleep(500);
        }

        Console.WriteLine("Done. Press any key to exit.");
        Console.ReadKey();
    }
}

/*
Metric Name: http.server.duration
(2025-06-14T13:57:00.2989076Z, 2025-06-14T14:00:34.4877970Z] method: GETHistogram
Value: Sum: 2921 Count: 10 Min: 197 Max: 485
(-Infinity,0]:0
(0,5]:0
(5,10]:0
(10,25]:0
(25,50]:0
(50,75]:0
(75,100]:0
(100,250]:5
(250,500]:5
(500,750]:0
(750,1000]:0
(1000,2500]:0
(2500,5000]:0
(5000,7500]:0
(7500,10000]:0
(10000,+Infinity]:0

Instrumentation scope (Meter):
        Name: MyCompany.MyApp
        Version: 1.0.0
Resource associated with Metric:
        telemetry.sdk.name: opentelemetry
        telemetry.sdk.language: dotnet
        telemetry.sdk.version: 1.12.0
        service.name: unknown_service:Metrics
 */
