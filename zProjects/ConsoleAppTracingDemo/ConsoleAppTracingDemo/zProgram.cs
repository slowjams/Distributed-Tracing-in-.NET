using OpenTelemetry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleAppTracingDemo
{
    internal class Programz
    {
        static void Mainz(string[] args)
        {
            var act = new Activity("MyActivity").Start();
            act.SetTag("myTags", "this is it");
            act.SetBaggage("UserId", "1234");
            //act.Stop();

            var act2 = new Activity("MyActivity2").Start();
            act2.SetBaggage("UserId", "5678");
            //act2.Stop();

            var act3 = new Activity("MyActivity3").Start();

            Console.WriteLine("Hello World! from Programz");
            Console.ReadLine();
        }

        static async Task Mainzz()
        {
            Baggage.SetBaggage("UserId", "1234");

            await Task.Run(async () =>
            {
                // Inherits baggage from parent context
                Console.WriteLine($"Task.Run Start: {Baggage.GetBaggage("UserId")}"); // Output: 1234

                // Change baggage in this async flow
                Baggage.SetBaggage("UserId", "5678");
                Console.WriteLine($"Task.Run Changed: {Baggage.GetBaggage("UserId")}"); // Output: 5678

                await Task.Delay(100);
                Console.WriteLine($"Task.Run After Delay: {Baggage.GetBaggage("UserId")}"); // Output: 5678
            });

           

            Console.ReadLine();
        }
    }
   
}
