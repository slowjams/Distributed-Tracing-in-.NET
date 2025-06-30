## Demystifying Metrics

Summary Table
Step	Object	                     What happens
1	    `Counter<int>`	           Instrument is created
2	    `Metric`, `MetricState`	   Metric(s) and MetricState are created and linked to the instrument
3	    `Counter<int>.Add`	       Measurement is recorded
4	    MetricState	               Routes the measurement to the correct Metric
5	    Metric	                   Forwards the measurement to AggregatorStore
6	    AggregatorStore	           Finds or creates a MetricPoint for the tag set
7	    MetricPoint	               Updates the aggregated value (e.g., increments the sum)


## Prerequsite

**How `MeterListener` interacts with `Instrument` on `ListenerSubscription`**

Suppose you want to log measurements to a file, and you need to rotate the log file at runtime (e.g., daily or when a file size limit is reached).
You can use the state object to hold the current file stream. When you rotate the log, you re-enable the listener with a new file stream as the state, 
and clean up the old one:

```C#
class Program
{
    static Meter meter = new Meter("Example");
    static Counter<int> counter = meter.CreateCounter<int>("my_counter");

    static void Main()
    {
        const long maxLogSize = 1024; // 1 KB for demonstration
        string log1Path = "log1.txt";
        string log2Path = "log2.txt";
        StreamWriter currentLog = new StreamWriter(log1Path);
        string currentLogPath = log1Path;

        var listener = new MeterListener();
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            var writer = state as StreamWriter;
            writer?.WriteLine($"Measurement: {value}");
            writer?.Flush();
        });

        listener.MeasurementsCompleted = (instrument, oldState) =>
        {
            var oldWriter = oldState as StreamWriter;
            if (oldWriter != null)
            {
                oldWriter.WriteLine("Closing log file.");
                oldWriter.Dispose();
            }
        };

        // initial subscription with log1.txt
        listener.EnableMeasurementEvents(counter, currentLog);

        // simulate measurements and log rotation
        for (int i = 0; i < 1000; i++)
        {
            counter.Add(i);

            currentLog.Flush();
            FileInfo fi = new FileInfo(currentLogPath);
            if (fi.Length >= maxLogSize && currentLogPath == log1Path)  // check if current log file is full
            {
                // Switch to log2.txt
                StreamWriter newLog = new StreamWriter(log2Path);
                string oldLogPath = currentLogPath;
                currentLogPath = log2Path;
                listener.EnableMeasurementEvents(counter, newLog); // <--------------------this will trigger MeasurementsCompleted for log1
                currentLog = newLog;
                Console.WriteLine($"Switched to new log file: {log2Path}");
            }

            Thread.Sleep(10); // Simulate time between measurements
        }

        Console.ReadLine();
    }
}
```














A second MetricPoint will be created when you record a measurement with a new, previously unseen combination of tag values for that metric stream.

Example
Suppose your first call is:

If your next call uses a different tag value:

Or if you add another tag key:

Summary
A new MetricPoint is created for each unique tag value combination.
If you keep using the same tag set, the same MetricPoint is reused.
As soon as you use a new combination, a new MetricPoint is created and stored in the array.



The lock (this.onCollectLock) in MetricReader.Collect is used to ensure thread safety when performing the collection of metrics. Multiple threads might call Collect() at the same time (for example, if triggered by both a periodic exporter and a manual flush). The lock ensures that only one thread can execute the critical section (the actual collection logic) at a time.

```C#
class MetricReader
{
    private readonly object onCollectLock = new();

    public void Collect()
    {
        lock (onCollectLock)
        {
            Console.WriteLine($"Collect started by {Thread.CurrentThread.ManagedThreadId} at {DateTime.Now:HH:mm:ss.fff}");
            Thread.Sleep(1000); // Simulate work
            Console.WriteLine($"Collect finished by {Thread.CurrentThread.ManagedThreadId} at {DateTime.Now:HH:mm:ss.fff}");
        }
    }
}

class Program
{
    static void Main()
    {
        var reader = new MetricReader();

        // Periodic collection (simulates exporter)
        var timer = new Timer(_ => reader.Collect(), null, 0, 500);

        // Manual flush (simulates user or SDK trigger)
        Task.Run(() =>
        {
            Thread.Sleep(200); // Wait a bit to overlap with timer
            reader.Collect();
        });

        // Let it run for a few seconds
        Thread.Sleep(3000);
        timer.Dispose();
    }
}
```


Great question! The distinction between `Instrument<T>`/`Instrument` and `Metric` in OpenTelemetry .NET is important for understanding the **metrics pipeline**.

---

### **Instrument<T> / Instrument**
- Represents the **definition** of a metric instrument in your application code.
- Examples: `Counter<int>`, `ObservableGauge<double>`, `Histogram<long>`, etc.
- Used to **record or observe measurements** (e.g., `counter.Add(1)`).
- Exists for the **lifetime of your application** and is used by your code to produce metric data.

---

### **Metric**
- Represents an **aggregated, export-ready view** of a metric stream.
- Created and managed by the **OpenTelemetry SDK** (specifically by the `MetricReader`).
- Holds the **aggregated data** (sum, histogram buckets, last value, etc.) for a specific metric stream, including all the configuration from views, temporality, and aggregation.
- Used **internally by the SDK** for exporting to backends (e.g., Prometheus, OTLP, Console).
- Exists only for the **lifetime of the export/collection cycle**.

---



### **Analogy**

- **Instrument<T>:** Like a sensor in your code, reporting raw data.
- **Metric:** Like a report generated from the sensor data, ready to be sent to a dashboard or backend.

---

**Summary:**  
`Instrument<T>` is for recording measurements in your app;  
`Metric` is for holding the aggregated, export-ready data that the SDK sends to metric backends.  
Both are needed for a clean, efficient, and flexible metrics pipeline.



## AggregatorStore 

```C#
var meter = new Meter("MyApp");
var counter = meter.CreateCounter<int>("requests");

counter.Add(1, KeyValuePair.Create("region", "us-east"));
counter.Add(1, KeyValuePair.Create("region", "eu-west"));
counter.Add(1, KeyValuePair.Create("region", "us-east"));
```

```yml
AggregatorStore
  ├─ MetricPoint[0]: region=us-east, value=2
  └─ MetricPoint[1]: region=eu-west, value=1
  ```