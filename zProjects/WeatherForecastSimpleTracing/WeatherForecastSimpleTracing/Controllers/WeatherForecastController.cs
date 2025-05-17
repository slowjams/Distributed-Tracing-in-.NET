using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace WeatherForecastSimpleTracing.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController : ControllerBase
    {
        private static readonly string[] Summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        private static readonly ActivitySource _activitySource = new("Tracing.NET");

        private readonly ILogger<WeatherForecastController> _logger;

        public WeatherForecastController(ILogger<WeatherForecastController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public IEnumerable<WeatherForecast> Get()
        {
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }

        [HttpGet("OutgoingHttp")]
        public async Task OutgoingHttpRequest()
        {
            using var activity = _activitySource.StartActivity("AnotherOne");  // <-------------check tpsact see why ActivityListener is not null
            activity.SetTag("myTags.count", 1);
            var userId = Activity.Current.GetBaggageItem("UserId");
            activity.AddTag("UserId", userId);

            var client = new HttpClient();

            var response = await client.GetAsync("https://code-maze.com");
            response.EnsureSuccessStatusCode();
        }
    }
}
