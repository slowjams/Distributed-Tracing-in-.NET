using System.Diagnostics.Metrics;

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

        // Initial subscription with log1.txt
        listener.EnableMeasurementEvents(counter, currentLog);

        // Simulate measurements and log rotation
        for (int i = 0; i < 1000; i++)
        {
            counter.Add(i);

            // Check if current log file is full
            currentLog.Flush();
            FileInfo fi = new FileInfo(currentLogPath);
            if (fi.Length >= maxLogSize && currentLogPath == log1Path)
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

        Console.WriteLine("Done. Press Enter to exit.");
        Console.ReadLine();
    }
}

/*
 
Suppose you want to log measurements to a file, and you need to rotate the log file at runtime (e.g., daily or when a file size limit is reached).
You can use the state object to hold the current file stream. When you rotate the log, you re-enable the listener with a new file stream as the state, 
and clean up the old one.

 */