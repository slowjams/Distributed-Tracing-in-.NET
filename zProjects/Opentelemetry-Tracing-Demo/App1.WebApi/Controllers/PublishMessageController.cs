using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace App1.WebApi.Controllers
{
    [ApiController]
    [Route("publish-message")]
    public class PublishMessageController(ILogger<PublishMessageController> logger, IConfiguration configuration): ControllerBase
    {
        private static readonly ActivitySource Activity = new(nameof(PublishMessageController));

        // OpenTelemetry does not yet have support for automatic RabbitMq trace correlation, so you have to do it manually via Propagators
        private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

        [HttpGet]
        public void Get()
        {
            try
            {
                using (var activity = Activity.StartActivity("RabbitMq Publish", ActivityKind.Producer))
                {
                    var factory = new ConnectionFactory { HostName = configuration["RabbitMq:Host"] };
                    using (var connection = factory.CreateConnection())
                    using (var channel = connection.CreateModel())
                    {
                        IBasicProperties props = channel.CreateBasicProperties();

                        AddActivityToHeader(activity, props);

                        channel.QueueDeclare(queue: "sample",
                            durable: false,
                            exclusive: false,
                            autoDelete: false,
                            arguments: null);

                        var body = Encoding.UTF8.GetBytes("I am app1");

                        logger.LogInformation("Publishing message to queue");

                        channel.BasicPublish(exchange: "",
                            routingKey: "sample",
                            basicProperties: props,
                            body: body);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error trying to publish a message");
                throw;
            }
        }

        private void AddActivityToHeader(Activity activity, IBasicProperties props)
        {
            Propagator.Inject(      // <---------------------------------------------this is the key part for injecting context
                new PropagationContext(activity.Context, Baggage.Current),
                props, 
                InjectContextIntoHeader);
            
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.rabbitmq.queue", "sample");
        }
                                                                   
        private void InjectContextIntoHeader(IBasicProperties props, string key, string value)
        {                                                                // key can be "traceparent", "tracestate", "baggage" etc
            try
            {
                props.Headers ??= new Dictionary<string, object>();
                props.Headers[key] = value;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to inject trace context.");
            }
        }
    }
}
