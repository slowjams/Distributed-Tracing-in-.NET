﻿using System;
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

            //act.Stop();

            var act2 = new Activity("MyActivity2").Start();

            act2.Stop();

            Console.WriteLine("Hello World! from Programz");
            Console.ReadLine();
        }
    }
   
}
