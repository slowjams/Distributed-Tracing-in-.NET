using OpenTelemetry;
using OpenTelemetry.Metrics;
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

class Program4
{
    static void Main4()
    {
        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("BookStore.WebApi")

            // View 1: Histogram with custom buckets
            //.AddView(
            //    instrumentName: "orders-number-of-books",
            //    new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 1.0, 2.0, 5.0 } })

             //View 2: Histogram by city only(filter tags)
            .AddView(
                instrumentName: "orders-number-of-books",
                new ExplicitBucketHistogramConfiguration
                {
                    Name = "orders-number-of-books-by-city",
                    Boundaries = new[] { 1.0, 2.0, 5.0 },
                    TagKeys = new[] { "city" }
                })
            .AddConsoleExporter()
            .Build();

        var meter = new Meter("BookStore.WebApi");
        var histogram = meter.CreateHistogram<int>("orders-number-of-books", "Books", "Number of books per order");

        // Correct way to record with tags:
        //histogram.Record(2);
        //histogram.Record(3);
        //histogram.Record(4);

        histogram.Record(2, new[] { new KeyValuePair<string, object?>("city", "Sydney") });
        histogram.Record(3, new[] { new KeyValuePair<string, object?>("city", "Melbourne") });
        histogram.Record(4, new[] { new KeyValuePair<string, object?>("country", "Australia") });
        Console.ReadLine();
    }
}
