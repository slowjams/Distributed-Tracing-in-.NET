## OpenTelemetry Metrics

```C#
//-----------------------------------------------V
var builder = WebApplication.CreateBuilder(args);
// ...
builder.Services.AddOpenTelemetry().WithMetrics(opts => opts
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BookStore.WebApi"))
    .AddMeter(builder.Configuration.GetValue<string>("BookStoreMeterName"))
    .AddAspNetCoreInstrumentation()
    .AddProcessInstrumentation()
    .AddRuntimeInstrumentation()
    .AddView(
        instrumentName: "orders-price",
        new ExplicitBucketHistogramConfiguration { Boundaries = [15, 30, 45, 60, 75] })
    .AddView(
        instrumentName: "orders-number-of-books",
        new ExplicitBucketHistogramConfiguration { Boundaries = [1, 2, 5] })
    .AddConsoleExporter()
    .AddOtlpExporter(options  =>
    {
        options.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"] ?? throw new InvalidOperationException());
    }));

var app = builder.Build();
//-----------------------------------------------Ʌ
```

```C#
//---------------------------V
public class BookStoreMetrics
{
    //Books meters
    private  Counter<int> BooksAddedCounter { get; }
    private  Counter<int> BooksDeletedCounter { get; }
    private  Counter<int> BooksUpdatedCounter { get; }
    private  UpDownCounter<int> TotalBooksUpDownCounter { get; }

    //Categories meters
    private Counter<int> CategoriesAddedCounter { get; }
    private Counter<int> CategoriesDeletedCounter { get; }
    private Counter<int> CategoriesUpdatedCounter { get; }
    private ObservableGauge<int> TotalCategoriesGauge { get; }
    private int _totalCategories = 0;

    //Order meters
    private Histogram<double> OrdersPriceHistogram { get; }
    private Histogram<int> NumberOfBooksPerOrderHistogram { get; }
    private ObservableCounter<int> OrdersCanceledCounter { get; }
    private int _ordersCanceled = 0;
    private Counter<int> TotalOrdersCounter { get; }

    public BookStoreMetrics(IMeterFactory meterFactory, IConfiguration configuration)
    {
        var meter = meterFactory.Create(configuration["BookStoreMeterName"] ?? throw new NullReferenceException("BookStore meter missing a name"));

        BooksAddedCounter = meter.CreateCounter<int>("books-added", "Book");
        BooksDeletedCounter = meter.CreateCounter<int>("books-deleted", "Book");
        BooksUpdatedCounter = meter.CreateCounter<int>("books-updated", "Book");
        TotalBooksUpDownCounter = meter.CreateUpDownCounter<int>("total-books", "Book");
        
        CategoriesAddedCounter = meter.CreateCounter<int>("categories-added", "Category");
        CategoriesDeletedCounter = meter.CreateCounter<int>("categories-deleted", "Category");
        CategoriesUpdatedCounter = meter.CreateCounter<int>("categories-updated", "Category");
        TotalCategoriesGauge = meter.CreateObservableGauge<int>("total-categories", () => _totalCategories);

        OrdersPriceHistogram = meter.CreateHistogram<double>("orders-price", "Euros", "Price distribution of book orders");
        NumberOfBooksPerOrderHistogram = meter.CreateHistogram<int>("orders-number-of-books", "Books", "Number of books per order");
        OrdersCanceledCounter = meter.CreateObservableCounter<int>("orders-canceled", () => _ordersCanceled);
        TotalOrdersCounter = meter.CreateCounter<int>("total-orders", "Orders");
    }


    //Books meters
    public void AddBook() => BooksAddedCounter.Add(1);
    public void DeleteBook() => BooksDeletedCounter.Add(1);
    public void UpdateBook() => BooksUpdatedCounter.Add(1);
    public void IncreaseTotalBooks() => TotalBooksUpDownCounter.Add(1);
    public void DecreaseTotalBooks() => TotalBooksUpDownCounter.Add(-1);

    //Categories meters
    public void AddCategory() => CategoriesAddedCounter.Add(1);
    public void DeleteCategory() => CategoriesDeletedCounter.Add(1);
    public void UpdateCategory() => CategoriesUpdatedCounter.Add(1);
    public void IncreaseTotalCategories() => _totalCategories++;
    public void DecreaseTotalCategories() => _totalCategories--;

    //Orders meters
    public void RecordOrderTotalPrice(double price) => OrdersPriceHistogram.Record(price);
    public void RecordNumberOfBooks(int amount) => NumberOfBooksPerOrderHistogram.Record(amount);
    public void IncreaseOrdersCanceled() => _ordersCanceled++;
    public void IncreaseTotalOrders(string city) => TotalOrdersCounter.Add(1, KeyValuePair.Create<string, object>("City", city));
}
//---------------------------Ʌ

//-------------------------------------------------------------------------------V
public class OrderRepository(BookStoreDbContext context, BookStoreMetrics meters) : Repository<Order>(context), IOrderRepository
{
    public override async Task<Order> GetById(int id)
    {
        return await Db.Orders
            .Include(b => b.Books)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public override async Task<List<Order>> GetAll()
    {
        return await Db.Orders
            .Include(b => b.Books)
            .ToListAsync();
    }

    public override async Task Add(Order entity)
    {
        DbSet.Add(entity);
        await base.SaveChanges();

        meters.RecordOrderTotalPrice(entity.TotalAmount);
        meters.RecordNumberOfBooks(entity.Books.Count);
        meters.IncreaseTotalOrders(entity.City);
    }

    public override async Task Update(Order entity)
    {
        await base.Update(entity);

        meters.IncreaseOrdersCanceled();
    }
}
//-------------------------------------------------------------------------------Ʌ
```


========================================================================

