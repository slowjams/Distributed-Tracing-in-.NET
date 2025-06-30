using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;

class Program7   // simpfied for debugging purposes, see full example at OldStateScenario.cs
{
    static Meter meter = new Meter("Example");
    static Counter<int> counter = meter.CreateCounter<int>("my_counter");

    static void Main()
    {
        var listener = new MeterListener();
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            Console.WriteLine($"Measurement: {value}, State: {state}");
        });

        // First subscription with state "FirstState"
        listener.EnableMeasurementEvents(counter, "FirstState");

        // ... some measurements happen ...

        listener.MeasurementsCompleted = (instrument, oldState) =>
        {
            Console.WriteLine($"Cleaning up old state: {oldState}");
        };

        // re-enable with a new state "SecondState"
        listener.EnableMeasurementEvents(counter, "SecondState");   // <--------------------this will trigger MeasurementsCompleted for "FirstState"

        // Output:
        // Cleaning up old state: FirstState

        // Now, measurements will use "SecondState" as the state object.

        Console.ReadLine();
    }
}
