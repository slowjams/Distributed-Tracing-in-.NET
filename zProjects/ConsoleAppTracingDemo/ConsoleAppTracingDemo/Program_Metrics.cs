using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ConsoleAppTracingDemo
{
    class Program
    {
        static Meter s_meter = new Meter("HatCo.Store");
        static Counter<int> s_hatsSold = s_meter.CreateCounter<int>("hatco.store.hats_sold");

        static void Main(string[] args)
        {
            Console.WriteLine("Press any key to exit");
            while (!Console.KeyAvailable)
            {
                // Pretend our store has a transaction each second that sells 4 hats
                Thread.Sleep(1000);
                s_hatsSold.Add(4);
            }
        }
    }
}