```C#
//--------------------------------------V
public sealed class OpenTelemetryBuilder : IOpenTelemetryBuilder
{
    internal OpenTelemetryBuilder(IServiceCollection services)
    {
        services.AddOpenTelemetrySharedProviderBuilderServices();

        this.Services = services;
    }

    public IServiceCollection Services { get; }

    public OpenTelemetryBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        OpenTelemetryBuilderSdkExtensions.ConfigureResource(this, configure);
        return this;
    }

    public OpenTelemetryBuilder WithMetrics()
        => this.WithMetrics(b => { });

    public OpenTelemetryBuilder WithMetrics(Action<MeterProviderBuilder> configure)   // <--------------------------------
    {
        OpenTelemetryBuilderSdkExtensions.WithMetrics(this, configure);
        return this;
    }   
    // ...
}
//--------------------------------------Ʌ

//---------------------------------------------------V
public static class OpenTelemetryBuilderSdkExtensions
{
    public static IOpenTelemetryBuilder ConfigureResource(this IOpenTelemetryBuilder builder, Action<ResourceBuilder> configure)
    {
        builder.Services.ConfigureOpenTelemetryMeterProvider(
            builder => builder.ConfigureResource(configure));

        builder.Services.ConfigureOpenTelemetryTracerProvider(
            builder => builder.ConfigureResource(configure));

        builder.Services.ConfigureOpenTelemetryLoggerProvider(
            builder => builder.ConfigureResource(configure));

        return builder;
    }

    public static IOpenTelemetryBuilder WithMetrics(this IOpenTelemetryBuilder builder)
        => WithMetrics(builder, b => { });

    public static IOpenTelemetryBuilder WithMetrics(this IOpenTelemetryBuilder builder, Action<MeterProviderBuilder> configure)
    {
        OpenTelemetryMetricsBuilderExtensions.RegisterMetricsListener(
            builder.Services,
            configure);

        return builder;
    }
    // ...
}
//---------------------------------------------------Ʌ

//---------------------------------------------------------V
internal static class OpenTelemetryMetricsBuilderExtensions
{
    public static IMetricsBuilder UseOpenTelemetry(this IMetricsBuilder metricsBuilder)
        => UseOpenTelemetry(metricsBuilder, b => { });

    public static IMetricsBuilder UseOpenTelemetry(this IMetricsBuilder metricsBuilder, Action<MeterProviderBuilder> configure)
    {
        RegisterMetricsListener(metricsBuilder.Services, configure);

        return metricsBuilder;
    }

    internal static void RegisterMetricsListener(IServiceCollection services, Action<MeterProviderBuilder> configure)
    {
        var builder = new MeterProviderBuilderBase(services!);

        services!.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMetricsListener, OpenTelemetryMetricsListener>());

        configure(builder);
    }
}
//---------------------------------------------------------Ʌ

//------------------------------------------------V
public static class MeterProviderBuilderExtensions
{
    public static MeterProviderBuilder AddReader(this MeterProviderBuilder meterProviderBuilder, MetricReader reader)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.AddReader(reader);
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder AddReader<T>(this MeterProviderBuilder meterProviderBuilder) where T : MetricReader
    {
        meterProviderBuilder.ConfigureServices(services => services.TryAddSingleton<T>());

        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.AddReader(sp.GetRequiredService<T>());
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder AddReader(this MeterProviderBuilder meterProviderBuilder, Func<IServiceProvider, MetricReader> implementationFactory)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.AddReader(implementationFactory(sp));
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder AddView(this MeterProviderBuilder meterProviderBuilder, string instrumentName, string name)
    {
        if (!MeterProviderBuilderSdk.IsValidInstrumentName(name))
            throw new ArgumentException($"Custom view name {name} is invalid.", nameof(name));

        if (instrumentName.Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Instrument selection criteria is invalid. Instrument name '{instrumentName}' " +
                $"contains a wildcard character. This is not allowed when using a view to " + $"rename a metric stream as it would lead to conflicting metric stream names.",
                nameof(instrumentName));
        }

        meterProviderBuilder.AddView(instrumentName, new MetricStreamConfiguration { Name = name });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder AddView(this MeterProviderBuilder meterProviderBuilder, string instrumentName, MetricStreamConfiguration metricStreamConfiguration)
    {
        if (metricStreamConfiguration.Name != null && instrumentName.Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Instrument selection criteria is invalid. Instrument name '{instrumentName}' " +
                $"contains a wildcard character. This is not allowed when using a view to " +  $"rename a metric stream as it would lead to conflicting metric stream names.",
                nameof(instrumentName));
        }

        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
            {
                if (instrumentName.Contains('*', StringComparison.Ordinal))
                {
                    var pattern = '^' + Regex.Escape(instrumentName).Replace("\\*", ".*", StringComparison.Ordinal);

                    var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    meterProviderBuilderSdk.AddView(instrument => regex.IsMatch(instrument.Name) ? metricStreamConfiguration : null);
                }
                else
                {
                    meterProviderBuilderSdk.AddView(instrument => instrument.Name.Equals(instrumentName, StringComparison.OrdinalIgnoreCase) ? metricStreamConfiguration : null);
                }
            }
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder AddView(this MeterProviderBuilder meterProviderBuilder, Func<Instrument, MetricStreamConfiguration?> viewConfig)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.AddView(viewConfig);
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder SetMaxMetricStreams(this MeterProviderBuilder meterProviderBuilder, int maxMetricStreams)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
            {
                meterProviderBuilderSdk.SetMetricLimit(maxMetricStreams);
            }
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder SetMaxMetricPointsPerMetricStream(this MeterProviderBuilder meterProviderBuilder, int maxMetricPointsPerMetricStream)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.SetDefaultCardinalityLimit(maxMetricPointsPerMetricStream);
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder SetResourceBuilder(this MeterProviderBuilder meterProviderBuilder, ResourceBuilder resourceBuilder)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.SetResourceBuilder(resourceBuilder);
        });

        return meterProviderBuilder;
    }

    public static MeterProviderBuilder ConfigureResource(this MeterProviderBuilder meterProviderBuilder, Action<ResourceBuilder> configure)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
                meterProviderBuilderSdk.ConfigureResource(configure);
        });

        return meterProviderBuilder;
    }

    public static MeterProvider Build(this MeterProviderBuilder meterProviderBuilder)
    {
        if (meterProviderBuilder is MeterProviderBuilderBase meterProviderBuilderBase)
            return meterProviderBuilderBase.InvokeBuild();

        throw new NotSupportedException($"Build is not supported on '{meterProviderBuilder?.GetType().FullName ?? "null"}' instances.");
    }

    public static MeterProviderBuilder SetExemplarFilter(this MeterProviderBuilder meterProviderBuilder, ExemplarFilterType exemplarFilter)
    {
        meterProviderBuilder.ConfigureBuilder((sp, builder) =>
        {
            if (builder is MeterProviderBuilderSdk meterProviderBuilderSdk)
            {
                switch (exemplarFilter)
                {
                    case ExemplarFilterType.AlwaysOn:
                    case ExemplarFilterType.AlwaysOff:
                    case ExemplarFilterType.TraceBased:
                        meterProviderBuilderSdk.SetExemplarFilter(exemplarFilter);
                        break;
                    default:
                        throw new NotSupportedException($"ExemplarFilterType '{exemplarFilter}' is not supported.");
                }
            }
        });

        return meterProviderBuilder;
    }
}
//------------------------------------------------Ʌ

//----------------------------------------------V
public static class OtlpMetricExporterExtensions
{
    public static MeterProviderBuilder AddOtlpExporter(this MeterProviderBuilder builder)
        => AddOtlpExporter(builder, name: null, configure: null);

    public static MeterProviderBuilder AddOtlpExporter(this MeterProviderBuilder builder, Action<OtlpExporterOptions> configure)
        => AddOtlpExporter(builder, name: null, configure);

    public static MeterProviderBuilder AddOtlpExporter(this MeterProviderBuilder builder, string? name, Action<OtlpExporterOptions>? configure)
    {
        var finalOptionsName = name ?? Options.DefaultName;

        builder.ConfigureServices(services =>
        {
            if (name != null && configure != null)
            {
                // if we are using named options we register the configuration delegate into options pipeline.
                services.Configure(finalOptionsName, configure);
            }

            services.AddOtlpExporterMetricsServices(finalOptionsName);
        });

        return builder.AddReader(sp =>
        {
            OtlpExporterOptions exporterOptions;

            if (name == null)
            {
                exporterOptions = sp.GetRequiredService<IOptionsFactory<OtlpExporterOptions>>().Create(finalOptionsName);

                configure?.Invoke(exporterOptions);
            }
            else
            {
                exporterOptions = sp.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(finalOptionsName);
            }

            return BuildOtlpExporterMetricReader(
                sp,
                exporterOptions,
                sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(finalOptionsName),
                sp.GetRequiredService<IOptionsMonitor<ExperimentalOptions>>().Get(finalOptionsName));
        });
    }

    public static MeterProviderBuilder AddOtlpExporter(this MeterProviderBuilder builder, Action<OtlpExporterOptions, MetricReaderOptions> configureExporterAndMetricReader)
        => AddOtlpExporter(builder, name: null, configureExporterAndMetricReader);

    public static MeterProviderBuilder AddOtlpExporter(this MeterProviderBuilder builder, string? name,
        Action<OtlpExporterOptions, MetricReaderOptions>? configureExporterAndMetricReader)
    {
        var finalOptionsName = name ?? Options.DefaultName;

        builder.ConfigureServices(services =>
        {
            services.AddOtlpExporterMetricsServices(finalOptionsName);
        });

        return builder.AddReader(sp =>
        {
            OtlpExporterOptions exporterOptions;
            if (name == null)
                exporterOptions = sp.GetRequiredService<IOptionsFactory<OtlpExporterOptions>>().Create(finalOptionsName);
            else
                exporterOptions = sp.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(finalOptionsName);

            var metricReaderOptions = sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(finalOptionsName);

            configureExporterAndMetricReader?.Invoke(exporterOptions, metricReaderOptions);

            return BuildOtlpExporterMetricReader(
                sp,
                exporterOptions,
                metricReaderOptions,
                sp.GetRequiredService<IOptionsMonitor<ExperimentalOptions>>().Get(finalOptionsName));
        });
    }

    internal static MetricReader BuildOtlpExporterMetricReader(
        IServiceProvider serviceProvider,
        OtlpExporterOptions exporterOptions,
        MetricReaderOptions metricReaderOptions,
        ExperimentalOptions experimentalOptions,
        bool skipUseOtlpExporterRegistrationCheck = false,
        Func<BaseExporter<Metric>, BaseExporter<Metric>>? configureExporterInstance = null)
    {
        if (exporterOptions!.Protocol == OtlpExportProtocol.Grpc && ReferenceEquals(exporterOptions.HttpClientFactory, exporterOptions.DefaultHttpClientFactory))
        {
            throw new NotSupportedException("OtlpExportProtocol.Grpc with the default HTTP client factory is not supported on .NET Framework or .NET Standard 2.0." +
                "Please switch to OtlpExportProtocol.HttpProtobuf or provide a custom HttpClientFactory.");
        }

        if (!skipUseOtlpExporterRegistrationCheck)
            serviceProvider!.EnsureNoUseOtlpExporterRegistrations();

        exporterOptions!.TryEnableIHttpClientFactoryIntegration(serviceProvider!, "OtlpMetricExporter");

        BaseExporter<Metric> metricExporter = new OtlpMetricExporter(exporterOptions!, experimentalOptions!);

        if (configureExporterInstance != null)
        {
            metricExporter = configureExporterInstance(metricExporter);
        }

        return PeriodicExportingMetricReaderHelper.CreatePeriodicExportingMetricReader(metricExporter, metricReaderOptions!);   // use PeriodicExportingMetricReader
    }
}
//----------------------------------------------Ʌ

//----------------------------------------------------V
public class OtlpMetricExporter : BaseExporter<Metric>
{
    private const int GrpcStartWritePosition = 5;
    private readonly OtlpExporterTransmissionHandler transmissionHandler;
    private readonly int startWritePosition;

    private Resource? resource;

    // Initial buffer size set to ~732KB.
    // This choice allows us to gradually grow the buffer while targeting a final capacity of around 100 MB,
    // by the 7th doubling to maintain efficient allocation without frequent resizing.
    private byte[] buffer = new byte[750000];

    public OtlpMetricExporter(OtlpExporterOptions options) : this(options, experimentalOptions: new(), transmissionHandler: null) { }

    internal OtlpMetricExporter(OtlpExporterOptions exporterOptions, ExperimentalOptions experimentalOptions, OtlpExporterTransmissionHandler? transmissionHandler = null)
    {
        this.startWritePosition = exporterOptions!.Protocol == OtlpExportProtocol.Grpc ? GrpcStartWritePosition : 0;
        this.transmissionHandler = transmissionHandler ?? exporterOptions!.GetExportTransmissionHandler(experimentalOptions!, OtlpSignalType.Metrics);
    }

    internal Resource Resource => this.resource ??= this.ParentProvider.GetResource();

    public override ExportResult Export(in Batch<Metric> metrics)
    {
        // Prevents the exporter's gRPC and HTTP operations from being instrumented.
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            int writePosition = ProtobufOtlpMetricSerializer.WriteMetricsData(ref this.buffer, this.startWritePosition, this.Resource, metrics);

            if (this.startWritePosition == GrpcStartWritePosition)
            {
                // Grpc payload consists of 3 parts
                // byte 0 - Specifying if the payload is compressed.
                // 1-4 byte - Specifies the length of payload in big endian format.
                // 5 and above -  Protobuf serialized data.
                Span<byte> data = new Span<byte>(this.buffer, 1, 4);
                var dataLength = writePosition - GrpcStartWritePosition;
                BinaryPrimitives.WriteUInt32BigEndian(data, (uint)dataLength);
            }

            if (!this.transmissionHandler.TrySubmitRequest(this.buffer, writePosition))
            {
                return ExportResult.Failure;
            }
        }
        catch (Exception ex)
        {
            OpenTelemetryProtocolExporterEventSource.Log.ExportMethodException(ex);
            return ExportResult.Failure;
        }

        return ExportResult.Success;
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => this.transmissionHandler.Shutdown(timeoutMilliseconds);
}
//----------------------------------------------------Ʌ

//---------------------V
public static class Sdk
{
    static Sdk()
    {
        Propagators.DefaultTextMapPropagator = new CompositeTextMapPropagator(new TextMapPropagator[]
        {
            new TraceContextPropagator(),
            new BaggagePropagator(),
        });

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        SelfDiagnostics.EnsureInitialized();

        var sdkAssembly = typeof(Sdk).Assembly;
        InformationalVersion = sdkAssembly.GetPackageVersion();
    }

    public static bool SuppressInstrumentation => SuppressInstrumentationScope.IsSuppressed;

    internal static string InformationalVersion { get; }

    public static void SetDefaultTextMapPropagator(TextMapPropagator textMapPropagator)
    {
        Guard.ThrowIfNull(textMapPropagator);

        Propagators.DefaultTextMapPropagator = textMapPropagator;
    }

    public static MeterProviderBuilder CreateMeterProviderBuilder()
    {
        return new MeterProviderBuilderBase();
    }

    public static TracerProviderBuilder CreateTracerProviderBuilder()
    {
        return new TracerProviderBuilderBase();
    }

    public static LoggerProviderBuilder CreateLoggerProviderBuilder()
    {
        return new LoggerProviderBuilderBase();
    }
}
//---------------------Ʌ

//-----------------------------------V
public class MeterProviderBuilderBase : MeterProviderBuilder, IMeterProviderBuilder
{
    private readonly bool allowBuild;
    private readonly MeterProviderServiceCollectionBuilder innerBuilder;

    public MeterProviderBuilderBase()
    {
        var services = new ServiceCollection();

        services
            .AddOpenTelemetrySharedProviderBuilderServices()
            .AddOpenTelemetryMeterProviderBuilderServices()
            .TryAddSingleton<MeterProvider>(
                sp => throw new NotSupportedException("Self-contained MeterProvider cannot be accessed using the application IServiceProvider call Build instead."));

        this.innerBuilder = new MeterProviderServiceCollectionBuilder(services);

        this.allowBuild = true;
    }

    internal MeterProviderBuilderBase(IServiceCollection services)
    {
        services
            .AddOpenTelemetryMeterProviderBuilderServices()
            .TryAddSingleton<MeterProvider>(sp => new MeterProviderSdk(sp, ownsServiceProvider: false));

        this.innerBuilder = new MeterProviderServiceCollectionBuilder(services);

        this.allowBuild = false;
    }

    MeterProvider? IMeterProviderBuilder.Provider => null;

    public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        this.innerBuilder.AddInstrumentation(instrumentationFactory);

        return this;
    }

    public override MeterProviderBuilder AddMeter(params string[] names)
    {
        this.innerBuilder.AddMeter(names);

        return this;
    }

    MeterProviderBuilder IMeterProviderBuilder.ConfigureServices(Action<IServiceCollection> configure)
    {
        this.innerBuilder.ConfigureServices(configure);

        return this;
    }

    MeterProviderBuilder IDeferredMeterProviderBuilder.Configure(Action<IServiceProvider, MeterProviderBuilder> configure)
    {
        this.innerBuilder.ConfigureBuilder(configure);

        return this;
    }

    internal MeterProvider InvokeBuild()
        => this.Build();

    protected MeterProvider Build()
    {
        if (!this.allowBuild)
        {
            throw new NotSupportedException("A MeterProviderBuilder bound to external service cannot be built directly. Access the MeterProvider using the application IServiceProvider instead.");
        }

        var services = this.innerBuilder.Services ?? throw new NotSupportedException("MeterProviderBuilder build method cannot be called multiple times.");

        this.innerBuilder.Services = null;

        bool validateScopes = true;  // false if it is not DEBUG

        var serviceProvider = services.BuildServiceProvider(validateScopes);

        return new MeterProviderSdk(serviceProvider, ownsServiceProvider: true);
    }
}
//-----------------------------------Ʌ

//------------------------------------V
internal sealed class MeterProviderSdk : MeterProvider
{
    internal const string ExemplarFilterConfigKey = "OTEL_METRICS_EXEMPLAR_FILTER";
    internal const string ExemplarFilterHistogramsConfigKey = "OTEL_DOTNET_EXPERIMENTAL_METRICS_EXEMPLAR_FILTER_HISTOGRAMS";

    internal readonly IServiceProvider ServiceProvider;
    internal IDisposable? OwnedServiceProvider;
    internal int ShutdownCount;
    internal bool Disposed;
    internal ExemplarFilterType? ExemplarFilter;
    internal ExemplarFilterType? ExemplarFilterForHistograms;
    internal Action? OnCollectObservableInstruments;

    private readonly List<object> instrumentations = [];
    private readonly List<Func<Instrument, MetricStreamConfiguration?>> viewConfigs;
    private readonly Lock collectLock = new();
    private readonly MeterListener listener;
    private readonly Func<Instrument, bool> shouldListenTo = instrument => false;
    private CompositeMetricReader? compositeMetricReader;
    private MetricReader? reader;

    internal MeterProviderSdk(IServiceProvider serviceProvider, bool ownsServiceProvider)
    {
        MeterProviderBuilderSdk state = serviceProvider!.GetRequiredService<MeterProviderBuilderSdk>();
        state.RegisterProvider(this);

        this.ServiceProvider = serviceProvider!;

        if (ownsServiceProvider)
            this.OwnedServiceProvider = serviceProvider as IDisposable;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent("Building MeterProvider.");

        var configureProviderBuilders = serviceProvider!.GetServices<IConfigureMeterProviderBuilder>();
        foreach (var configureProviderBuilder in configureProviderBuilders)
        {
            configureProviderBuilder.ConfigureBuilder(serviceProvider!, state);
        }

        this.ExemplarFilter = state.ExemplarFilter;

        this.ApplySpecificationConfigurationKeys(serviceProvider!.GetRequiredService<IConfiguration>());

        StringBuilder exportersAdded = new StringBuilder();
        StringBuilder instrumentationFactoriesAdded = new StringBuilder();

        var resourceBuilder = state.ResourceBuilder ?? ResourceBuilder.CreateDefault();
        resourceBuilder.ServiceProvider = serviceProvider;
        this.Resource = resourceBuilder.Build();

        this.viewConfigs = state.ViewConfigs;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
            $"MeterProvider configuration: {{MetricLimit={state.MetricLimit}, CardinalityLimit={state.CardinalityLimit}, ExemplarFilter={this.ExemplarFilter}, ExemplarFilterForHistograms={this.ExemplarFilterForHistograms}}}.");

        foreach (var reader in state.Readers)
        {
            reader.SetParentProvider(this);

            reader.ApplyParentProviderSettings(
                state.MetricLimit,
                state.CardinalityLimit,
                this.ExemplarFilter,
                this.ExemplarFilterForHistograms);

            if (this.reader == null)
            {
                this.reader = reader;
            }
            else if (this.reader is CompositeMetricReader compositeReader)
            {
                compositeReader.AddReader(reader);
            }
            else
            {
                this.reader = new CompositeMetricReader([this.reader, reader]);
            }

            if (reader is PeriodicExportingMetricReader periodicExportingMetricReader)
            {
                exportersAdded.Append(periodicExportingMetricReader.Exporter);
                exportersAdded.Append(" (Paired with PeriodicExportingMetricReader exporting at ");
                exportersAdded.Append(periodicExportingMetricReader.ExportIntervalMilliseconds);
                exportersAdded.Append(" milliseconds intervals.)");
                exportersAdded.Append(';');
            }
            else if (reader is BaseExportingMetricReader baseExportingMetricReader)
            {
                exportersAdded.Append(baseExportingMetricReader.Exporter);
                exportersAdded.Append(" (Paired with a MetricReader requiring manual trigger to export.)");
                exportersAdded.Append(';');
            }
        }

        if (exportersAdded.Length != 0)
        {
            exportersAdded.Remove(exportersAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exporters added = \"{exportersAdded}\".");
        }

        this.compositeMetricReader = this.reader as CompositeMetricReader;

        if (state.Instrumentation.Count > 0)
        {
            foreach (var instrumentation in state.Instrumentation)
            {
                if (instrumentation.Instance is not null)
                {
                    this.instrumentations.Add(instrumentation.Instance);
                }

                instrumentationFactoriesAdded.Append(instrumentation.Name);
                instrumentationFactoriesAdded.Append(';');
            }
        }

        if (instrumentationFactoriesAdded.Length != 0)
        {
            instrumentationFactoriesAdded.Remove(instrumentationFactoriesAdded.Length - 1, 1);
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Instrumentations added = \"{instrumentationFactoriesAdded}\".");
        }

        // Setup Listener
        if (state.MeterSources.Exists(WildcardHelper.ContainsWildcard))
        {
            var regex = WildcardHelper.GetWildcardRegex(state.MeterSources);
            this.shouldListenTo = instrument => regex.IsMatch(instrument.Meter.Name);
        }
        else if (state.MeterSources.Count > 0)
        {
            var meterSourcesToSubscribe = new HashSet<string>(state.MeterSources, StringComparer.OrdinalIgnoreCase);
            this.shouldListenTo = instrument => meterSourcesToSubscribe.Contains(instrument.Meter.Name);
        }

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Listening to following meters = \"{string.Join(";", state.MeterSources)}\".");

        this.listener = new MeterListener();   // <-----------------------------!!
        var viewConfigCount = this.viewConfigs.Count;

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Number of views configured = {viewConfigCount}.");

        this.listener.InstrumentPublished = (instrument, listener) =>   // <-----------------------met2.3
        {
            MetricState? state = this.InstrumentPublished(instrument, listeningIsManagedExternally: false);  // <-------------met2.4.0
            if (state != null)
            {
                listener.EnableMeasurementEvents(instrument, state);   // <------------------------met2.5.0
            }
        };

        // <------------------------------------------------------------------------smec
        // Everything double
        this.listener.SetMeasurementEventCallback<double>(MeasurementRecordedDouble);
        this.listener.SetMeasurementEventCallback<float>(static (instrument, value, tags, state) => MeasurementRecordedDouble(instrument, value, tags, state));

        // Everything long
        this.listener.SetMeasurementEventCallback<long>(MeasurementRecordedLong);
        this.listener.SetMeasurementEventCallback<int>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));  // <-------met3.4
        this.listener.SetMeasurementEventCallback<short>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));
        this.listener.SetMeasurementEventCallback<byte>(static (instrument, value, tags, state) => MeasurementRecordedLong(instrument, value, tags, state));

        this.listener.MeasurementsCompleted = MeasurementsCompleted;    // <----------------------------------

        this.listener.Start();   // <-----------------------------!!

        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent("MeterProvider built successfully.");
    }

    internal Resource Resource { get; }

    internal List<object> Instrumentations => this.instrumentations;

    internal MetricReader? Reader => this.reader;

    internal int ViewCount => this.viewConfigs.Count;

    // object is MetricState
    internal object? InstrumentPublished(Instrument instrument, bool listeningIsManagedExternally)   // <-------------------------met2.4.0
    {   
        var listenToInstrumentUsingSdkConfiguration = this.shouldListenTo(instrument); // <-------------------met2.4.1

        if (listeningIsManagedExternally && listenToInstrumentUsingSdkConfiguration)   // listeningIsManagedExternally is default by false
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name,
                "Instrument belongs to a Meter which has been enabled both externally and via a subscription on the provider. External subscription will be ignored in favor of the provider subscription.", "Programmatic calls adding meters to the SDK (either by calling Add*Meter directly or indirectly through helpers such as 'AddInstrumentation' extensions) are always favored over external registrations. When also using external management (typically IMetricsBuilder or IMetricsListener) remove programmatic calls to the SDK to allow registrations to be added and removed dynamically.");
            
            return null;
        }
        else if (!listenToInstrumentUsingSdkConfiguration && !listeningIsManagedExternally)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name,
                "Instrument belongs to a Meter not subscribed by the provider.", "Use Add*Meter to add the Meter to the provider.");
            
            return null;
        }

        object? state = null;
        var viewConfigCount = this.viewConfigs.Count;

        try
        {
            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Started publishing Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\".");

            if (viewConfigCount <= 0)
            {
                if (!MeterProviderBuilderSdk.IsValidInstrumentName(instrument.Name))
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name,
                        "Instrument name is invalid.", "The name must comply with the OpenTelemetry specification");

                    return null;
                }

                if (this.reader != null)
                {
                    List<Metric> metrics = this.reader.AddMetricWithNoViews(instrument);   // <----------------------met2.4.2, reader is PeriodicExportingMetricReader
                    if (metrics.Count == 1)
                    {
                        state = MetricState.BuildForSingleMetric(metrics[0]);
                    }
                    else if (metrics.Count > 0)
                    {
                        state = MetricState.BuildForMetricList(metrics);
                    }
                }
            }
            else
            {
                // Creating list with initial capacity as the maximum
                // possible size, to avoid any array resize/copy internally.
                // There may be excess space wasted, but it'll eligible for
                // GC right after this method.
                var metricStreamConfigs = new List<MetricStreamConfiguration?>(viewConfigCount);
                for (var i = 0; i < viewConfigCount; ++i)
                {
                    var viewConfig = this.viewConfigs[i];
                    MetricStreamConfiguration? metricStreamConfig = null;

                    try
                    {
                        metricStreamConfig = viewConfig(instrument);

                        // The SDK provides some static MetricStreamConfigurations.
                        // For example, the Drop configuration. The static ViewId
                        // should not be changed for these configurations.
                        if (metricStreamConfig != null && !metricStreamConfig.ViewId.HasValue)
                        {
                            metricStreamConfig.ViewId = i;
                        }

                        if (metricStreamConfig is HistogramConfiguration && instrument.GetType().GetGenericTypeDefinition() != typeof(Histogram<>))
                        {
                            metricStreamConfig = null;

                            OpenTelemetrySdkEventSource.Log.MetricViewIgnored(instrument.Name, instrument.Meter.Name,
                                "The current SDK does not allow aggregating non-Histogram instruments as Histograms.", "Fix the view configuration.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OpenTelemetrySdkEventSource.Log.MetricViewIgnored(instrument.Name, instrument.Meter.Name, ex.Message, "Fix the view configuration.");
                    }

                    if (metricStreamConfig != null)
                    {
                        metricStreamConfigs.Add(metricStreamConfig);
                    }
                }

                if (metricStreamConfigs.Count == 0)
                {
                    // no views matched. Add null which will apply defaults. users can turn off this default by adding a view like below as the last view:
                    // .AddView(instrumentName: "*", MetricStreamConfiguration.Drop)
                    metricStreamConfigs.Add(null);
                }

                if (this.reader != null)
                {
                    var metrics = this.reader.AddMetricWithViews(instrument, metricStreamConfigs);
                    if (metrics.Count == 1)
                    {
                        state = MetricState.BuildForSingleMetric(metrics[0]);
                    }
                    else if (metrics.Count > 0)
                    {
                        state = MetricState.BuildForMetricList(metrics);
                    }
                }
            }

            if (state != null)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Measurements for Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\" will be processed and aggregated by the SDK.");
                return state;
            }
            else
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Measurements for Instrument = \"{instrument.Name}\" of Meter = \"{instrument.Meter.Name}\" will be dropped by the SDK.");
                return null;
            }
        }
        catch (Exception)
        {
            OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(instrument.Name, instrument.Meter.Name, "SDK internal error occurred.", "Contact SDK owners.");
            return null;
        }
    }

    internal static void MeasurementsCompleted(Instrument instrument, object? state)
    {
        if (state is not MetricState metricState)
            return;

        metricState.CompleteMeasurement();
    }

    internal static void MeasurementRecordedLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)  // <-------met3.4
    {
        if (state is not MetricState metricState)
        {
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(instrument?.Name ?? "UnknownInstrument", "SDK internal error occurred.", "Contact SDK owners.");
            return;
        }

        metricState.RecordMeasurementLong(value, tags);  // <----------------------------met3.5
    }

    internal static void MeasurementRecordedDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not MetricState metricState)
        {
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(instrument?.Name ?? "UnknownInstrument", "SDK internal error occurred.", "Contact SDK owners.");
            return;
        }

        metricState.RecordMeasurementDouble(value, tags);
    }

    internal void CollectObservableInstruments()
    {
        lock (this.collectLock)
        {
            // Record all observable instruments
            try
            {
                this.listener.RecordObservableInstruments();

                this.OnCollectObservableInstruments?.Invoke();
            }
            catch (Exception exception)
            {
                OpenTelemetrySdkEventSource.Log.MetricObserverCallbackException(exception);
            }
        }
    }

    internal bool OnForceFlush(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.OnForceFlush)} called with {nameof(timeoutMilliseconds)} = {timeoutMilliseconds}.");

        return this.reader?.Collect(timeoutMilliseconds) ?? true;
    }

    internal bool OnShutdown(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.OnShutdown)} called with {nameof(timeoutMilliseconds)} = {timeoutMilliseconds}.");

        return this.reader?.Shutdown(timeoutMilliseconds) ?? true;
    }

    protected override void Dispose(bool disposing)
    {
        OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"{nameof(MeterProviderSdk)}.{nameof(this.Dispose)} started.");
        if (!this.Disposed)
        {
            if (disposing)
            {
                foreach (var item in this.instrumentations)
                {
                    (item as IDisposable)?.Dispose();
                }

                this.instrumentations.Clear();

                // Wait for up to 5 seconds grace period
                this.reader?.Shutdown(5000);
                this.reader?.Dispose();
                this.reader = null;

                this.compositeMetricReader?.Dispose();
                this.compositeMetricReader = null;

                this.listener?.Dispose();

                this.OwnedServiceProvider?.Dispose();
                this.OwnedServiceProvider = null;
            }

            this.Disposed = true;
            OpenTelemetrySdkEventSource.Log.ProviderDisposed(nameof(MeterProvider));
        }

        base.Dispose(disposing);
    }

    private void ApplySpecificationConfigurationKeys(IConfiguration configuration)
    {
        var hasProgrammaticExemplarFilterValue = this.ExemplarFilter.HasValue;

        if (configuration.TryGetStringValue(ExemplarFilterConfigKey, out var configValue))
        {
            if (hasProgrammaticExemplarFilterValue)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
                    $"Exemplar filter configuration value '{configValue}' has been ignored because a value '{this.ExemplarFilter}' was set programmatically.");
                return;
            }

            if (!TryParseExemplarFilterFromConfigurationValue(configValue, out var exemplarFilter))
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter configuration was found but the value '{configValue}' is invalid and will be ignored.");
                return;
            }

            this.ExemplarFilter = exemplarFilter;

            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter set to '{exemplarFilter}' from configuration.");
        }

        if (configuration.TryGetStringValue(ExemplarFilterHistogramsConfigKey, out configValue))
        {
            if (hasProgrammaticExemplarFilterValue)
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent(
                    $"Exemplar filter histogram configuration value '{configValue}' has been ignored because a value '{this.ExemplarFilter}' was set programmatically.");
                return;
            }

            if (!TryParseExemplarFilterFromConfigurationValue(configValue, out var exemplarFilter))
            {
                OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter histogram configuration was found but the value '{configValue}' is invalid and will be ignored.");
                return;
            }

            this.ExemplarFilterForHistograms = exemplarFilter;

            OpenTelemetrySdkEventSource.Log.MeterProviderSdkEvent($"Exemplar filter for histograms set to '{exemplarFilter}' from configuration.");
        }

        static bool TryParseExemplarFilterFromConfigurationValue(string? configValue, out ExemplarFilterType? exemplarFilter)
        {
            if (string.Equals("always_off", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.AlwaysOff;
                return true;
            }

            if (string.Equals("always_on", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.AlwaysOn;
                return true;
            }

            if (string.Equals("trace_based", configValue, StringComparison.OrdinalIgnoreCase))
            {
                exemplarFilter = ExemplarFilterType.TraceBased;
                return true;
            }

            exemplarFilter = null;
            return false;
        }
    }
}
//------------------------------------Ʌ

//-------------------------------------------V
internal sealed class MeterProviderBuilderSdk : MeterProviderBuilder, IMeterProviderBuilder
{
    public const int DefaultMetricLimit = 1000;
    public const int DefaultCardinalityLimit = 2000;
    private const string DefaultInstrumentationVersion = "1.0.0.0";

    private readonly IServiceProvider serviceProvider;
    private MeterProviderSdk? meterProvider;

    public MeterProviderBuilderSdk(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public static Regex InstrumentNameRegex { get; set; } = new(@"^[a-z][a-z0-9-._/]{0,254}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<InstrumentationRegistration> Instrumentation { get; } = new();

    public ResourceBuilder? ResourceBuilder { get; private set; }

    public ExemplarFilterType? ExemplarFilter { get; private set; }

    public MeterProvider? Provider => this.meterProvider;

    public List<MetricReader> Readers { get; } = new();

    public List<string> MeterSources { get; } = new();

    public List<Func<Instrument, MetricStreamConfiguration?>> ViewConfigs { get; } = new();

    public int MetricLimit { get; private set; } = DefaultMetricLimit;

    public int CardinalityLimit { get; private set; } = DefaultCardinalityLimit;

    public static bool IsValidInstrumentName(string instrumentName)
    {
        if (string.IsNullOrWhiteSpace(instrumentName))
            return false;

        return InstrumentNameRegex.IsMatch(instrumentName);
    }

    public static bool IsValidViewName(string customViewName)
    {
        // Only validate the view name in case it's not null. In case it's null, the view name will be the instrument name as per the spec.
        if (customViewName == null)
        {
            return true;
        }

        return InstrumentNameRegex.IsMatch(customViewName);
    }

    public void RegisterProvider(MeterProviderSdk meterProvider)
    {
        if (this.meterProvider != null)
        {
            throw new NotSupportedException("MeterProvider cannot be accessed while build is executing.");
        }

        this.meterProvider = meterProvider;
    }

    public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
    {
        return this.AddInstrumentation(
            typeof(TInstrumentation).Name,
            typeof(TInstrumentation).Assembly.GetName().Version?.ToString() ?? DefaultInstrumentationVersion,
            instrumentationFactory!());
    }

    public MeterProviderBuilder AddInstrumentation(string instrumentationName, string instrumentationVersion, object? instrumentation)
    {
        this.Instrumentation.Add(new InstrumentationRegistration(instrumentationName, instrumentationVersion, instrumentation));

        return this;
    }

    public MeterProviderBuilder ConfigureResource(Action<ResourceBuilder> configure)
    {
        var resourceBuilder = this.ResourceBuilder ??= ResourceBuilder.CreateDefault();

        configure!(resourceBuilder);

        return this;
    }

    public MeterProviderBuilder SetResourceBuilder(ResourceBuilder resourceBuilder)
    {
        this.ResourceBuilder = resourceBuilder;

        return this;
    }

    public MeterProviderBuilder SetExemplarFilter(ExemplarFilterType exemplarFilter)
    {
        this.ExemplarFilter = exemplarFilter;

        return this;
    }

    public override MeterProviderBuilder AddMeter(params string[] names)  // <-------------------------------
    {
        foreach (var name in names!)
        {
            this.MeterSources.Add(name);
        }

        return this;
    }

    public MeterProviderBuilder AddReader(MetricReader reader)
    {
        this.Readers.Add(reader!);

        return this;
    }

    public MeterProviderBuilder AddView(Func<Instrument, MetricStreamConfiguration?> viewConfig)   // <--------------------------
    {
        this.ViewConfigs.Add(viewConfig!);

        return this;
    }

    public MeterProviderBuilder SetMetricLimit(int metricLimit)
    {
        this.MetricLimit = metricLimit;

        return this;
    }

    public MeterProviderBuilder SetDefaultCardinalityLimit(int cardinalityLimit)
    {
        this.CardinalityLimit = cardinalityLimit;

        return this;
    }

    public MeterProviderBuilder ConfigureBuilder(Action<IServiceProvider, MeterProviderBuilder> configure)
    {
        configure!(this.serviceProvider, this);

        return this;
    }

    public MeterProviderBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        throw new NotSupportedException("Services cannot be configured after ServiceProvider has been created.");
    }

    MeterProviderBuilder IDeferredMeterProviderBuilder.Configure(Action<IServiceProvider, MeterProviderBuilder> configure)
        => this.ConfigureBuilder(configure);

    internal readonly struct InstrumentationRegistration
    {
        public readonly string Name;
        public readonly string Version;
        public readonly object? Instance;

        internal InstrumentationRegistration(string name, string version, object? instance)
        {
            this.Name = name;
            this.Version = version;
            this.Instance = instance;
        }
    }
}
//-------------------------------------------Ʌ

//---------------------------------------V
public class MeterProvider : BaseProvider
{
    protected MeterProvider() { }
}
//---------------------------------------Ʌ

//------------------------------------------------V
internal sealed class OpenTelemetryMetricsListener : IMetricsListener, IDisposable
{
    private readonly MeterProviderSdk meterProviderSdk;
    private IObservableInstrumentsSource? observableInstrumentsSource;

    public OpenTelemetryMetricsListener(MeterProvider meterProvider)
    {
        var meterProviderSdk = meterProvider as MeterProviderSdk;

        this.meterProviderSdk = meterProviderSdk!;

        this.meterProviderSdk.OnCollectObservableInstruments += this.OnCollectObservableInstruments;
    }

    public string Name => "OpenTelemetry";

    public void Dispose()
    {
        this.meterProviderSdk.OnCollectObservableInstruments -= this.OnCollectObservableInstruments;
    }

    public MeasurementHandlers GetMeasurementHandlers()
    {
        return new MeasurementHandlers()
        {
            ByteHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            ShortHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            IntHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedLong(instrument, value, tags, state),
            LongHandler = MeterProviderSdk.MeasurementRecordedLong,
            FloatHandler = (instrument, value, tags, state)
                => MeterProviderSdk.MeasurementRecordedDouble(instrument, value, tags, state),
            DoubleHandler = MeterProviderSdk.MeasurementRecordedDouble,
        };
    }

    public bool InstrumentPublished(Instrument instrument, out object? userState)
    {
        userState = this.meterProviderSdk.InstrumentPublished(instrument, listeningIsManagedExternally: true);
        return userState != null;
    }

    public void MeasurementsCompleted(Instrument instrument, object? userState)
    {
        MeterProviderSdk.MeasurementsCompleted(instrument, userState);
    }

    public void Initialize(IObservableInstrumentsSource source)
    {
        this.observableInstrumentsSource = source;
    }

    private void OnCollectObservableInstruments()
    {
        this.observableInstrumentsSource?.RecordObservableInstruments();
    }
}
//------------------------------------------------Ʌ

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
            recordMeasurementLong: metric!.UpdateLong,      // <----------------------------met3.5
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
        if (instrumentIdentity.InstrumentType == typeof(ObservableCounter<long>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<int>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<short>)
            || instrumentIdentity.InstrumentType == typeof(ObservableCounter<byte>))
        {
            aggType = AggregationType.LongSumIncomingCumulative;
            this.MetricType = MetricType.LongSum;
        }
        else if (instrumentIdentity.InstrumentType == typeof(Counter<long>)
            || instrumentIdentity.InstrumentType == typeof(Counter<int>)
            || instrumentIdentity.InstrumentType == typeof(Counter<short>)
            || instrumentIdentity.InstrumentType == typeof(Counter<byte>))
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

    internal void UpdateLong(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags); // <---------------------met3.5.

    internal void UpdateDouble(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => this.AggregatorStore.Update(value, tags);

    internal int Snapshot() => this.AggregatorStore.Snapshot();
}
//------------------------Ʌ

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

//-----------------------------V
internal enum MetricPointStatus
{
    NoCollectPending,
    CollectPending,
}
//-----------------------------Ʌ

//-----------------------V
public struct MetricPoint  // hold the actual value via MetricPointValueStorage
{
    internal int ReferenceCount;

    private const int DefaultSimpleReservoirPoolSize = 1;

    private readonly AggregatorStore aggregatorStore;

    private readonly AggregationType aggType;

    private MetricPointOptionalComponents? mpComponents;

    // represents temporality adjusted "value" for double/long metric types or "count" when histogram
    private MetricPointValueStorage runningValue;  // <------------------------------------

    // represents either "value" for double/long metric types or "count" when histogram
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

    internal void Update(long number)
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

//-----------------------------------------V
public readonly struct MetricPointsAccessor
{
    private readonly MetricPoint[] metricsPoints;
    private readonly int[] metricPointsToProcess;
    private readonly int targetCount;

    internal MetricPointsAccessor(MetricPoint[] metricsPoints, int[] metricPointsToProcess, int targetCount)
    {
        this.metricsPoints = metricsPoints!;
        this.metricPointsToProcess = metricPointsToProcess!;
        this.targetCount = targetCount;
    }

    public Enumerator GetEnumerator() => new(this.metricsPoints, this.metricPointsToProcess, this.targetCount);


    public struct Enumerator
    {
        private readonly MetricPoint[] metricsPoints;
        private readonly int[] metricPointsToProcess;
        private readonly int targetCount;
        private int index;

        internal Enumerator(MetricPoint[] metricsPoints, int[] metricPointsToProcess, int targetCount)
        {
            this.metricsPoints = metricsPoints;
            this.metricPointsToProcess = metricPointsToProcess;
            this.targetCount = targetCount;
            this.index = -1;
        }

        public readonly ref readonly MetricPoint Current  => ref this.metricsPoints[this.metricPointsToProcess[this.index]];
 
        public bool MoveNext() => ++this.index < this.targetCount;
    }
}
//-----------------------------------------Ʌ

//--------------------V
[Flags]
public enum MetricType : byte
{
    LongSum = 0x1a,
    DoubleSum = 0x1d,
    LongGauge = 0x2a,
    DoubleGauge = 0x2d,
    Histogram = 0x40,
    ExponentialHistogram = 0x50,
    LongSumNonMonotonic = 0x8a,
    DoubleSumNonMonotonic = 0x8d,
}
//--------------------Ʌ

//------------------------------------V
public class MetricStreamConfiguration
{
    private string? name;

    private int? cardinalityLimit;

    public static MetricStreamConfiguration Drop { get; } = new MetricStreamConfiguration { ViewId = -1 };

    public string? Name
    {
        get => this.name;
        set
        {
            if (value != null && !MeterProviderBuilderSdk.IsValidViewName(value))
            {
                throw new ArgumentException($"Custom view name {value} is invalid.", nameof(value));
            }

            this.name = value;
        }
    }

    public string? Description { get; set; }

    public string[]? TagKeys
    {
        get => this.CopiedTagKeys?.ToArray();
        set => this.CopiedTagKeys = value?.ToArray();
    }

    public int? CardinalityLimit
    {
        get => this.cardinalityLimit;
        set
        {
            if (value != null)
            {
                Guard.ThrowIfOutOfRange(value.Value, min: 1, max: int.MaxValue);
            }

            this.cardinalityLimit = value;
        }
    }

    public Func<ExemplarReservoir?>? ExemplarReservoirFactory { get; set; }

    internal string[]? CopiedTagKeys { get; private set; }

    internal int? ViewId { get; set; }
}
//------------------------------------Ʌ

//---------------------------------V
public class HistogramConfiguration : MetricStreamConfiguration
{
    public bool RecordMinMax { get; set; } = true;
}
//---------------------------------Ʌ

//-----------------------------------------------V
public class ExplicitBucketHistogramConfiguration : HistogramConfiguration
{
    public double[]? Boundaries
    {
        get
        {
            if (this.CopiedBoundaries != null)
            {
                double[] copy = new double[this.CopiedBoundaries.Length];
                this.CopiedBoundaries.AsSpan().CopyTo(copy);
                return copy;
            }

            return null;
        }

        set
        {
            if (value != null)
            {
                if (!IsSortedAndDistinct(value))
                {
                    throw new ArgumentException($"Histogram boundaries are invalid. Histogram boundaries must be in ascending order with distinct values.", nameof(value));
                }

                double[] copy = new double[value.Length];
                value.AsSpan().CopyTo(copy);
                this.CopiedBoundaries = copy;
            }
            else
            {
                this.CopiedBoundaries = null;
            }
        }
    }

    internal double[]? CopiedBoundaries { get; private set; }

    private static bool IsSortedAndDistinct(double[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
//-----------------------------------------------Ʌ

//----------------------------V
public enum ExemplarFilterType
{
    AlwaysOff,
    AlwaysOn,
    TraceBased,
}
//----------------------------Ʌ
```

