using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;

class Program5
{
    static Meter meter = new Meter("AppMetrics");
    static Counter<int> counter = meter.CreateCounter<int>("requests");

    // Simple rolling average class
    class RollingAverage
    {
        private int count = 0;
        private double total = 0;

        public void Add(int value)
        {
            count++;
            total += value;
        }

        public double Value => count == 0 ? 0 : total / count;
    }

    static RollingAverage rollingAverage = new RollingAverage();

    static void Main5()
    {
        // Listener 1: logs raw measurements
        var listener1 = new MeterListener();
        listener1.SetMeasurementEventCallback<int>(OnMeasurement);
        listener1.EnableMeasurementEvents(counter, state: "RawLogger");
        listener1.Start();

        // Listener 2: computes rolling average
        var listener2 = new MeterListener();
        listener2.SetMeasurementEventCallback<int>(OnMeasurement);
        listener2.EnableMeasurementEvents(counter, state: "Averager");
        listener2.Start();

        // Simulate measurements
        for (int i = 1; i <= 5; i++)
        {
            counter.Add(i);
            Thread.Sleep(500);
        }

        Console.ReadLine();
    }

    static void OnMeasurement(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (state)
        {
            case "RawLogger":
                Console.WriteLine($"RawLogger: {measurement}");
                break;

            case "Averager":
                rollingAverage.Add(measurement);
                Console.WriteLine($"Rolling average: {rollingAverage.Value:F2}");
                break;
        }
    }
}
