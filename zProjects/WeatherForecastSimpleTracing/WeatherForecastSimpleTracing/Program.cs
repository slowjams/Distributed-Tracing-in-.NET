using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;

namespace WeatherForecastSimpleTracing
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();

            //
            builder.Services
                .AddOpenTelemetry()
                .WithTracing(builder =>  // TracerProviderBuilder
                    builder
                    .AddAspNetCoreInstrumentation(opt =>
                    {
                        opt.EnrichWithHttpRequest = (activity, httpRequest) =>
                        {
                            activity.SetTag("myTags.count", 0);
                            activity.SetTag("myTags.method", httpRequest.Method);
                            activity.SetTag("myTags.url", httpRequest.Path);
                            activity.SetBaggage("UserId", "1234");
                        };
                    })
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter()
                    .AddJaegerExporter()
                    .AddSource("Tracing.NET")  // <----------------- check acts to see how ActivityListener in tpsact can use this setting
                 );
            //

            var app = builder.Build();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