```C#
//--------------------------------------------------V
public static class ConsoleExporterMetricsExtensions
{
    private const int DefaultExportIntervalMilliseconds = 10000;
    private const int DefaultExportTimeoutMilliseconds = Timeout.Infinite;

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder)
        => AddConsoleExporter(builder, name: null, configureExporter: null);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, Action<ConsoleExporterOptions> configureExporter)
        => AddConsoleExporter(builder, name: null, configureExporter);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, string? name, Action<ConsoleExporterOptions>? configureExporter)
    {

        name ??= Options.DefaultName;

        if (configureExporter != null)
        {
            builder.ConfigureServices(services => services.Configure(name, configureExporter));
        }

        return builder.AddReader(sp =>
        {
            return BuildConsoleExporterMetricReader(
                sp.GetRequiredService<IOptionsMonitor<ConsoleExporterOptions>>().Get(name),
                sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(name));
        });
    }

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, Action<ConsoleExporterOptions, MetricReaderOptions>? configureExporterAndMetricReader)
        => AddConsoleExporter(builder, name: null, configureExporterAndMetricReader);

    public static MeterProviderBuilder AddConsoleExporter(this MeterProviderBuilder builder, string? name, Action<ConsoleExporterOptions, MetricReaderOptions>? configureExporterAndMetricReader)
    {

        name ??= Options.DefaultName;

        return builder.AddReader(sp =>   // <--------------------------------------------2
        {
            var exporterOptions = sp.GetRequiredService<IOptionsMonitor<ConsoleExporterOptions>>().Get(name);
            var metricReaderOptions = sp.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(name);

            configureExporterAndMetricReader?.Invoke(exporterOptions, metricReaderOptions);

            return BuildConsoleExporterMetricReader(exporterOptions, metricReaderOptions);
        });
    }

    private static PeriodicExportingMetricReader BuildConsoleExporterMetricReader(ConsoleExporterOptions exporterOptions, MetricReaderOptions metricReaderOptions)
    {
        var metricExporter = new ConsoleMetricExporter(exporterOptions);

        return PeriodicExportingMetricReaderHelper.CreatePeriodicExportingMetricReader(  // <-------------------------------------------------3
            metricExporter,                                                              // create `new PeriodicExportingMetricReader(...)`
            metricReaderOptions,
            DefaultExportIntervalMilliseconds,
            DefaultExportTimeoutMilliseconds);
    }
}
//--------------------------------------------------Ʌ

//--------------------------------V
public class ConsoleMetricExporter : ConsoleExporter<Metric>
{
    public ConsoleMetricExporter(ConsoleExporterOptions options): base(options) { }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            var msg = new StringBuilder(Environment.NewLine);
            msg.Append(CultureInfo.InvariantCulture, $"Metric Name: {metric.Name}");
            if (string.IsNullOrEmpty(metric.Description))
            {
                msg.Append(CultureInfo.InvariantCulture, $", Description: {metric.Description}");
            }

            if (string.IsNullOrEmpty(metric.Unit))
            {
                msg.Append(CultureInfo.InvariantCulture, $", Unit: {metric.Unit}");
            }

            this.WriteLine(msg.ToString());

            foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            {
                string valueDisplay = string.Empty;
                StringBuilder tagsBuilder = new StringBuilder();
                foreach (var tag in metricPoint.Tags)
                {
                    if (this.TagWriter.TryTransformTag(tag, out var result))
                    {
                        tagsBuilder.Append(CultureInfo.InvariantCulture, $"{result.Key}: {result.Value}");
                        tagsBuilder.Append(' ');
                    }
                }

                var tags = tagsBuilder.ToString().TrimEnd();

                var metricType = metric.MetricType;

                if (metricType == MetricType.Histogram || metricType == MetricType.ExponentialHistogram)
                {
                    var bucketsBuilder = new StringBuilder();
                    var sum = metricPoint.GetHistogramSum();
                    var count = metricPoint.GetHistogramCount();
                    bucketsBuilder.Append(CultureInfo.InvariantCulture, $"Sum: {sum} Count: {count} ");
                    if (metricPoint.TryGetHistogramMinMaxValues(out double min, out double max))
                    {
                        bucketsBuilder.Append(CultureInfo.InvariantCulture, $"Min: {min} Max: {max} ");
                    }

                    bucketsBuilder.AppendLine();

                    if (metricType == MetricType.Histogram)
                    {
                        bool isFirstIteration = true;
                        double previousExplicitBound = default;
                        foreach (var histogramMeasurement in metricPoint.GetHistogramBuckets())
                        {
                            if (isFirstIteration)
                            {
                                bucketsBuilder.Append("(-Infinity,");
                                bucketsBuilder.Append(histogramMeasurement.ExplicitBound);
                                bucketsBuilder.Append(']');
                                bucketsBuilder.Append(':');
                                bucketsBuilder.Append(histogramMeasurement.BucketCount);
                                previousExplicitBound = histogramMeasurement.ExplicitBound;
                                isFirstIteration = false;
                            }
                            else
                            {
                                bucketsBuilder.Append('(');
                                bucketsBuilder.Append(previousExplicitBound);
                                bucketsBuilder.Append(',');
                                if (histogramMeasurement.ExplicitBound != double.PositiveInfinity)
                                {
                                    bucketsBuilder.Append(histogramMeasurement.ExplicitBound);
                                    previousExplicitBound = histogramMeasurement.ExplicitBound;
                                }
                                else
                                {
                                    bucketsBuilder.Append("+Infinity");
                                }

                                bucketsBuilder.Append(']');
                                bucketsBuilder.Append(':');
                                bucketsBuilder.Append(histogramMeasurement.BucketCount);
                            }

                            bucketsBuilder.AppendLine();
                        }
                    }
                    else
                    {
                        var exponentialHistogramData = metricPoint.GetExponentialHistogramData();
                        var scale = exponentialHistogramData.Scale;

                        if (exponentialHistogramData.ZeroCount != 0)
                            bucketsBuilder.AppendLine(CultureInfo.InvariantCulture, $"Zero Bucket:{exponentialHistogramData.ZeroCount}");

                        var offset = exponentialHistogramData.PositiveBuckets.Offset;
                        foreach (var bucketCount in exponentialHistogramData.PositiveBuckets)
                        {
                            var lowerBound = Base2ExponentialBucketHistogramHelper.CalculateLowerBoundary(offset, scale).ToString(CultureInfo.InvariantCulture);
                            var upperBound = Base2ExponentialBucketHistogramHelper.CalculateLowerBoundary(++offset, scale).ToString(CultureInfo.InvariantCulture);
                            bucketsBuilder.AppendLine(CultureInfo.InvariantCulture, $"({lowerBound}, {upperBound}]:{bucketCount}");
                        }
                    }

                    valueDisplay = bucketsBuilder.ToString();
                }
                else if (metricType.IsDouble())
                {
                    valueDisplay = metricType.IsSum() ? metricPoint.GetSumDouble().ToString(CultureInfo.InvariantCulture) : metricPoint.GetGaugeLastValueDouble().ToString(CultureInfo.InvariantCulture);
                }
                else if (metricType.IsLong())
                {
                    valueDisplay = metricType.IsSum() ? metricPoint.GetSumLong().ToString(CultureInfo.InvariantCulture) : metricPoint.GetGaugeLastValueLong().ToString(CultureInfo.InvariantCulture);
                }

                var exemplarString = new StringBuilder();
                if (metricPoint.TryGetExemplars(out var exemplars))
                {
                    foreach (ref readonly var exemplar in exemplars)
                    {
                        exemplarString.Append("Timestamp: ");
                        exemplarString.Append(exemplar.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                        if (metricType.IsDouble())
                        {
                            exemplarString.Append(" Value: ");
                            exemplarString.Append(exemplar.DoubleValue);
                        }
                        else if (metricType.IsLong())
                        {
                            exemplarString.Append(" Value: ");
                            exemplarString.Append(exemplar.LongValue);
                        }

                        if (exemplar.TraceId != default)
                        {
                            exemplarString.Append(" TraceId: ");
                            exemplarString.Append(exemplar.TraceId.ToHexString());
                            exemplarString.Append(" SpanId: ");
                            exemplarString.Append(exemplar.SpanId.ToHexString());
                        }

                        bool appendedTagString = false;
                        foreach (var tag in exemplar.FilteredTags)
                        {
                            if (this.TagWriter.TryTransformTag(tag, out var result))
                            {
                                if (!appendedTagString)
                                {
                                    exemplarString.Append(" Filtered Tags: ");
                                    appendedTagString = true;
                                }
                                exemplarString.Append(CultureInfo.InvariantCulture, $"{result.Key}: {result.Value}");
                                exemplarString.Append($"{result.Key}: {result.Value}");
                                exemplarString.Append(' ');
                            }
                        }
                        exemplarString.AppendLine();
                    }
                }

                msg = new StringBuilder();
                msg.Append('(');
                msg.Append(metricPoint.StartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                msg.Append(", ");
                msg.Append(metricPoint.EndTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                msg.Append("] ");
                msg.Append(tags);
                if (string.IsNullOrEmpty(tags))
                {
                    msg.Append(' ');
                }

                msg.Append(metric.MetricType);
                msg.AppendLine();
                msg.Append(CultureInfo.InvariantCulture, $"Value: {valueDisplay}");

                if (exemplarString.Length > 0)
                {
                    msg.AppendLine();
                    msg.AppendLine("Exemplars");
                    msg.Append(exemplarString);
                }

                this.WriteLine(msg.ToString());

                this.WriteLine("Instrumentation scope (Meter):");
                this.WriteLine($"\tName: {metric.MeterName}");
                if (!string.IsNullOrEmpty(metric.MeterVersion))
                {
                    this.WriteLine($"\tVersion: {metric.MeterVersion}");
                }

                if (metric.MeterTags?.Any() == true)
                {
                    this.WriteLine("\tTags:");
                    foreach (var meterTag in metric.MeterTags)
                    {
                        if (this.TagWriter.TryTransformTag(meterTag, out var result))
                        {
                            this.WriteLine($"\t\t{result.Key}: {result.Value}");
                        }
                    }
                }

                var resource = this.ParentProvider.GetResource();
                if (resource != Resource.Empty)
                {
                    this.WriteLine("Resource associated with Metric:");
                    foreach (var resourceAttribute in resource.Attributes)
                    {
                        if (this.TagWriter.TryTransformTag(resourceAttribute.Key, resourceAttribute.Value, out var result))
                        {
                            this.WriteLine($"\t{result.Key}: {result.Value}");
                        }
                    }
                }
            }
        }
        return ExportResult.Success;
    }
}
//--------------------------------Ʌ

//-------------------------------------------------------V
internal static class PeriodicExportingMetricReaderHelper
{
    internal const int DefaultExportIntervalMilliseconds = 60000;
    internal const int DefaultExportTimeoutMilliseconds = 30000;

    internal static PeriodicExportingMetricReader CreatePeriodicExportingMetricReader(
        BaseExporter<Metric> exporter,
        MetricReaderOptions options,
        int defaultExportIntervalMilliseconds = DefaultExportIntervalMilliseconds,
        int defaultExportTimeoutMilliseconds = DefaultExportTimeoutMilliseconds)
    {
        var exportInterval =
            options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds ?? defaultExportIntervalMilliseconds;

        var exportTimeout =
            options.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds ?? defaultExportTimeoutMilliseconds;

        var metricReader = new PeriodicExportingMetricReader(exporter, exportInterval, exportTimeout)
        {
            TemporalityPreference = options.TemporalityPreference,
        };

        return metricReader;
    }
}
//-------------------------------------------------------Ʌ

//----------------------------------------V
public class PeriodicExportingMetricReader : BaseExportingMetricReader
{
    internal const int DefaultExportIntervalMilliseconds = 60000;
    internal const int DefaultExportTimeoutMilliseconds = 30000;

    internal readonly int ExportIntervalMilliseconds;
    internal readonly int ExportTimeoutMilliseconds;
    private readonly Thread exporterThread;
    private readonly AutoResetEvent exportTrigger = new(false);
    private readonly ManualResetEvent shutdownTrigger = new(false);
    private bool disposed;

    public PeriodicExportingMetricReader(
        BaseExporter<Metric> exporter,
        int exportIntervalMilliseconds = DefaultExportIntervalMilliseconds,
        int exportTimeoutMilliseconds = DefaultExportTimeoutMilliseconds) : base(exporter)
    {
        if ((this.SupportedExportModes & ExportModes.Push) != ExportModes.Push)
            throw new InvalidOperationException($"The '{nameof(exporter)}' does not support '{nameof(ExportModes)}.{nameof(ExportModes.Push)}'");

        this.ExportIntervalMilliseconds = exportIntervalMilliseconds;
        this.ExportTimeoutMilliseconds = exportTimeoutMilliseconds;

        // use Thread rather than Task.Run (because it uses the thread pool) needs a long-running, dedicated background thread, that is not tied to the .NET thread pool 
        this.exporterThread = new Thread(new ThreadStart(this.ExporterProc))   // <-----------------------------
        {
            IsBackground = true,
            Name = $"OpenTelemetry-{nameof(PeriodicExportingMetricReader)}-{exporter.GetType().Name}",
        };

        this.exporterThread.Start();
    }

    private void ExporterProc()  // <----------------------------------------
    {
        int index;
        int timeout;
        var triggers = new WaitHandle[] { this.exportTrigger, this.shutdownTrigger };
        var sw = Stopwatch.StartNew();

        while (true)
        {
            timeout = (int)(this.ExportIntervalMilliseconds - (sw.ElapsedMilliseconds % this.ExportIntervalMilliseconds));

            try
            {
                index = WaitHandle.WaitAny(triggers, timeout);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            switch (index)
            {
                case 0: // export
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because Export was triggered.");
                    this.Collect(this.ExportTimeoutMilliseconds);
                    break;
                case 1: // shutdown
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because Shutdown was triggered.");
                    this.Collect(this.ExportTimeoutMilliseconds); // TODO: do we want to use the shutdown timeout here?
                    return;
                case WaitHandle.WaitTimeout: // timer
                    OpenTelemetrySdkEventSource.Log.MetricReaderEvent("PeriodicExportingMetricReader calling MetricReader.Collect because the export interval has elapsed.");
                    this.Collect(this.ExportTimeoutMilliseconds);   // <------------------------- calls MetricReader.Collect()
                    break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.exportTrigger.Dispose();
                this.shutdownTrigger.Dispose();
            }

            this.disposed = true;
        }

        base.Dispose(disposing);
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = true;

        try
        {
            this.shutdownTrigger.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (timeoutMilliseconds == Timeout.Infinite)
        {
            this.exporterThread.Join();
            result = this.exporter.Shutdown() && result;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            result = this.exporterThread.Join(timeoutMilliseconds) && result;
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;
            result = this.exporter.Shutdown((int)Math.Max(timeout, 0)) && result;
        }

        return result;
    }
}
//----------------------------------------Ʌ

//------------------------------------V
public class BaseExportingMetricReader : MetricReader
{
    protected readonly BaseExporter<Metric> exporter;

    private readonly string exportCalledMessage;
    private readonly string exportSucceededMessage;
    private readonly string exportFailedMessage;
    private bool disposed;

    public BaseExportingMetricReader(BaseExporter<Metric> exporter)
    {
        this.exporter = exporter;

        var exporterType = exporter.GetType();
        var attributes = exporterType.GetCustomAttributes(typeof(ExportModesAttribute), true);
        if (attributes.Length > 0)
        {
            var attr = (ExportModesAttribute)attributes[attributes.Length - 1];
            this.SupportedExportModes = attr.Supported;
        }

        if (exporter is IPullMetricExporter pullExporter)
        {
            if (this.SupportedExportModes.HasFlag(ExportModes.Push))
            {
                pullExporter.Collect = this.Collect;
            }
            else
            {
                pullExporter.Collect = (timeoutMilliseconds) =>
                {
                    using (PullMetricScope.Begin())
                    {
                        return this.Collect(timeoutMilliseconds);
                    }
                };
            }
        }

        this.exportCalledMessage = $"{nameof(BaseExportingMetricReader)} calling {this.Exporter}.{nameof(this.Exporter.Export)} method.";
        this.exportSucceededMessage = $"{this.Exporter}.{nameof(this.Exporter.Export)} succeeded.";
        this.exportFailedMessage = $"{this.Exporter}.{nameof(this.Exporter.Export)} failed.";
    }

    internal BaseExporter<Metric> Exporter => this.exporter;

    protected ExportModes SupportedExportModes { get; } = ExportModes.Push | ExportModes.Pull;

    internal override void SetParentProvider(BaseProvider parentProvider)
    {
        base.SetParentProvider(parentProvider);
        this.exporter.ParentProvider = parentProvider;
    }

    internal override bool ProcessMetrics(in Batch<Metric> metrics, int timeoutMilliseconds)   // <--------------------------------------
    {
        try
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportCalledMessage);

            var result = this.exporter.Export(metrics);    // <----------------------------------!!

            if (result == ExportResult.Success)
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportSucceededMessage);
                return true;
            }
            else
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent(this.exportFailedMessage);
                return false;
            }
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.ProcessMetrics), ex);
            return false;
        }
    }

    protected override bool OnCollect(int timeoutMilliseconds)
    {
        if (this.SupportedExportModes.HasFlag(ExportModes.Push))
            return base.OnCollect(timeoutMilliseconds);

        if (this.SupportedExportModes.HasFlag(ExportModes.Pull) && PullMetricScope.IsPullAllowed)
            return base.OnCollect(timeoutMilliseconds);

        return false;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        var result = true;

        if (timeoutMilliseconds == Timeout.Infinite)
        {
            result = this.Collect(Timeout.Infinite) && result;
            result = this.exporter.Shutdown(Timeout.Infinite) && result;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            result = this.Collect(timeoutMilliseconds) && result;
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;
            result = this.exporter.Shutdown((int)Math.Max(timeout, 0)) && result;
        }

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                try
                {
                    if (this.exporter is IPullMetricExporter pullExporter)
                        pullExporter.Collect = null;

                    this.exporter.Dispose();
                }
                catch (Exception ex)
                {
                    OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Dispose), ex);
                }
            }
            this.disposed = true;
        }

        base.Dispose(disposing);
    }
}
//------------------------------------Ʌ

//-------------------------------------------V
public enum MetricReaderTemporalityPreference
{
    Cumulative = 1,
    Delta = 2,
}

public enum AggregationTemporality : byte
{
    Cumulative = 0b1,

    Delta = 0b10,
}
//-------------------------------------------Ʌ

//----------------------------------------V
public abstract partial class MetricReader : IDisposable
{
    private const MetricReaderTemporalityPreference MetricReaderTemporalityPreferenceUnspecified = (MetricReaderTemporalityPreference)0;

    private static readonly Func<Type, AggregationTemporality> CumulativeTemporalityPreferenceFunc =
        (instrumentType) => AggregationTemporality.Cumulative;

    private static readonly Func<Type, AggregationTemporality> MonotonicDeltaTemporalityPreferenceFunc = (instrumentType) =>
    {
        return instrumentType.GetGenericTypeDefinition() switch
        {
            var type when type == typeof(Counter<>) => AggregationTemporality.Delta,
            var type when type == typeof(ObservableCounter<>) => AggregationTemporality.Delta,
            var type when type == typeof(Histogram<>) => AggregationTemporality.Delta,

            // Temporality is not defined for gauges, so this does not really affect anything.
            var type when type == typeof(ObservableGauge<>) => AggregationTemporality.Delta,
            var type when type == typeof(Gauge<>) => AggregationTemporality.Delta,

            var type when type == typeof(UpDownCounter<>) => AggregationTemporality.Cumulative,
            var type when type == typeof(ObservableUpDownCounter<>) => AggregationTemporality.Cumulative,

            // TODO: Consider logging here because we should not fall through to this case.
            _ => AggregationTemporality.Delta,
        };
    };

    private readonly Lock newTaskLock = new();
    private readonly Lock onCollectLock = new();
    private readonly TaskCompletionSource<bool> shutdownTcs = new();
    private MetricReaderTemporalityPreference temporalityPreference = MetricReaderTemporalityPreferenceUnspecified;
    private Func<Type, AggregationTemporality> temporalityFunc = CumulativeTemporalityPreferenceFunc;
    private int shutdownCount;
    private TaskCompletionSource<bool>? collectionTcs;
    private BaseProvider? parentProvider;

    public MetricReaderTemporalityPreference TemporalityPreference
    {
        get
        {
            if (this.temporalityPreference == MetricReaderTemporalityPreferenceUnspecified)
            {
                this.temporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            }

            return this.temporalityPreference;
        }

        set
        {
            if (this.temporalityPreference != MetricReaderTemporalityPreferenceUnspecified)
            {
                throw new NotSupportedException($"The temporality preference cannot be modified (the current value is {this.temporalityPreference}).");
            }

            this.temporalityPreference = value;
            this.temporalityFunc = value switch
            {
                MetricReaderTemporalityPreference.Delta => MonotonicDeltaTemporalityPreferenceFunc,
                _ => CumulativeTemporalityPreferenceFunc,
            };
        }
    }

    public bool Collect(int timeoutMilliseconds = Timeout.Infinite)
    {
        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect method called.");
        var shouldRunCollect = false;
        var tcs = this.collectionTcs;

        if (tcs == null)
        {
            lock (this.newTaskLock)
            {
                tcs = this.collectionTcs;

                if (tcs == null)
                {
                    shouldRunCollect = true;
                    tcs = new TaskCompletionSource<bool>();
                    this.collectionTcs = tcs;
                }
            }
        }

        if (!shouldRunCollect)
            return Task.WaitAny([tcs.Task, this.shutdownTcs.Task], timeoutMilliseconds) == 0 && tcs.Task.Result;

        var result = false;
        try
        {
            lock (this.onCollectLock)
            {
                this.collectionTcs = null;
                result = this.OnCollect(timeoutMilliseconds);   // <-------------------------------------
            }
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Collect), ex);
        }

        tcs.TrySetResult(result);

        if (result)
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect succeeded.");
        else
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Collect failed.");

        return result;
    }

    protected virtual bool OnCollect(int timeoutMilliseconds)
    {
        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.OnCollect called.");

        var sw = timeoutMilliseconds == Timeout.Infinite ? null : Stopwatch.StartNew();

        var meterProviderSdk = this.parentProvider as MeterProviderSdk;
        meterProviderSdk?.CollectObservableInstruments();

        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("Observable instruments collected.");

        var metrics = this.GetMetricsBatch();   // <----------------------------------------------------

        bool result;
        if (sw == null)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics called.");
            result = this.ProcessMetrics(metrics, Timeout.Infinite);
            if (result)
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics succeeded.");
            else
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics failed.");

            return result;
        }
        else
        {
            var timeout = timeoutMilliseconds - sw.ElapsedMilliseconds;

            if (timeout <= 0)
            {
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("OnCollect failed timeout period has elapsed.");
                return false;
            }

            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics called.");
            result = this.ProcessMetrics(metrics, (int)timeout);
            if (result)
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics succeeded.");
            else
                OpenTelemetrySdkEventSource.Log.MetricReaderEvent("ProcessMetrics failed.");

            return result;
        }
    }

    public bool Shutdown(int timeoutMilliseconds = Timeout.Infinite)
    {
        Guard.ThrowIfInvalidTimeout(timeoutMilliseconds);

        OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown called.");

        if (Interlocked.CompareExchange(ref this.shutdownCount, 1, 0) != 0)
            return false; // shutdown already called

        var result = false;
        try
        {
            result = this.OnShutdown(timeoutMilliseconds);
        }
        catch (Exception ex)
        {
            OpenTelemetrySdkEventSource.Log.MetricReaderException(nameof(this.Shutdown), ex);
        }

        this.shutdownTcs.TrySetResult(result);

        if (result)
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown succeeded.");
        else
            OpenTelemetrySdkEventSource.Log.MetricReaderEvent("MetricReader.Shutdown failed.");

        return result;
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal virtual void SetParentProvider(BaseProvider parentProvider)
    {
        this.parentProvider = parentProvider;
    }

    internal virtual bool ProcessMetrics(in Batch<Metric> metrics, int timeoutMilliseconds)
    {
        return true;
    }

    protected virtual bool OnShutdown(int timeoutMilliseconds) => this.Collect(timeoutMilliseconds);

    protected virtual void Dispose(bool disposing){ }
}
//----------------------------------------Ʌ

//----------------------------------------V  from MetricReaderExt.cs
public abstract partial class MetricReader
{
    private readonly HashSet<string> metricStreamNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MetricStreamIdentity, Metric?> instrumentIdentityToMetric = new();
    private readonly Lock instrumentCreationLock = new();
    private int metricLimit;
    private int cardinalityLimit;
    private Metric?[]? metrics;
    private Metric[]? metricsCurrentBatch;
    private int metricIndex = -1;
    private ExemplarFilterType? exemplarFilter;
    private ExemplarFilterType? exemplarFilterForHistograms;

    internal static void DeactivateMetric(Metric metric)
    {
        if (metric.Active)
        {
            metric.Active = false;

            OpenTelemetrySdkEventSource.Log.MetricInstrumentDeactivated(
                metric.Name,
                metric.MeterName);
        }
    }

    internal AggregationTemporality GetAggregationTemporality(Type instrumentType)
    {
        return this.temporalityFunc(instrumentType);
    }

    internal virtual List<Metric> AddMetricWithNoViews(Instrument instrument)   // <---------------------------met2.4.2
    {
        var metricStreamIdentity = new MetricStreamIdentity(instrument!, metricStreamConfiguration: null);   // <---------------------------met2.4.3

        var exemplarFilter = metricStreamIdentity.IsHistogram
            ? this.exemplarFilterForHistograms ?? this.exemplarFilter
            : this.exemplarFilter;

        lock (this.instrumentCreationLock)
        {
            if (this.TryGetExistingMetric(in metricStreamIdentity, out var existingMetric))
            {
                return new() { existingMetric };
            }

            var index = ++this.metricIndex;
            if (index >= this.metricLimit)
            {
                OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Maximum allowed Metric streams for the provider exceeded.", "Use MeterProviderBuilder.AddView to drop unused instruments. Or use MeterProviderBuilder.SetMaxMetricStreams to configure MeterProvider to allow higher limit.");
                return new();
            }
            else
            {
                Metric? metric = null;
                try
                {
                    metric = new Metric(      // <---------------------------met2.4.4
                        metricStreamIdentity,
                        this.GetAggregationTemporality(metricStreamIdentity.InstrumentType),
                        this.cardinalityLimit,
                        exemplarFilter);
                }
                catch (NotSupportedException nse)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Unsupported instrument. Details: " + nse.Message, "Switch to a supported instrument type.");
                    return new();
                }

                this.instrumentIdentityToMetric[metricStreamIdentity] = metric;
                this.metrics![index] = metric;     // <---------------------------met2.4.5

                this.CreateOrUpdateMetricStreamRegistration(in metricStreamIdentity);

                return new() { metric };   // <---------------------------met2.4.5.
            }
        }
    }

    internal virtual List<Metric> AddMetricWithViews(Instrument instrument, List<MetricStreamConfiguration?> metricStreamConfigs)
    {
        var maxCountMetricsToBeCreated = metricStreamConfigs!.Count;

        var metrics = new List<Metric>(maxCountMetricsToBeCreated);
        lock (this.instrumentCreationLock)
        {
            for (int i = 0; i < maxCountMetricsToBeCreated; i++)
            {
                var metricStreamConfig = metricStreamConfigs[i];
                var metricStreamIdentity = new MetricStreamIdentity(instrument!, metricStreamConfig);

                var exemplarFilter = metricStreamIdentity.IsHistogram
                    ? this.exemplarFilterForHistograms ?? this.exemplarFilter
                    : this.exemplarFilter;

                if (!MeterProviderBuilderSdk.IsValidInstrumentName(metricStreamIdentity.InstrumentName))
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(
                        metricStreamIdentity.InstrumentName,
                        metricStreamIdentity.MeterName,
                        "Metric name is invalid.",
                        "The name must comply with the OpenTelemetry specification.");

                    continue;
                }

                if (this.TryGetExistingMetric(in metricStreamIdentity, out var existingMetric))
                {
                    metrics.Add(existingMetric);
                    continue;
                }

                if (metricStreamConfig == MetricStreamConfiguration.Drop)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "View configuration asks to drop this instrument.", "Modify view configuration to allow this instrument, if desired.");
                    continue;
                }

                var index = ++this.metricIndex;
                if (index >= this.metricLimit)
                {
                    OpenTelemetrySdkEventSource.Log.MetricInstrumentIgnored(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName, "Maximum allowed Metric streams for the provider exceeded.", "Use MeterProviderBuilder.AddView to drop unused instruments. Or use MeterProviderBuilder.SetMaxMetricStreams to configure MeterProvider to allow higher limit.");
                }
                else
                {
                    Metric metric = new(
                        metricStreamIdentity,
                        this.GetAggregationTemporality(metricStreamIdentity.InstrumentType),
                        metricStreamConfig?.CardinalityLimit ?? this.cardinalityLimit,
                        exemplarFilter,
                        metricStreamConfig?.ExemplarReservoirFactory);

                    this.instrumentIdentityToMetric[metricStreamIdentity] = metric;
                    this.metrics![index] = metric;
                    metrics.Add(metric);

                    this.CreateOrUpdateMetricStreamRegistration(in metricStreamIdentity);
                }
            }

            return metrics;
        }
    }

    internal void ApplyParentProviderSettings(int metricLimit, int cardinalityLimit, ExemplarFilterType? exemplarFilter, ExemplarFilterType? exemplarFilterForHistograms)
    {
        this.metricLimit = metricLimit;
        this.metrics = new Metric[metricLimit];
        this.metricsCurrentBatch = new Metric[metricLimit];
        this.cardinalityLimit = cardinalityLimit;
        this.exemplarFilter = exemplarFilter;
        this.exemplarFilterForHistograms = exemplarFilterForHistograms;
    }

    private bool TryGetExistingMetric(in MetricStreamIdentity metricStreamIdentity, [NotNullWhen(true)] out Metric? existingMetric)
        => this.instrumentIdentityToMetric.TryGetValue(metricStreamIdentity, out existingMetric)
            && existingMetric != null && existingMetric.Active;

    private void CreateOrUpdateMetricStreamRegistration(in MetricStreamIdentity metricStreamIdentity)
    {
        if (!this.metricStreamNames.Add(metricStreamIdentity.MetricStreamName))
        {
            OpenTelemetrySdkEventSource.Log.DuplicateMetricInstrument(metricStreamIdentity.InstrumentName, metricStreamIdentity.MeterName,
                "Metric instrument has the same name as an existing one but differs by description, unit, or instrument type. Measurements from this instrument will still be exported but may result in conflicts.", "Either change the name of the instrument or use MeterProviderBuilder.AddView to resolve the conflict.");
        }
    }

    private Batch<Metric> GetMetricsBatch()   // <---------------------------------------
    {      
        try
        {
            var indexSnapshot = Math.Min(this.metricIndex, this.metricLimit - 1);
            var target = indexSnapshot + 1;
            int metricCountCurrentBatch = 0;
            for (int i = 0; i < target; i++)
            {
                ref var metric = ref this.metrics![i];
                if (metric != null)
                {
                    int metricPointSize = metric.Snapshot();  // <-------! exporter takes a snapshot to capture the current values of all metrics at that exact moment in time

                    if (metricPointSize > 0)
                        this.metricsCurrentBatch![metricCountCurrentBatch++] = metric;

                    if (!metric.Active)
                        this.RemoveMetric(ref metric);
                }
            }

            return (metricCountCurrentBatch > 0) ? new Batch<Metric>(this.metricsCurrentBatch!, metricCountCurrentBatch) : default;
        }
        catch (Exception ex)
        {
            return default;
        }
    }

    private void RemoveMetric(ref Metric? metric)
    {             
        this.instrumentIdentityToMetric.TryUpdate(metric.InstrumentIdentity, null, metric);
   
        metric = null;
    }
}
//----------------------------------------Ʌ
```

