using System;
using System.Diagnostics.Metrics;
using System.Threading;

class Program1
{
    static Meter s_meter = new Meter("HatCo.Store");
    static Counter<int> s_hatsSold = s_meter.CreateCounter<int>("hatco.store.hats_sold");
    static Histogram<double> s_orderProcessingTime = s_meter.CreateHistogram<double>("hatco.store.order_processing_time");
    static int s_coatsSold;
    static int s_ordersPending;

    static Random s_rand = new Random();

    static void Main1(string[] args)
    {
        s_meter.CreateObservableCounter<int>("hatco.store.coats_sold", () => s_coatsSold);
        s_meter.CreateObservableGauge<int>("hatco.store.orders_pending", () => s_ordersPending);

        Console.WriteLine("Press any key to exit");
        while (!Console.KeyAvailable)
        {
            // Pretend our store has one transaction each 100ms that each sell 4 hats
            Thread.Sleep(100);
            s_hatsSold.Add(4);

            // Pretend we also sold 3 coats. For an ObservableCounter we track the value in our variable and report it
            // on demand in the callback
            s_coatsSold += 3;

            // Pretend we have some queue of orders that varies over time. The callback for the orders_pending gauge will report
            // this value on-demand.
            s_ordersPending = s_rand.Next(0, 20);

            // Last we pretend that we measured how long it took to do the transaction (for example we could time it with Stopwatch)
            s_orderProcessingTime.Record(s_rand.Next(5, 15) / 1000.0);
        }
    }
}