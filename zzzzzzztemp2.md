## OpenTelemetry Metrics

```C#
//------------------------V
public sealed class Metric
{
    internal const int DefaultExponentialHistogramMaxBuckets = 160;
    internal const int DefaultExponentialHistogramMaxScale = 20;
    internal static readonly double[] DefaultHistogramBounds = new double[] { 0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000 };

    // short default histogram bounds. Based on the recommended semantic convention values for http.server.request.duration.
    internal static readonly double[] DefaultHistogramBoundsShortSeconds = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 };
    internal static readonly HashSet<(string, string)> DefaultHistogramBoundShortMappings = new HashSet<(string, string)>
    {
        ("Microsoft.AspNetCore.Hosting", "http.server.request.duration"),
        ("Microsoft.AspNetCore.RateLimiting", "aspnetcore.rate_limiting.request.time_in_queue"),
        ("Microsoft.AspNetCore.RateLimiting", "aspnetcore.rate_limiting.request_lease.duration"),
        ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.tls_handshake.duration"),
        ("OpenTelemetry.Instrumentation.AspNet", "http.server.request.duration"),
        ("OpenTelemetry.Instrumentation.AspNetCore", "http.server.request.duration"),
        ("OpenTelemetry.Instrumentation.Http", "http.client.request.duration"),
        ("System.Net.Http", "http.client.request.duration"),
        ("System.Net.Http", "http.client.request.time_in_queue"),
        ("System.Net.NameResolution", "dns.lookup.duration"),
    }

    // Long default histogram bounds. Not based on a standard. May change in the future.
    internal static readonly double[] DefaultHistogramBoundsLongSeconds = new double[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300 };
    internal static readonly HashSet<(string, string)> DefaultHistogramBoundLongMappings = new HashSet<(string, string)>
    {
        ("Microsoft.AspNetCore.Http.Connections", "signalr.server.connection.duration"),
        ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.connection.duration"),
        ("System.Net.Http", "http.client.connection.duration"),
    };

    internal readonly AggregatorStore AggregatorStore;

    internal Metric(
        MetricStreamIdentity instrumentIdentity,
        AggregationTemporality temporality,
        int cardinalityLimit,
        ExemplarFilterType? exemplarFilter = null,
        Func<ExemplarReservoir?>? exemplarReservoirFactory = null)
    {
        this.InstrumentIdentity = instrumentIdentity;

        AggregationType aggType;
        if (instrumentIdentity.InstrumentType == typeof(ObservableCounter<long>) ||...)  
        {
            aggType = AggregationType.LongSumIncomingCumulative;
            this.MetricType = MetricType.LongSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Counter<long>) || ...)     
        {
            aggType = AggregationType.LongSumIncomingDelta;
            this.MetricType = MetricType.LongSum;
        }
        // ...
        else if (instrumentIdentity.IsHistogram)
        {
            var explicitBucketBounds = instrumentIdentity.HistogramBucketBounds;
            var exponentialMaxSize = instrumentIdentity.ExponentialHistogramMaxSize;
            var histogramRecordMinMax = instrumentIdentity.HistogramRecordMinMax;

            this.MetricType = exponentialMaxSize == 0
                ? MetricType.Histogram
                : MetricType.ExponentialHistogram;

            if (this.MetricType == MetricType.Histogram)
            {
                aggType = explicitBucketBounds != null && explicitBucketBounds.Length == 0
                    ? (histogramRecordMinMax ? AggregationType.HistogramWithMinMax : AggregationType.Histogram)
                    : (histogramRecordMinMax ? AggregationType.HistogramWithMinMaxBuckets : AggregationType.HistogramWithBuckets);
            }
            else
            {
                aggType = histogramRecordMinMax ? AggregationType.Base2ExponentialHistogramWithMinMax : AggregationType.Base2ExponentialHistogram;
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported Instrument Type: {instrumentIdentity.InstrumentType.FullName}");
        }

        this.AggregatorStore = new AggregatorStore(
            instrumentIdentity,
            aggType,
            temporality,
            cardinalityLimit,
            exemplarFilter,
            exemplarReservoirFactory);

        this.Temporality = temporality;
    }

    public MetricType MetricType { get; private set; }

    public AggregationTemporality Temporality { get; private set; }

    public string Name => this.InstrumentIdentity.InstrumentName;

    public string Description => this.InstrumentIdentity.Description;

    public string Unit => this.InstrumentIdentity.Unit;

    public string MeterName => this.InstrumentIdentity.MeterName;

    public string MeterVersion => this.InstrumentIdentity.MeterVersion;

    public IEnumerable<KeyValuePair<string, object?>>? MeterTags => this.InstrumentIdentity.MeterTags?.KeyValuePairs;

    internal MetricStreamIdentity InstrumentIdentity { get; private set; }

    internal bool Active { get; set; } = true;

    public MetricPointsAccessor GetMetricPoints()  => this.AggregatorStore.GetMetricPoints();

    internal void UpdateLong(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags);  // <--------------------------

    internal void UpdateDouble(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags);

    internal int Snapshot() => this.AggregatorStore.Snapshot();
}
//------------------------Ʌ

//-------------------------------V
internal sealed class MetricState
{
    public readonly Action CompleteMeasurement;

    public readonly RecordMeasurementAction<long> RecordMeasurementLong;
    public readonly RecordMeasurementAction<double> RecordMeasurementDouble;

    private MetricState(Action completeMeasurement, RecordMeasurementAction<long> recordMeasurementLong, RecordMeasurementAction<double> recordMeasurementDouble)
    {
        this.CompleteMeasurement = completeMeasurement;
        this.RecordMeasurementLong = recordMeasurementLong;
        this.RecordMeasurementDouble = recordMeasurementDouble;
    }

    internal delegate void RecordMeasurementAction<T>(T value, ReadOnlySpan<KeyValuePair<string, object?>> tags);

    public static MetricState BuildForSingleMetric(Metric metric)
    {
        return new(
            completeMeasurement: () => MetricReader.DeactivateMetric(metric!),
            recordMeasurementLong: metric!.UpdateLong,
            recordMeasurementDouble: metric!.UpdateDouble);
    }

    public static MetricState BuildForMetricList(List<Metric> metrics)
    {
        var metricsArray = metrics!.ToArray();

        return new(
            completeMeasurement: () =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    MetricReader.DeactivateMetric(metricsArray[i]);
                }
            },
            recordMeasurementLong: (v, t) =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    metricsArray[i].UpdateLong(v, t);
                }
            },
            recordMeasurementDouble: (v, t) =>
            {
                for (int i = 0; i < metricsArray.Length; i++)
                {
                    metricsArray[i].UpdateDouble(v, t);
                }
            });
    }
}
//-------------------------------Ʌ

//-------------------------------------------V
internal readonly struct MetricStreamIdentity : IEquatable<MetricStreamIdentity>
{
    private static readonly StringArrayEqualityComparer StringArrayComparer = new();
    private readonly int hashCode;

    public MetricStreamIdentity(Instrument instrument, MetricStreamConfiguration? metricStreamConfiguration)
    {
        this.MeterName = instrument.Meter.Name;
        this.MeterVersion = instrument.Meter.Version ?? string.Empty;
        this.MeterTags = instrument.Meter.Tags != null ? new Tags(instrument.Meter.Tags.ToArray()) : null;
        this.InstrumentName = metricStreamConfiguration?.Name ?? instrument.Name;
        this.Unit = instrument.Unit ?? string.Empty;
        this.Description = metricStreamConfiguration?.Description ?? instrument.Description ?? string.Empty;
        this.InstrumentType = instrument.GetType();
        this.ViewId = metricStreamConfiguration?.ViewId;
        this.MetricStreamName = $"{this.MeterName}.{this.MeterVersion}.{this.InstrumentName}";
        this.TagKeys = metricStreamConfiguration?.CopiedTagKeys;
        this.HistogramBucketBounds = GetExplicitBucketHistogramBounds(instrument, metricStreamConfiguration);
        this.ExponentialHistogramMaxSize = (metricStreamConfiguration as Base2ExponentialBucketHistogramConfiguration)?.MaxSize ?? 0;
        this.ExponentialHistogramMaxScale = (metricStreamConfiguration as Base2ExponentialBucketHistogramConfiguration)?.MaxScale ?? 0;
        this.HistogramRecordMinMax = (metricStreamConfiguration as HistogramConfiguration)?.RecordMinMax ?? true;


        HashCode hashCode = default;
        hashCode.Add(this.InstrumentType);
        hashCode.Add(this.MeterName);
        hashCode.Add(this.MeterVersion);
        hashCode.Add(this.MeterTags);
        hashCode.Add(this.InstrumentName);
        hashCode.Add(this.HistogramRecordMinMax);
        hashCode.Add(this.Unit);
        hashCode.Add(this.Description);
        hashCode.Add(this.ViewId);
        hashCode.Add(this.TagKeys!, StringArrayComparer);
        hashCode.Add(this.ExponentialHistogramMaxSize);
        hashCode.Add(this.ExponentialHistogramMaxScale);
        if (this.HistogramBucketBounds != null)
        {
            for (var i = 0; i < this.HistogramBucketBounds.Length; ++i)
                hashCode.Add(this.HistogramBucketBounds[i]);
        }

        var hash = hashCode.ToHashCode();

        this.hashCode = hash;
    }

    public string MeterName { get; }

    public string MeterVersion { get; }

    public Tags? MeterTags { get; }

    public string InstrumentName { get; }

    public string Unit { get; }

    public string Description { get; }

    public Type InstrumentType { get; }

    public int? ViewId { get; }

    public string MetricStreamName { get; }

    public string[]? TagKeys { get; }

    public double[]? HistogramBucketBounds { get; }

    public int ExponentialHistogramMaxSize { get; }

    public int ExponentialHistogramMaxScale { get; }

    public bool HistogramRecordMinMax { get; }

    public bool IsHistogram =>
        this.InstrumentType == typeof(Histogram<long>)
        || this.InstrumentType == typeof(Histogram<int>)
        || this.InstrumentType == typeof(Histogram<short>)
        || this.InstrumentType == typeof(Histogram<byte>)
        || this.InstrumentType == typeof(Histogram<float>)
        || this.InstrumentType == typeof(Histogram<double>);

    public static bool operator ==(MetricStreamIdentity metricIdentity1, MetricStreamIdentity metricIdentity2) => metricIdentity1.Equals(metricIdentity2);

    public static bool operator !=(MetricStreamIdentity metricIdentity1, MetricStreamIdentity metricIdentity2) => !metricIdentity1.Equals(metricIdentity2);

    public override readonly bool Equals(object? obj)
    {
        return obj is MetricStreamIdentity other && this.Equals(other);
    }

    public bool Equals(MetricStreamIdentity other)
    {
        return this.InstrumentType == other.InstrumentType
            && this.MeterName == other.MeterName
            && this.MeterVersion == other.MeterVersion
            && this.InstrumentName == other.InstrumentName
            && this.Unit == other.Unit
            && this.Description == other.Description
            && this.ViewId == other.ViewId
            && this.MeterTags == other.MeterTags
            && this.HistogramRecordMinMax == other.HistogramRecordMinMax
            && this.ExponentialHistogramMaxSize == other.ExponentialHistogramMaxSize
            && this.ExponentialHistogramMaxScale == other.ExponentialHistogramMaxScale
            && StringArrayComparer.Equals(this.TagKeys, other.TagKeys)
            && HistogramBoundsEqual(this.HistogramBucketBounds, other.HistogramBucketBounds);
    }

    public override readonly int GetHashCode() => this.hashCode;

    private static double[]? GetExplicitBucketHistogramBounds(Instrument instrument, MetricStreamConfiguration? metricStreamConfiguration)
    {
        if (metricStreamConfiguration is ExplicitBucketHistogramConfiguration explicitBucketHistogramConfiguration
            && explicitBucketHistogramConfiguration.CopiedBoundaries != null)
        {
            return explicitBucketHistogramConfiguration.CopiedBoundaries;
        }

        return instrument switch
        {
            Histogram<long> longHistogram => GetExplicitBucketHistogramBoundsFromAdvice(longHistogram),
            Histogram<int> intHistogram => GetExplicitBucketHistogramBoundsFromAdvice(intHistogram),
            Histogram<short> shortHistogram => GetExplicitBucketHistogramBoundsFromAdvice(shortHistogram),
            Histogram<byte> byteHistogram => GetExplicitBucketHistogramBoundsFromAdvice(byteHistogram),
            Histogram<float> floatHistogram => GetExplicitBucketHistogramBoundsFromAdvice(floatHistogram),
            Histogram<double> doubleHistogram => GetExplicitBucketHistogramBoundsFromAdvice(doubleHistogram),
            _ => null,
        };
    }

    private static double[]? GetExplicitBucketHistogramBoundsFromAdvice<T>(Histogram<T> histogram) where T : struct
    {
        var adviceExplicitBucketBoundaries = histogram.Advice?.HistogramBucketBoundaries;
        if (adviceExplicitBucketBoundaries == null)
        {
            return null;
        }

        if (typeof(T) == typeof(double))
        {
            return ((IReadOnlyList<double>)adviceExplicitBucketBoundaries).ToArray();
        }
        else
        {
            double[] explicitBucketBoundaries = new double[adviceExplicitBucketBoundaries.Count];

            for (int i = 0; i < adviceExplicitBucketBoundaries.Count; i++)
            {
                explicitBucketBoundaries[i] = Convert.ToDouble(adviceExplicitBucketBoundaries[i], CultureInfo.InvariantCulture);
            }

            return explicitBucketBoundaries;
        }
    }

    private static bool HistogramBoundsEqual(double[]? bounds1, double[]? bounds2)
    {
        if (ReferenceEquals(bounds1, bounds2))
            return true;

        if (ReferenceEquals(bounds1, null) || ReferenceEquals(bounds2, null))
            return false;

        var len1 = bounds1.Length;

        if (len1 != bounds2.Length)
            return false;

        for (int i = 0; i < len1; i++)
        {
            if (!bounds1[i].Equals(bounds2[i]))
                return false;
        }

        return true;
    }
}
//-------------------------------------------Ʌ

//-----------------------V
public struct MetricPoint
{
    internal int ReferenceCount;

    private const int DefaultSimpleReservoirPoolSize = 1;

    private readonly AggregatorStore aggregatorStore;

    private readonly AggregationType aggType;

    private MetricPointOptionalComponents? mpComponents;

    // Represents temporality adjusted "value" for double/long metric types or "count" when histogram
    private MetricPointValueStorage runningValue;

    // Represents either "value" for double/long metric types or "count" when histogram
    private MetricPointValueStorage snapshotValue;

    private MetricPointValueStorage deltaLastValue;

    internal MetricPoint(
        AggregatorStore aggregatorStore,
        AggregationType aggType,
        KeyValuePair<string, object?>[]? tagKeysAndValues,
        double[] histogramExplicitBounds,
        int exponentialHistogramMaxSize,
        int exponentialHistogramMaxScale,
        LookupData? lookupData = null)
    {
        this.aggType = aggType;
        this.Tags = new ReadOnlyTagCollection(tagKeysAndValues);
        this.runningValue = default;
        this.snapshotValue = default;
        this.deltaLastValue = default;
        this.MetricPointStatus = MetricPointStatus.NoCollectPending;
        this.ReferenceCount = 1;
        this.LookupData = lookupData;

        var isExemplarEnabled = aggregatorStore!.IsExemplarEnabled();

        ExemplarReservoir? reservoir;
        try
        {
            reservoir = isExemplarEnabled ? aggregatorStore.ExemplarReservoirFactory?.Invoke() : null;
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricViewException("ExemplarReservoirFactory", ex);
            reservoir = null;
        }

        if (this.aggType == AggregationType.HistogramWithBuckets ||
            this.aggType == AggregationType.HistogramWithMinMaxBuckets)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.HistogramBuckets = new HistogramBuckets(histogramExplicitBounds);
            if (isExemplarEnabled && reservoir == null)
            {
                reservoir = new AlignedHistogramBucketExemplarReservoir(histogramExplicitBounds!.Length);
            }
        }
        else if (this.aggType == AggregationType.Histogram || this.aggType == AggregationType.HistogramWithMinMax)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.HistogramBuckets = new HistogramBuckets(null);
        }
        else if (this.aggType == AggregationType.Base2ExponentialHistogram || this.aggType == AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            this.mpComponents = new MetricPointOptionalComponents();
            this.mpComponents.Base2ExponentialBucketHistogram = new Base2ExponentialBucketHistogram(exponentialHistogramMaxSize, exponentialHistogramMaxScale);
            if (isExemplarEnabled && reservoir == null)
            {
                reservoir = new SimpleFixedSizeExemplarReservoir(Math.Min(20, exponentialHistogramMaxSize));
            }
        }
        else
        {
            this.mpComponents = null;
        }

        if (isExemplarEnabled && reservoir == null)
        {
            reservoir = new SimpleFixedSizeExemplarReservoir(DefaultSimpleReservoirPoolSize);
        }

        if (reservoir != null)
        {
            if (this.mpComponents == null)
            {
                this.mpComponents = new MetricPointOptionalComponents();
            }

            reservoir.Initialize(aggregatorStore);

            this.mpComponents.ExemplarReservoir = reservoir;
        }

        // Note: Intentionally set last because this is used to detect valid MetricPoints.
        this.aggregatorStore = aggregatorStore;
    }

    public readonly ReadOnlyTagCollection Tags { get; }

    public readonly DateTimeOffset StartTime => this.aggregatorStore.StartTimeExclusive;
    public readonly DateTimeOffset EndTime => this.aggregatorStore.EndTimeInclusive;

    internal MetricPointStatus MetricPointStatus { get; set; }
    
    internal LookupData? LookupData { get; set; }

    internal readonly bool IsInitialized => this.aggregatorStore != null;

    public readonly double GetSumDouble()
    {
        if (this.aggType != AggregationType.DoubleSumIncomingDelta && this.aggType != AggregationType.DoubleSumIncomingCumulative)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetSumDouble));
        }

        return this.snapshotValue.AsDouble;
    }

    public readonly double GetGaugeLastValueDouble()
    {
        if (this.aggType != AggregationType.DoubleGauge)
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetGaugeLastValueDouble));

        return this.snapshotValue.AsDouble;
    }

    public readonly long GetHistogramCount()
    {
        return this.snapshotValue.AsLong;
    }

    public readonly double GetHistogramSum()
    {
        return this.mpComponents!.HistogramBuckets != null
            ? this.mpComponents.HistogramBuckets.SnapshotSum
            : this.mpComponents.Base2ExponentialBucketHistogram!.SnapshotSum;
    }

    public readonly HistogramBuckets GetHistogramBuckets()
    {
        if (this.aggType != AggregationType.HistogramWithBuckets &&
            this.aggType != AggregationType.Histogram &&
            this.aggType != AggregationType.HistogramWithMinMaxBuckets &&
            this.aggType != AggregationType.HistogramWithMinMax)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetHistogramBuckets));
        }

        Debug.Assert(this.mpComponents?.HistogramBuckets != null, "HistogramBuckets was null");

        return this.mpComponents!.HistogramBuckets!;
    }

    public ExponentialHistogramData GetExponentialHistogramData()
    {
        if (this.aggType != AggregationType.Base2ExponentialHistogram &&
            this.aggType != AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            this.ThrowNotSupportedMetricTypeException(nameof(this.GetExponentialHistogramData));
        }

        Debug.Assert(this.mpComponents?.Base2ExponentialBucketHistogram != null, "Base2ExponentialBucketHistogram was null");

        return this.mpComponents!.Base2ExponentialBucketHistogram!.GetExponentialHistogramData();
    }

    public readonly bool TryGetHistogramMinMaxValues(out double min, out double max)
    {
        if (this.aggType == AggregationType.HistogramWithMinMax || this.aggType == AggregationType.HistogramWithMinMaxBuckets)
        {
            min = this.mpComponents!.HistogramBuckets!.SnapshotMin;
            max = this.mpComponents.HistogramBuckets.SnapshotMax;
            return true;
        }

        if (this.aggType == AggregationType.Base2ExponentialHistogramWithMinMax)
        {
            min = this.mpComponents!.Base2ExponentialBucketHistogram!.SnapshotMin;
            max = this.mpComponents.Base2ExponentialBucketHistogram.SnapshotMax;
            return true;
        }

        min = 0;
        max = 0;
        return false;
    }

    public readonly bool TryGetExemplars(out ReadOnlyExemplarCollection exemplars)
    {
        exemplars = this.mpComponents?.Exemplars ?? ReadOnlyExemplarCollection.Empty;
        return exemplars.MaximumCount > 0;
    }

    internal readonly MetricPoint Copy()
    {
        MetricPoint copy = this;
        copy.mpComponents = this.mpComponents?.Copy();
        return copy;
    }

    internal void Update(long number)  // <-------------------------!!
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
            {
                Interlocked.Add(ref this.runningValue.AsLong, number);  // <-----------------------
                break;
            }

            case AggregationType.LongSumIncomingCumulative:
            case AggregationType.LongGauge:
            {
                Interlocked.Exchange(ref this.runningValue.AsLong, number);
                break;
            }

            case AggregationType.Histogram:
            {
                this.UpdateHistogram(number);
                return;
            }

            case AggregationType.HistogramWithMinMax:
            {
                this.UpdateHistogramWithMinMax(number);
                return;
            }

            case AggregationType.HistogramWithBuckets:
            {
                this.UpdateHistogramWithBuckets(number);
                return;
            }

            case AggregationType.HistogramWithMinMaxBuckets:
            {
                this.UpdateHistogramWithBucketsAndMinMax(number);
                return;
            }
            // ...
        }

        this.CompleteUpdate();
    }

    internal void UpdateWithExemplar(long number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
            {
                Interlocked.Add(ref this.runningValue.AsLong, number);
                break;
            }

            case AggregationType.LongSumIncomingCumulative:
            case AggregationType.LongGauge:
            {
                Interlocked.Exchange(ref this.runningValue.AsLong, number);
                break;
            }

            case AggregationType.Histogram:
            {
                this.UpdateHistogram(number, tags, offerExemplar);
                return;
            }

            case AggregationType.HistogramWithMinMax:
            {
                this.UpdateHistogramWithMinMax(number, tags, offerExemplar);
                return;
            }

            case AggregationType.HistogramWithBuckets:
            {
                this.UpdateHistogramWithBuckets(number, tags, offerExemplar);
                return;
            }

            case AggregationType.HistogramWithMinMaxBuckets:
            {
                this.UpdateHistogramWithBucketsAndMinMax(number, tags, offerExemplar);
                return;
            }
            // ...
        }

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    internal void Update(double number)  // <------------------there is no `Update(int number)` method, int is implicitly converted to long before calling Update
    {
        switch (this.aggType)
        {
            case AggregationType.DoubleSumIncomingDelta:
                {
                    InterlockedHelper.Add(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.DoubleSumIncomingCumulative:
            case AggregationType.DoubleGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number);
                    return;
                }
            // ...        
        }

        this.CompleteUpdate();
    }

    internal void UpdateWithExemplar(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        switch (this.aggType)
        {
            case AggregationType.DoubleSumIncomingDelta:
                {
                    InterlockedHelper.Add(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.DoubleSumIncomingCumulative:
            case AggregationType.DoubleGauge:
                {
                    Interlocked.Exchange(ref this.runningValue.AsDouble, number);
                    break;
                }

            case AggregationType.Histogram:
                {
                    this.UpdateHistogram(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    this.UpdateHistogramWithMinMax(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    this.UpdateHistogramWithBuckets(number, tags, offerExemplar);
                    return;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    this.UpdateHistogramWithBucketsAndMinMax(number, tags, offerExemplar);
                    return;
                }
            // ...      
        }

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    internal void TakeSnapshot(bool outputDelta)
    {
        switch (this.aggType)
        {
            case AggregationType.LongSumIncomingDelta:
            case AggregationType.LongSumIncomingCumulative:
                {
                    if (outputDelta)
                    {
                        long initValue = Interlocked.Read(ref this.runningValue.AsLong);
                        this.snapshotValue.AsLong = initValue - this.deltaLastValue.AsLong;
                        this.deltaLastValue.AsLong = initValue;
                        this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                        // Check again if value got updated, if yes reset status.
                        // This ensures no Updates get Lost.
                        if (initValue != Interlocked.Read(ref this.runningValue.AsLong))
                        {
                            this.MetricPointStatus = MetricPointStatus.CollectPending;
                        }
                    }
                    else
                    {
                        this.snapshotValue.AsLong = Interlocked.Read(ref this.runningValue.AsLong);
                    }

                    break;
                }

            case AggregationType.DoubleSumIncomingDelta:
            case AggregationType.DoubleSumIncomingCumulative:
                {
                    if (outputDelta)
                    {
                        double initValue = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                        this.snapshotValue.AsDouble = initValue - this.deltaLastValue.AsDouble;
                        this.deltaLastValue.AsDouble = initValue;
                        this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                        // Check again if value got updated, if yes reset status.
                        // This ensures no Updates get Lost.
                        if (initValue != InterlockedHelper.Read(ref this.runningValue.AsDouble))
                        {
                            this.MetricPointStatus = MetricPointStatus.CollectPending;
                        }
                    }
                    else
                    {
                        this.snapshotValue.AsDouble = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                    }

                    break;
                }

            case AggregationType.LongGauge:
                {
                    this.snapshotValue.AsLong = Interlocked.Read(ref this.runningValue.AsLong);
                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    // Check again if value got updated, if yes reset status.
                    // This ensures no Updates get Lost.
                    if (this.snapshotValue.AsLong != Interlocked.Read(ref this.runningValue.AsLong))
                    {
                        this.MetricPointStatus = MetricPointStatus.CollectPending;
                    }

                    break;
                }

            case AggregationType.DoubleGauge:
                {
                    this.snapshotValue.AsDouble = InterlockedHelper.Read(ref this.runningValue.AsDouble);
                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    // Check again if value got updated, if yes reset status.
                    // This ensures no Updates get Lost.
                    if (this.snapshotValue.AsDouble != InterlockedHelper.Read(ref this.runningValue.AsDouble))
                    {
                        this.MetricPointStatus = MetricPointStatus.CollectPending;
                    }

                    break;
                }

            case AggregationType.HistogramWithBuckets:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                    }

                    histogramBuckets.Snapshot(outputDelta);

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Histogram:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.HistogramWithMinMaxBuckets:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;
                    histogramBuckets.SnapshotMin = histogramBuckets.RunningMin;
                    histogramBuckets.SnapshotMax = histogramBuckets.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                        histogramBuckets.RunningMin = double.PositiveInfinity;
                        histogramBuckets.RunningMax = double.NegativeInfinity;
                    }

                    histogramBuckets.Snapshot(outputDelta);

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.HistogramWithMinMax:
                {
                    var histogramBuckets = this.mpComponents!.HistogramBuckets!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogramBuckets.SnapshotSum = histogramBuckets.RunningSum;
                    histogramBuckets.SnapshotMin = histogramBuckets.RunningMin;
                    histogramBuckets.SnapshotMax = histogramBuckets.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogramBuckets.RunningSum = 0;
                        histogramBuckets.RunningMin = double.PositiveInfinity;
                        histogramBuckets.RunningMax = double.NegativeInfinity;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Base2ExponentialHistogram:
                {
                    var histogram = this.mpComponents!.Base2ExponentialBucketHistogram!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogram.SnapshotSum = histogram.RunningSum;
                    histogram.Snapshot();

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogram.RunningSum = 0;
                        histogram.Reset();
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }

            case AggregationType.Base2ExponentialHistogramWithMinMax:
                {
                    var histogram = this.mpComponents!.Base2ExponentialBucketHistogram!;

                    this.mpComponents.AcquireLock();

                    this.snapshotValue.AsLong = this.runningValue.AsLong;
                    histogram.SnapshotSum = histogram.RunningSum;
                    histogram.Snapshot();
                    histogram.SnapshotMin = histogram.RunningMin;
                    histogram.SnapshotMax = histogram.RunningMax;

                    if (outputDelta)
                    {
                        this.runningValue.AsLong = 0;
                        histogram.RunningSum = 0;
                        histogram.Reset();
                        histogram.RunningMin = double.PositiveInfinity;
                        histogram.RunningMax = double.NegativeInfinity;
                    }

                    this.MetricPointStatus = MetricPointStatus.NoCollectPending;

                    this.mpComponents.ReleaseLock();

                    break;
                }
        }
    }

    internal void TakeSnapshotWithExemplar(bool outputDelta)
    {
        this.TakeSnapshot(outputDelta);

        this.mpComponents.Exemplars = this.mpComponents.ExemplarReservoir!.Collect();
    }

    internal void NullifyMetricPointState()
    {
        this.LookupData = null;
        this.mpComponents = null;
    }

    private void UpdateHistogram(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets!;

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
        }

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets!;

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
        }

        histogramBuckets.RunningMin = Math.Min(histogramBuckets.RunningMin, number);
        histogramBuckets.RunningMax = Math.Max(histogramBuckets.RunningMax, number);

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithBuckets(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets;

        int bucketIndex = histogramBuckets!.FindBucketIndex(number);

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
            histogramBuckets.BucketCounts[bucketIndex].RunningValue++;
        }

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar, bucketIndex);

        this.CompleteUpdate();
    }

    private void UpdateHistogramWithBucketsAndMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false)
    {
        var histogramBuckets = this.mpComponents!.HistogramBuckets;

        int bucketIndex = histogramBuckets!.FindBucketIndex(number);

        this.mpComponents.AcquireLock();

        unchecked
        {
            this.runningValue.AsLong++;
            histogramBuckets.RunningSum += number;
            histogramBuckets.BucketCounts[bucketIndex].RunningValue++;
        }

        histogramBuckets.RunningMin = Math.Min(histogramBuckets.RunningMin, number);
        histogramBuckets.RunningMax = Math.Max(histogramBuckets.RunningMax, number);

        this.mpComponents.ReleaseLock();

        this.UpdateExemplar(number, tags, offerExemplar, bucketIndex);

        this.CompleteUpdate();
    }

    private void UpdateBase2ExponentialHistogram(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false);

    private void UpdateBase2ExponentialHistogramWithMinMax(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags = default, bool offerExemplar = false);

    private readonly void UpdateExemplar(long number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar)
    {
        if (offerExemplar)
        {
            this.mpComponents!.ExemplarReservoir!.Offer(new ExemplarMeasurement<long>(number, tags));
        }
    }

    private readonly void UpdateExemplar(double number, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool offerExemplar, int explicitBucketHistogramBucketIndex = -1)
    {
        if (offerExemplar)
        {
            this.mpComponents!.ExemplarReservoir!.Offer(new ExemplarMeasurement<double>(number, tags, explicitBucketHistogramBucketIndex));
        }
    }

    private void CompleteUpdate()
    {
        this.MetricPointStatus = MetricPointStatus.CollectPending;

        this.CompleteUpdateWithoutMeasurement();
    }

    private void CompleteUpdateWithoutMeasurement()
    {
        if (this.aggregatorStore.OutputDelta)
        {
            Interlocked.Decrement(ref this.ReferenceCount);
        }
    }
}
//-----------------------Ʌ

//------------------------------V
public abstract class Instrument
{
    internal readonly DiagLinkedList<ListenerSubscription> _subscriptions = new DiagLinkedList<ListenerSubscription>();

    protected Instrument(Meter meter, string name, string? unit = default, string? description = default, IEnumerable<KeyValuePair<string, object?>>? tags = default)
    {
        Meter = meter; Name = name; Description = description; Unit = unit;
    }

    protected void Publish()
    {
        List<MeterListener>? allListeners = null;
        lock (Instrument.SyncObject)
        {
            if (Meter.Disposed || !Meter.AddInstrument(this)) 
            {
                return;
            }

            allListeners = MeterListener.GetAllListeners();
        }

        if (allListeners is not null)
        {
            foreach (MeterListener listener in allListeners)
            {
                listener.InstrumentPublished?.Invoke(this, listener); 
            }
        }
    }

    public Meter Meter { get; } 

    public bool Enabled => _subscriptions.First is not null;

    internal static void ValidateTypeParameter<T>();
   
    internal object? EnableMeasurement(ListenerSubscription subscription, out bool oldStateStored)  
    {
        oldStateStored = false;

        if (!_subscriptions.AddIfNotExist(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener)))
        {
            ListenerSubscription oldSubscription = _subscriptions.Remove(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener));
            _subscriptions.AddIfNotExist(subscription, (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener));
            oldStateStored = object.ReferenceEquals(oldSubscription.Listener, subscription.Listener);
            return oldSubscription.State;
        }

        return false;
    }

    internal object? DisableMeasurements(MeterListener listener)  // called from MeterListener.DisableMeasurementEvents
        => _subscriptions.Remove(new ListenerSubscription(listener), (s1, s2) => object.ReferenceEquals(s1.Listener, s2.Listener)).State;

    internal virtual void Observe(MeterListener listener) => throw new InvalidOperationException();

    internal object? GetSubscriptionState(MeterListener listener)
    {
        DiagNode<ListenerSubscription>? current = _subscriptions.First;
        while (current is not null)
        {
            if (object.ReferenceEquals(listener, current.Value.Listener))
            {
                return current.Value.State;
            }
            current = current.Next;
        }

        return null;
    }
}
//------------------------------Ʌ

//---------------------------------------------V
public sealed class MeterListener : IDisposable
{
    private static readonly List<MeterListener> s_allStartedListeners = new List<MeterListener>();
    private readonly DiagLinkedList<Instrument> _enabledMeasurementInstruments = new DiagLinkedList<Instrument>();

    // initialize all measurement callback with no-op operations so we'll avoid the null checks during the execution;
    private MeasurementCallback<byte> _byteMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<int> _intMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    private MeasurementCallback<long> _longMeasurementCallback = (instrument, measurement, tags, state) => { /* no-op */ };
    // <-----------------------smec set those callbacks

    public MeterListener() 
    { 
        RuntimeMetrics.EnsureInitialized(); 
    }

    public Action<Instrument, MeterListener>? InstrumentPublished { get; set; }

    public Action<Instrument, object?>? MeasurementsCompleted { get; set; }
                                                                                                                               
    public void EnableMeasurementEvents(Instrument instrument, object? state = null)   // <----------------------------met2.5.0
    {                                                               // state is MetricState
        bool oldStateStored = false;
        bool enabled = false;
        object? oldState = null;

        lock (Instrument.SyncObject)
        {
            if (instrument is not null && !_disposed && !instrument.Meter.Disposed)
            {
                _enabledMeasurementInstruments.AddIfNotExist(instrument, object.ReferenceEquals);
                oldState = instrument.EnableMeasurement(new ListenerSubscription(this, state), out oldStateStored);
                enabled = true;
            }
        }

        /*
            internal static void MeasurementsCompleted(Instrument instrument, object? state)
            {
                if (state is not MetricState metricState)
                    return;

                metricState.CompleteMeasurement();
            }
        */
        if (enabled)
        {
            if (oldStateStored && MeasurementsCompleted is not null)
            {
                MeasurementsCompleted?.Invoke(instrument!, oldState);
            }
        }
        else
        {
            MeasurementsCompleted?.Invoke(instrument!, state);
        }
    }

    public object? DisableMeasurementEvents(Instrument instrument)
    {
        if (!Meter.IsSupported)
        {
            return default;
        }

        object? state = null;
        lock (Instrument.SyncObject)
        {
            if (instrument is null || _enabledMeasurementInstruments.Remove(instrument, object.ReferenceEquals) == default)
            {
                return default;
            }

            state = instrument.DisableMeasurements(this);
        }

        MeasurementsCompleted?.Invoke(instrument, state);
        return state;
    }

    public void SetMeasurementEventCallback<T>(MeasurementCallback<T>? measurementCallback) where T : struct
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        measurementCallback ??= (instrument, measurement, tags, state) => { /* no-op */};

        if (typeof(T) == typeof(byte))
            _byteMeasurementCallback = (MeasurementCallback<byte>)(object)measurementCallback;
        else if (typeof(T) == typeof(int))
            _intMeasurementCallback = (MeasurementCallback<int>)(object)measurementCallback;
        // ...
    }

    public void Start()
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        List<Instrument>? publishedInstruments = null;
        lock (Instrument.SyncObject)
        {
            if (_disposed)
            {
                return;
            }

            if (!s_allStartedListeners.Contains(this))
            {
                s_allStartedListeners.Add(this);  // <------------------------------
                publishedInstruments = Meter.GetPublishedInstruments();
            }
        }

        if (publishedInstruments is not null)
        {
            foreach (Instrument instrument in publishedInstruments)
            {
                InstrumentPublished?.Invoke(instrument, this);
            }
        }
    }

    public void RecordObservableInstruments()
    {
        if (!Meter.IsSupported)
        {
            return;
        }

        List<Exception>? exceptionsList = null;
        DiagNode<Instrument>? current = _enabledMeasurementInstruments.First;
        while (current is not null)
        {
            if (current.Value.IsObservable)
            {
                try
                {
                    current.Value.Observe(this);
                }
                catch (Exception e)
                {
                    exceptionsList ??= new List<Exception>();
                    exceptionsList.Add(e);
                }
            }

            current = current.Next;
        }

        if (exceptionsList is not null)
        {
            throw new AggregateException(exceptionsList);
        }
    }

    internal static List<MeterListener>? GetAllListeners() => s_allStartedListeners.Count == 0 ? null : new List<MeterListener>(s_allStartedListeners);

    internal void NotifyMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags,  object? state) where T : struct
    {
        if (typeof(T) == typeof(byte))
            _byteMeasurementCallback(instrument, (byte)(object)measurement, tags, state);
        if (typeof(T) == typeof(int))
            _intMeasurementCallback(instrument, (int)(object)measurement, tags, state);   // _intMeasurementCallback set by MeterProviderSdk
        // ...

        /*
            internal static void MeasurementRecordedLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            {
                if (state is not MetricState metricState)
                {
                    OpenTelemetrySdkEventSource.Log.MeasurementDropped(instrument?.Name ?? "UnknownInstrument", "SDK internal error occurred.", "Contact SDK owners.");
                    return;
                }

                metricState.RecordMeasurementLong(value, tags);
            }
        */
    }
}

internal readonly struct ListenerSubscription
{
    internal ListenerSubscription(MeterListener listener, object? state = null)
    {
        Listener = listener;
        State = state;
    }

    internal MeterListener Listener { get; }
    internal object? State { get; }
}
//---------------------------------------------Ʌ
```