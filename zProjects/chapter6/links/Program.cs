﻿using links;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Collections.Concurrent;

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(b => b.AddService("links-sample"))
    .AddSource(nameof(Processor))
    .AddSource(nameof(Producer))
    .AddJaegerExporter()
    .Build()!;

var queue = new ConcurrentQueue<WorkItem>();
var producer = new Producer(queue);

var consumer = new BatchProcessor(queue);
consumer.Start();

producer.Enqueue(1);
producer.Enqueue(2);
producer.Enqueue(3);

await consumer.Stop();

Console.ReadLine();



