using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;

class Program6
{
    static void Main6()
    {
        var meter = new Meter("MyApp");
        var counter = meter.CreateCounter<int>("requests");

        var listener = new MeterListener();

        var state1 = "state1";
        var state2 = "state2";

        // First subscription
        listener.EnableMeasurementEvents(counter, state1);
        // At this point, oldStateStored is false (no previous subscription)

        // Second subscription with a new state
        listener.EnableMeasurementEvents(counter, state2);

        Console.ReadLine();
    }
}
