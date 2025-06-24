using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;

class Program7
{
    static Meter meter = new Meter("Example");
    static Counter<int> counter = meter.CreateCounter<int>("my_counter");

    static void Main7()
    {
        var listener = new MeterListener();
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            Console.WriteLine($"Measurement: {value}, State: {state}");
        });

        // First subscription with state "FirstState"
        listener.EnableMeasurementEvents(counter, "FirstState");

        // ... some measurements happen ...

        // Re-enable with a new state "SecondState"
        listener.MeasurementsCompleted = (instrument, oldState) =>
        {
            Console.WriteLine($"Cleaning up old state: {oldState}");
        };
        listener.EnableMeasurementEvents(counter, "SecondState");

        // Output:
        // Cleaning up old state: FirstState

        // Now, measurements will use "SecondState" as the state object.

        Console.ReadLine();
    }
}