=====================================================================================================================

```C#
//-------------------------------------V
[StructLayout(LayoutKind.Explicit)]
internal struct MetricPointValueStorage
{
    [FieldOffset(0)]
    public long AsLong;   // <------------------

    [FieldOffset(0)]
    public double AsDouble;
}
//-------------------------------------Ʌ
```


```C#
//-----------------------------------V
internal sealed class AggregatorStore
{
    internal readonly FrozenSet<string>? TagKeysInteresting;
    internal readonly bool OutputDelta;
    internal readonly int NumberOfMetricPoints;
    internal readonly ConcurrentDictionary<Tags, LookupData>? TagsToMetricPointIndexDictionaryDelta;
    internal readonly Func<ExemplarReservoir?>? ExemplarReservoirFactory;
    internal long DroppedMeasurements;

    private const ExemplarFilterType DefaultExemplarFilter = ExemplarFilterType.AlwaysOff;
    private static readonly Comparison<KeyValuePair<string, object?>> DimensionComparisonDelegate = (x, y) => string.Compare(x.Key, y.Key, StringComparison.Ordinal);

    private readonly Lock lockZeroTags = new();
    private readonly Lock lockOverflowTag = new();
    private readonly int tagsKeysInterestingCount;

    // This holds the reclaimed MetricPoints that are available for reuse.
    private readonly Queue<int>? availableMetricPoints;

    private readonly ConcurrentDictionary<Tags, int> tagsToMetricPointIndexDictionary = new();

    private readonly string name;
    private readonly MetricPoint[] metricPoints;
    private readonly int[] currentMetricPointBatch;
    private readonly AggregationType aggType;
    private readonly double[] histogramBounds;
    private readonly int exponentialHistogramMaxSize;
    private readonly int exponentialHistogramMaxScale;
    private readonly UpdateLongDelegate updateLongCallback;
    private readonly UpdateDoubleDelegate updateDoubleCallback;
    private readonly ExemplarFilterType exemplarFilter;
    private readonly Func<KeyValuePair<string, object?>[], int, int> lookupAggregatorStore;

    private int metricPointIndex;
    private int batchSize;
    private bool zeroTagMetricPointInitialized;
    private bool overflowTagMetricPointInitialized;

    internal AggregatorStore(
        MetricStreamIdentity metricStreamIdentity,
        AggregationType aggType,
        AggregationTemporality temporality,
        int cardinalityLimit,
        ExemplarFilterType? exemplarFilter = null,
        Func<ExemplarReservoir?>? exemplarReservoirFactory = null)
    {
        this.name = metricStreamIdentity.InstrumentName;

        // Increase the CardinalityLimit by 2 to reserve additional space.
        // This adjustment accounts for overflow attribute and a case where zero tags are provided.
        // Previously, these were included within the original cardinalityLimit, but now they are explicitly added to enhance clarity.
        this.NumberOfMetricPoints = cardinalityLimit + 2;

        this.metricPoints = new MetricPoint[this.NumberOfMetricPoints];   // <-------------------------------
        this.currentMetricPointBatch = new int[this.NumberOfMetricPoints];
        this.aggType = aggType;
        this.OutputDelta = temporality == AggregationTemporality.Delta;
        this.histogramBounds = metricStreamIdentity.HistogramBucketBounds ?? FindDefaultHistogramBounds(in metricStreamIdentity);
        this.exponentialHistogramMaxSize = metricStreamIdentity.ExponentialHistogramMaxSize;
        this.exponentialHistogramMaxScale = metricStreamIdentity.ExponentialHistogramMaxScale;
        this.StartTimeExclusive = DateTimeOffset.UtcNow;
        this.ExemplarReservoirFactory = exemplarReservoirFactory;
        if (metricStreamIdentity.TagKeys == null)
        {
            this.updateLongCallback = this.UpdateLong;   // <----------------------------met4.1 (reference)
            this.updateDoubleCallback = this.UpdateDouble;
        }
        else
        {
            this.updateLongCallback = this.UpdateLongCustomTags;  // <----------------------------met4.1 (reference)
            this.updateDoubleCallback = this.UpdateDoubleCustomTags;
            var hs = FrozenSet.ToFrozenSet(metricStreamIdentity.TagKeys, StringComparer.Ordinal);

            this.TagKeysInteresting = hs;
            this.tagsKeysInterestingCount = hs.Count;
        }

        this.exemplarFilter = exemplarFilter ?? DefaultExemplarFilter;

        // Setting metricPointIndex to 1 as we would reserve the metricPoints[1] for overflow attribute.
        // Newer attributes should be added starting at the index: 2
        this.metricPointIndex = 1;

        // Always reclaim unused MetricPoints for Delta aggregation temporality
        if (this.OutputDelta)
        {
            this.availableMetricPoints = new Queue<int>(cardinalityLimit);

            // There is no overload which only takes capacity as the parameter
            // Using the DefaultConcurrencyLevel defined in the ConcurrentDictionary class: https://github.com/dotnet/runtime/blob/v7.0.5/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs#L2020
            // We expect at the most (user provided cardinality limit) * 2 entries- one for sorted and one for unsorted input
            this.TagsToMetricPointIndexDictionaryDelta =
                new ConcurrentDictionary<Tags, LookupData>(concurrencyLevel: Environment.ProcessorCount, capacity: cardinalityLimit * 2);

            // Add all the indices except for the reserved ones to the queue so that threads have
            // readily available access to these MetricPoints for their use.
            // Index 0 and 1 are reserved for no tags and overflow
            for (int i = 2; i < this.NumberOfMetricPoints; i++)
            {
                this.availableMetricPoints.Enqueue(i);
            }

            this.lookupAggregatorStore = this.LookupAggregatorStoreForDeltaWithReclaim;
        }
        else
        {
            this.lookupAggregatorStore = this.LookupAggregatorStore;
        }
    }

    private delegate void UpdateLongDelegate(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags);

    private delegate void UpdateDoubleDelegate(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags);

    internal DateTimeOffset StartTimeExclusive { get; private set; }

    internal DateTimeOffset EndTimeInclusive { get; private set; }

    internal double[] HistogramBounds => this.histogramBounds;

    internal bool IsExemplarEnabled()
    {
        // Using this filter to indicate On/Off
        // instead of another separate flag.
        return this.exemplarFilter != ExemplarFilterType.AlwaysOff;
    }

    internal void Update(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)  // <----------------------------met4.0
    {
        try
        { 
            this.updateLongCallback(value, tags);  // <----------------------------met4.1
        }
        catch (Exception)
        {
            Interlocked.Increment(ref this.DroppedMeasurements);
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
        }
    }

    internal void Update(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        try
        {
            this.updateDoubleCallback(value, tags);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref this.DroppedMeasurements);
            OpenTelemetrySdkEventSource.Log.MeasurementDropped(this.name, "SDK internal error occurred.", "Contact SDK owners.");
        }
    }

    internal int Snapshot()
    {
        this.batchSize = 0;
        if (this.OutputDelta)
        {
            this.SnapshotDeltaWithMetricPointReclaim();
        }
        else
        {
            var indexSnapshot = Math.Min(this.metricPointIndex, this.NumberOfMetricPoints - 1);
            this.SnapshotCumulative(indexSnapshot);
        }

        this.EndTimeInclusive = DateTimeOffset.UtcNow;
        return this.batchSize;
    }

    internal void SnapshotDeltaWithMetricPointReclaim()
    {
        // Index = 0 is reserved for the case where no dimensions are provided.
        ref var metricPointWithNoTags = ref this.metricPoints[0];
        if (metricPointWithNoTags.MetricPointStatus != MetricPointStatus.NoCollectPending)
        {
            this.TakeMetricPointSnapshot(ref metricPointWithNoTags, outputDelta: true);

            this.currentMetricPointBatch[this.batchSize] = 0;
            this.batchSize++;
        }

        // TakeSnapshot for the MetricPoint for overflow
        ref var metricPointForOverflow = ref this.metricPoints[1];
        if (metricPointForOverflow.MetricPointStatus != MetricPointStatus.NoCollectPending)
        {
            this.TakeMetricPointSnapshot(ref metricPointForOverflow, outputDelta: true);

            this.currentMetricPointBatch[this.batchSize] = 1;
            this.batchSize++;
        }

        // Index 0 and 1 are reserved for no tags and overflow
        for (int i = 2; i < this.NumberOfMetricPoints; i++)
        {
            ref var metricPoint = ref this.metricPoints[i];

            if (metricPoint.MetricPointStatus == MetricPointStatus.NoCollectPending)
            {
                // Reclaim the MetricPoint if it was marked for it in the previous collect cycle
                if (metricPoint.LookupData != null && metricPoint.LookupData.DeferredReclaim == true)
                {
                    this.ReclaimMetricPoint(ref metricPoint, i);
                    continue;
                }

                // Check if the MetricPoint could be reclaimed in the current Collect cycle.
                // If metricPoint.LookupData is `null` then the MetricPoint is already reclaimed and in the queue.
                // If the Collect thread is successfully able to compare and swap the reference count from zero to int.MinValue, it means that
                // the MetricPoint can be reused for other tags.
                if (metricPoint.LookupData != null && Interlocked.CompareExchange(ref metricPoint.ReferenceCount, int.MinValue, 0) == 0)
                {
                    // This is similar to double-checked locking. For some rare case, the Collect thread might read the status as `NoCollectPending`,
                    // and then get switched out before it could set the ReferenceCount to `int.MinValue`. In the meantime, an Update thread could come in
                    // and update the MetricPoint, thereby, setting its status to `CollectPending`. Note that the ReferenceCount would be 0 after the update.
                    // If the Collect thread now wakes up, it would be able to set the ReferenceCount to `int.MinValue`, thereby, marking the MetricPoint
                    // invalid for newer updates. In such cases, the MetricPoint, should not be reclaimed before taking its Snapshot.

                    if (metricPoint.MetricPointStatus == MetricPointStatus.NoCollectPending)
                    {
                        this.ReclaimMetricPoint(ref metricPoint, i);
                    }
                    else
                    {
                        // MetricPoint's ReferenceCount is `int.MinValue` but it still has a collect pending. Take the MetricPoint's Snapshot
                        // and mark it to be reclaimed in the next Collect cycle.

                        metricPoint.LookupData.DeferredReclaim = true;

                        this.TakeMetricPointSnapshot(ref metricPoint, outputDelta: true);

                        this.currentMetricPointBatch[this.batchSize] = i;
                        this.batchSize++;
                    }
                }

                continue;
            }

            this.TakeMetricPointSnapshot(ref metricPoint, outputDelta: true);

            this.currentMetricPointBatch[this.batchSize] = i;
            this.batchSize++;
        }

        if (this.EndTimeInclusive != default)
        {
            this.StartTimeExclusive = this.EndTimeInclusive;
        }
    }

    internal void SnapshotCumulative(int indexSnapshot)
    {
        for (int i = 0; i <= indexSnapshot; i++)
        {
            ref var metricPoint = ref this.metricPoints[i];
            if (!metricPoint.IsInitialized)
            {
                continue;
            }

            this.TakeMetricPointSnapshot(ref metricPoint, outputDelta: false);

            this.currentMetricPointBatch[this.batchSize] = i;
            this.batchSize++;
        }
    }

    internal MetricPointsAccessor GetMetricPoints()
        => new(this.metricPoints, this.currentMetricPointBatch, this.batchSize);

    private static double[] FindDefaultHistogramBounds(in MetricStreamIdentity metricStreamIdentity)
    {
        if (metricStreamIdentity.Unit == "s")
        {
            if (Metric.DefaultHistogramBoundShortMappings.Contains((metricStreamIdentity.MeterName, metricStreamIdentity.InstrumentName)))
            {
                return Metric.DefaultHistogramBoundsShortSeconds;
            }

            if (Metric.DefaultHistogramBoundLongMappings.Contains((metricStreamIdentity.MeterName, metricStreamIdentity.InstrumentName)))
            {
                return Metric.DefaultHistogramBoundsLongSeconds;
            }
        }

        return Metric.DefaultHistogramBounds;
    }

    private void TakeMetricPointSnapshot(ref MetricPoint metricPoint, bool outputDelta)
    {
        if (this.IsExemplarEnabled())
        {
            metricPoint.TakeSnapshotWithExemplar(outputDelta);
        }
        else
        {
            metricPoint.TakeSnapshot(outputDelta);
        }
    }

    private void ReclaimMetricPoint(ref MetricPoint metricPoint, int metricPointIndex)
    {
        /*
         This method does three things:
          1. Set `metricPoint.LookupData` and `metricPoint.mpComponents` to `null` to have them collected faster by GC.
          2. Tries to remove the entry for this MetricPoint from the lookup dictionary. An update thread which retrieves this
             MetricPoint would realize that the MetricPoint is not valid for use since its reference count would have been set to a negative number.
             When that happens, the update thread would also try to remove the entry for this MetricPoint from the lookup dictionary.
             We only care about the entry getting removed from the lookup dictionary and not about which thread removes it.
          3. Put the array index of this MetricPoint to the queue of available metric points. This makes it available for update threads
             to use this MetricPoint to track newer dimension combinations.
        */

        var lookupData = metricPoint.LookupData;

        metricPoint.NullifyMetricPointState();

        lock (this.TagsToMetricPointIndexDictionaryDelta!)
        {
            LookupData? dictionaryValue;
            if (lookupData!.SortedTags != Tags.EmptyTags)
            {
                // Check if no other thread added a new entry for the same Tags.
                // If no, then remove the existing entries.
                if (this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(lookupData.SortedTags, out dictionaryValue) &&
                    dictionaryValue == lookupData)
                {
                    this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.SortedTags, out var _);
                    this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.GivenTags, out var _);
                }
            }
            else
            {
                if (this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(lookupData.GivenTags, out dictionaryValue) &&
                    dictionaryValue == lookupData)
                {
                    this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.GivenTags, out var _);
                }
            }

            Debug.Assert(this.availableMetricPoints != null, "this.availableMetricPoints was null");

            this.availableMetricPoints!.Enqueue(metricPointIndex);
        }
    }

    private void InitializeZeroTagPointIfNotInitialized()  // <----------------------------met4.3
    {
        if (!this.zeroTagMetricPointInitialized)
        {
            lock (this.lockZeroTags)
            {
                if (!this.zeroTagMetricPointInitialized)
                {
                    if (this.OutputDelta)
                    {
                        var lookupData = new LookupData(0, Tags.EmptyTags, Tags.EmptyTags);
                        this.metricPoints[0] = new MetricPoint(this, this.aggType, null, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                    }
                    else
                    {
                        this.metricPoints[0] = new MetricPoint(this, this.aggType, null, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale);
                    }

                    this.zeroTagMetricPointInitialized = true;
                }
            }
        }

        /*

        internal sealed class LookupData
        {
            public bool DeferredReclaim;
            public int Index;
            public Tags SortedTags;
            public Tags GivenTags;

            public LookupData(int index, in Tags sortedTags, in Tags givenTags) { ... }
        }
        
        */
    }

    private void InitializeOverflowTagPointIfNotInitialized()
    {
        if (!this.overflowTagMetricPointInitialized)
        {
            lock (this.lockOverflowTag)
            {
                if (!this.overflowTagMetricPointInitialized)
                {
                    var keyValuePairs = new KeyValuePair<string, object?>[] { new("otel.metric.overflow", true) };
                    var tags = new Tags(keyValuePairs);

                    if (this.OutputDelta)
                    {
                        var lookupData = new LookupData(1, tags, tags);
                        this.metricPoints[1] = new MetricPoint(this, this.aggType, keyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                    }
                    else
                    {
                        this.metricPoints[1] = new MetricPoint(this, this.aggType, keyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale);
                    }

                    this.overflowTagMetricPointInitialized = true;
                }
            }
        }
    }

    private int LookupAggregatorStore(KeyValuePair<string, object?>[] tagKeysAndValues, int length)
    {
        var givenTags = new Tags(tagKeysAndValues);

        if (!this.tagsToMetricPointIndexDictionary.TryGetValue(givenTags, out var aggregatorIndex))
        {
            if (length > 1)
            {
                // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                // Create or obtain new arrays to temporarily hold the sorted tag Keys and Values
                var storage = ThreadStaticStorage.GetStorage();
                storage.CloneKeysAndValues(tagKeysAndValues, length, out var tempSortedTagKeysAndValues);

                Array.Sort(tempSortedTagKeysAndValues, DimensionComparisonDelegate);

                var sortedTags = new Tags(tempSortedTagKeysAndValues);

                if (!this.tagsToMetricPointIndexDictionary.TryGetValue(sortedTags, out aggregatorIndex))
                {
                    aggregatorIndex = this.metricPointIndex;
                    if (aggregatorIndex >= this.NumberOfMetricPoints)
                    {
                        // sorry! out of data points.
                        // TODO: Once we support cleanup of
                        // unused points (typically with delta)
                        // we can re-claim them here.
                        return -1;
                    }

                    // Note: We are using storage from ThreadStatic (for upto MaxTagCacheSize tags) for both the input order of tags and the sorted order of tags,
                    // so we need to make a deep copy for Dictionary storage.
                    if (length <= ThreadStaticStorage.MaxTagCacheSize)
                    {
                        var givenTagKeysAndValues = new KeyValuePair<string, object?>[length];
                        tagKeysAndValues.CopyTo(givenTagKeysAndValues.AsSpan());

                        var sortedTagKeysAndValues = new KeyValuePair<string, object?>[length];
                        tempSortedTagKeysAndValues.CopyTo(sortedTagKeysAndValues.AsSpan());

                        givenTags = new Tags(givenTagKeysAndValues);
                        sortedTags = new Tags(sortedTagKeysAndValues);
                    }

                    lock (this.tagsToMetricPointIndexDictionary)
                    {
                        // check again after acquiring lock.
                        if (!this.tagsToMetricPointIndexDictionary.TryGetValue(sortedTags, out aggregatorIndex))
                        {
                            aggregatorIndex = ++this.metricPointIndex;
                            if (aggregatorIndex >= this.NumberOfMetricPoints)
                            {
                                // sorry! out of data points.
                                // TODO: Once we support cleanup of
                                // unused points (typically with delta)
                                // we can re-claim them here.
                                return -1;
                            }

                            ref var metricPoint = ref this.metricPoints[aggregatorIndex];
                            metricPoint = new MetricPoint(this, this.aggType, sortedTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale);

                            // Add to dictionary *after* initializing MetricPoint
                            // as other threads can start writing to the
                            // MetricPoint, if dictionary entry found.

                            // Add the sorted order along with the given order of tags
                            this.tagsToMetricPointIndexDictionary.TryAdd(sortedTags, aggregatorIndex);
                            this.tagsToMetricPointIndexDictionary.TryAdd(givenTags, aggregatorIndex);
                        }
                    }
                }
            }
            else
            {
                // This else block is for tag length = 1
                aggregatorIndex = this.metricPointIndex;
                if (aggregatorIndex >= this.NumberOfMetricPoints)
                {
                    return -1;
                }

                // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                var givenTagKeysAndValues = new KeyValuePair<string, object?>[length];

                tagKeysAndValues.CopyTo(givenTagKeysAndValues.AsSpan());

                givenTags = new Tags(givenTagKeysAndValues);

                lock (this.tagsToMetricPointIndexDictionary)
                {
                    // check again after acquiring lock.
                    if (!this.tagsToMetricPointIndexDictionary.TryGetValue(givenTags, out aggregatorIndex))
                    {
                        aggregatorIndex = ++this.metricPointIndex;
                        if (aggregatorIndex >= this.NumberOfMetricPoints)
                        {
                            // sorry! out of data points.
                            // TODO: Once we support cleanup of
                            // unused points (typically with delta)
                            // we can re-claim them here.
                            return -1;
                        }

                        ref var metricPoint = ref this.metricPoints[aggregatorIndex];
                        metricPoint = new MetricPoint(this, this.aggType, givenTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale);

                        // Add to dictionary *after* initializing MetricPoint
                        // as other threads can start writing to the
                        // MetricPoint, if dictionary entry found.

                        // givenTags will always be sorted when tags length == 1
                        this.tagsToMetricPointIndexDictionary.TryAdd(givenTags, aggregatorIndex);
                    }
                }
            }
        }

        return aggregatorIndex;
    }

    private int LookupAggregatorStoreForDeltaWithReclaim(KeyValuePair<string, object?>[] tagKeysAndValues, int length)
    {
        int index;
        var givenTags = new Tags(tagKeysAndValues);

        bool newMetricPointCreated = false;

        if (!this.TagsToMetricPointIndexDictionaryDelta!.TryGetValue(givenTags, out var lookupData))
        {
            if (length > 1)
            {
                // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                // Create or obtain new arrays to temporarily hold the sorted tag Keys and Values
                var storage = ThreadStaticStorage.GetStorage();
                storage.CloneKeysAndValues(tagKeysAndValues, length, out var tempSortedTagKeysAndValues);

                Array.Sort(tempSortedTagKeysAndValues, DimensionComparisonDelegate);

                var sortedTags = new Tags(tempSortedTagKeysAndValues);

                if (!this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(sortedTags, out lookupData))
                {
                    // Note: We are using storage from ThreadStatic (for up to MaxTagCacheSize tags) for both the input order of tags and the sorted order of tags,
                    // so we need to make a deep copy for Dictionary storage.
                    if (length <= ThreadStaticStorage.MaxTagCacheSize)
                    {
                        var givenTagKeysAndValues = new KeyValuePair<string, object?>[length];
                        tagKeysAndValues.CopyTo(givenTagKeysAndValues.AsSpan());

                        var sortedTagKeysAndValues = new KeyValuePair<string, object?>[length];
                        tempSortedTagKeysAndValues.CopyTo(sortedTagKeysAndValues.AsSpan());

                        givenTags = new Tags(givenTagKeysAndValues);
                        sortedTags = new Tags(sortedTagKeysAndValues);
                    }

                    lock (this.TagsToMetricPointIndexDictionaryDelta)
                    {
                        // check again after acquiring lock.
                        if (!this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(sortedTags, out lookupData))
                        {
                            // Check for an available MetricPoint
                            if (this.availableMetricPoints!.Count > 0)
                            {
                                index = this.availableMetricPoints.Dequeue();
                            }
                            else
                            {
                                // No MetricPoint is available for reuse
                                return -1;
                            }

                            lookupData = new LookupData(index, sortedTags, givenTags);

                            ref var metricPoint = ref this.metricPoints[index];
                            metricPoint = new MetricPoint(this, this.aggType, sortedTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                            newMetricPointCreated = true;

                            // Add to dictionary *after* initializing MetricPoint
                            // as other threads can start writing to the
                            // MetricPoint, if dictionary entry found.

                            // Add the sorted order along with the given order of tags
                            this.TagsToMetricPointIndexDictionaryDelta.TryAdd(sortedTags, lookupData);
                            this.TagsToMetricPointIndexDictionaryDelta.TryAdd(givenTags, lookupData);
                        }
                    }
                }
            }
            else
            {
                // This else block is for tag length = 1

                // Note: We are using storage from ThreadStatic, so need to make a deep copy for Dictionary storage.
                var givenTagKeysAndValues = new KeyValuePair<string, object?>[length];

                tagKeysAndValues.CopyTo(givenTagKeysAndValues.AsSpan());

                givenTags = new Tags(givenTagKeysAndValues);

                lock (this.TagsToMetricPointIndexDictionaryDelta)
                {
                    // check again after acquiring lock.
                    if (!this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(givenTags, out lookupData))
                    {
                        // Check for an available MetricPoint
                        if (this.availableMetricPoints!.Count > 0)
                        {
                            index = this.availableMetricPoints.Dequeue();
                        }
                        else
                        {
                            // No MetricPoint is available for reuse
                            return -1;
                        }

                        lookupData = new LookupData(index, Tags.EmptyTags, givenTags);

                        ref var metricPoint = ref this.metricPoints[index];
                        metricPoint = new MetricPoint(this, this.aggType, givenTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                        newMetricPointCreated = true;

                        // Add to dictionary *after* initializing MetricPoint
                        // as other threads can start writing to the
                        // MetricPoint, if dictionary entry found.

                        // givenTags will always be sorted when tags length == 1
                        this.TagsToMetricPointIndexDictionaryDelta.TryAdd(givenTags, lookupData);
                    }
                }
            }
        }

        // Found the MetricPoint
        index = lookupData.Index;

        // If the running thread created a new MetricPoint, then the Snapshot method cannot reclaim that MetricPoint because MetricPoint is initialized with a ReferenceCount of 1.
        // It can simply return the index.

        if (!newMetricPointCreated)
        {
            // If the running thread did not create the MetricPoint, it could be working on an index that has been reclaimed by Snapshot method.
            // This could happen if the thread get switched out by CPU after it retrieves the index but the Snapshot method reclaims it before the thread wakes up again.

            ref var metricPointAtIndex = ref this.metricPoints[index];
            var referenceCount = Interlocked.Increment(ref metricPointAtIndex.ReferenceCount);

            if (referenceCount < 0)
            {
                index = this.RemoveStaleEntriesAndGetAvailableMetricPointRare(lookupData, length);
            }
            else if (metricPointAtIndex.LookupData != lookupData)
            {
                Interlocked.Decrement(ref metricPointAtIndex.ReferenceCount);

                // Retry attempt to get a MetricPoint.
                index = this.RemoveStaleEntriesAndGetAvailableMetricPointRare(lookupData, length);
            }
        }

        return index;
    }

    // this method is always called under `lock(this.tagsToMetricPointIndexDictionaryDelta)` so it's safe with other code that adds or removes
    // entries from `this.tagsToMetricPointIndexDictionaryDelta`
    private bool TryGetAvailableMetricPointRare(Tags givenTags, Tags sortedTags, int length, [NotNullWhen(true)] out LookupData? lookupData, out bool newMetricPointCreated)
    {
        int index;
        newMetricPointCreated = false;

        if (length > 1)
        {
            // check again after acquiring lock.
            if (!this.TagsToMetricPointIndexDictionaryDelta!.TryGetValue(givenTags, out lookupData) &&
                !this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(sortedTags, out lookupData))
            {
                // Check for an available MetricPoint
                if (this.availableMetricPoints!.Count > 0)
                {
                    index = this.availableMetricPoints.Dequeue();
                }
                else
                {
                    // No MetricPoint is available for reuse
                    return false;
                }

                lookupData = new LookupData(index, sortedTags, givenTags);

                ref var metricPoint = ref this.metricPoints[index];
                metricPoint = new MetricPoint(this, this.aggType, sortedTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                newMetricPointCreated = true;

                // Add to dictionary *after* initializing MetricPoint
                // as other threads can start writing to the
                // MetricPoint, if dictionary entry found.

                // Add the sorted order along with the given order of tags
                this.TagsToMetricPointIndexDictionaryDelta.TryAdd(sortedTags, lookupData);
                this.TagsToMetricPointIndexDictionaryDelta.TryAdd(givenTags, lookupData);
            }
        }
        else
        {
            // check again after acquiring lock.
            if (!this.TagsToMetricPointIndexDictionaryDelta!.TryGetValue(givenTags, out lookupData))
            {
                // Check for an available MetricPoint
                if (this.availableMetricPoints!.Count > 0)
                {
                    index = this.availableMetricPoints.Dequeue();
                }
                else
                {
                    // No MetricPoint is available for reuse
                    return false;
                }

                lookupData = new LookupData(index, Tags.EmptyTags, givenTags);

                ref var metricPoint = ref this.metricPoints[index];
                metricPoint = new MetricPoint(this, this.aggType, givenTags.KeyValuePairs, this.histogramBounds, this.exponentialHistogramMaxSize, this.exponentialHistogramMaxScale, lookupData);
                newMetricPointCreated = true;

                // Add to dictionary *after* initializing MetricPoint
                // as other threads can start writing to the
                // MetricPoint, if dictionary entry found.

                // givenTags will always be sorted when tags length == 1
                this.TagsToMetricPointIndexDictionaryDelta.TryAdd(givenTags, lookupData);
            }
        }

        return true;
    }

    // This method is essentially a retry attempt for when `LookupAggregatorStoreForDeltaWithReclaim` cannot find a MetricPoint.
    // If we still fail to get a MetricPoint in this method, we don't retry any further and simply drop the measurement.
    // This method acquires `lock (this.tagsToMetricPointIndexDictionaryDelta)`
    private int RemoveStaleEntriesAndGetAvailableMetricPointRare(LookupData lookupData, int length)
    {
        bool foundMetricPoint = false;
        bool newMetricPointCreated = false;
        var sortedTags = lookupData.SortedTags;
        var inputTags = lookupData.GivenTags;

        // Acquire lock
        // Try to remove stale entries from dictionary
        // Get the index for a new MetricPoint (it could be self-claimed or from another thread that added a fresh entry)
        // If self-claimed, then add a fresh entry to the dictionary
        // If an available MetricPoint is found, then only increment the ReferenceCount

        // Delete the entry for these Tags and get another MetricPoint.
        lock (this.TagsToMetricPointIndexDictionaryDelta!)
        {
            LookupData? dictionaryValue;
            if (lookupData.SortedTags != Tags.EmptyTags)
            {
                // Check if no other thread added a new entry for the same Tags in the meantime.
                // If no, then remove the existing entries.
                if (this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(lookupData.SortedTags, out dictionaryValue))
                {
                    if (dictionaryValue == lookupData)
                    {
                        // No other thread added a new entry for the same Tags.
                        this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.SortedTags, out _);
                        this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.GivenTags, out _);
                    }
                    else
                    {
                        // Some other thread added a new entry for these Tags. Use the new MetricPoint
                        lookupData = dictionaryValue;
                        foundMetricPoint = true;
                    }
                }
            }
            else
            {
                if (this.TagsToMetricPointIndexDictionaryDelta.TryGetValue(lookupData.GivenTags, out dictionaryValue))
                {
                    if (dictionaryValue == lookupData)
                    {
                        // No other thread added a new entry for the same Tags.
                        this.TagsToMetricPointIndexDictionaryDelta.TryRemove(lookupData.GivenTags, out _);
                    }
                    else
                    {
                        // Some other thread added a new entry for these Tags. Use the new MetricPoint
                        lookupData = dictionaryValue;
                        foundMetricPoint = true;
                    }
                }
            }

            if (!foundMetricPoint
                && this.TryGetAvailableMetricPointRare(inputTags, sortedTags, length, out var tempLookupData, out newMetricPointCreated))
            {
                foundMetricPoint = true;
                lookupData = tempLookupData;
            }
        }

        if (foundMetricPoint)
        {
            var index = lookupData.Index;

            // If the running thread created a new MetricPoint, then the Snapshot method cannot reclaim that MetricPoint because MetricPoint is initialized with a ReferenceCount of 1.
            // It can simply return the index.

            if (!newMetricPointCreated)
            {
                // If the running thread did not create the MetricPoint, it could be working on an index that has been reclaimed by Snapshot method.
                // This could happen if the thread get switched out by CPU after it retrieves the index but the Snapshot method reclaims it before the thread wakes up again.

                ref var metricPointAtIndex = ref this.metricPoints[index];
                var referenceCount = Interlocked.Increment(ref metricPointAtIndex.ReferenceCount);

                if (referenceCount < 0)
                {
                    // Super rare case: Snapshot method had already marked the MetricPoint available for reuse as it has not been updated in last collect cycle even in the retry attempt.
                    // Example scenario mentioned in `LookupAggregatorStoreForDeltaWithReclaim` method.

                    // Don't retry again and drop the measurement.
                    return -1;
                }
                else if (metricPointAtIndex.LookupData != lookupData)
                {
                    // Rare case: Another thread with different input tags could have reclaimed this MetricPoint if it was freed up by Snapshot method even in the retry attempt.
                    // Example scenario mentioned in `LookupAggregatorStoreForDeltaWithReclaim` method.

                    // Remove reference since its not the right MetricPoint.
                    Interlocked.Decrement(ref metricPointAtIndex.ReferenceCount);

                    // Don't retry again and drop the measurement.
                    return -1;
                }
            }

            return index;
        }
        else
        {
            // No MetricPoint is available for reuse
            return -1;
        }
    }

    private void UpdateLong(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)   // <----------------------------met4.1
    {
        var index = this.FindMetricAggregatorsDefault(tags);     // <----------------------------met4.2

        this.UpdateLongMetricPoint(index, value, tags);
    }

    private void UpdateLongCustomTags(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var index = this.FindMetricAggregatorsCustomTag(tags);

        this.UpdateLongMetricPoint(index, value, tags);
    }

    private void UpdateLongMetricPoint(int metricPointIndex, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (metricPointIndex < 0)
        {
            Interlocked.Increment(ref this.DroppedMeasurements);
            this.InitializeOverflowTagPointIfNotInitialized();
            this.metricPoints[1].Update(value);

            return;
        }

        var exemplarFilterType = this.exemplarFilter;
        if (exemplarFilterType == ExemplarFilterType.AlwaysOff)
        {
            this.metricPoints[metricPointIndex].Update(value);
        }
        else if (exemplarFilterType == ExemplarFilterType.AlwaysOn)
        {
            this.metricPoints[metricPointIndex].UpdateWithExemplar(value, tags, offerExemplar: true);
        }
        else
        {
            this.metricPoints[metricPointIndex].UpdateWithExemplar(value, tags, offerExemplar: Activity.Current?.Recorded ?? false);
        }
    }

    // ...

    private int FindMetricAggregatorsDefault(ReadOnlySpan<KeyValuePair<string, object?>> tags)  // <----------------------------met4.2
    {
        int tagLength = tags.Length;
        if (tagLength == 0)
        {
            this.InitializeZeroTagPointIfNotInitialized();  // <----------------------------met4.3
            return 0;
        }

        var storage = ThreadStaticStorage.GetStorage();

        storage.SplitToKeysAndValues(tags, tagLength, out var tagKeysAndValues);

        return this.lookupAggregatorStore(tagKeysAndValues, tagLength);
    }

    private int FindMetricAggregatorsCustomTag(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        int tagLength = tags.Length;
        if (tagLength == 0 || this.tagsKeysInterestingCount == 0)
        {
            this.InitializeZeroTagPointIfNotInitialized();
            return 0;
        }

        var storage = ThreadStaticStorage.GetStorage();

        Debug.Assert(this.TagKeysInteresting != null, "this.tagKeysInteresting was null");

        storage.SplitToKeysAndValues(tags, tagLength, this.TagKeysInteresting!, out var tagKeysAndValues, out var actualLength);

        // actual number of tags depend on how many  of the incoming tags has user opted to select.
        if (actualLength == 0)
        {
            this.InitializeZeroTagPointIfNotInitialized();
            return 0;
        }

        return this.lookupAggregatorStore(tagKeysAndValues!, actualLength);
    }
}
//-----------------------------------Ʌ
```