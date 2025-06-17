using System;
using System.Diagnostics.Metrics;
using System.Threading;

class Program2
{
    static void Main2(string[] args)
    {
        var meter = new Meter("HatCo.Store");

        // Example: Track the current number of items in a queue
        int queueLength = 0;

        // Create an observable counter
        var queueLengthCounter = meter.CreateObservableCounter<int>(
            "queue_length",
            () => new Measurement<int>(queueLength)
        );

        // Simulate queue changes
        queueLength = 5; // The observable counter will report 5 when collected

        Console.ReadLine();
    }
}