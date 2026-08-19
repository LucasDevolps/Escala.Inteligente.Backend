using System.Text.Json;
using ScheduleManager.Infrastructure;
using ScheduleManager.Infrastructure.Messaging;
using ScheduleManager.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId | ActivityTrackingOptions.Baggage);
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
builder.Services.AddInfrastructure(builder.Configuration, "ScheduleManager.Worker");
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<NotificationConsumerWorker>();
builder.Services.AddHostedService<RetentionCleanupWorker>();

var host = builder.Build();
if (!builder.Environment.IsDevelopment() && !host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value.UseTls)
    throw new InvalidOperationException("RabbitMq:UseTls deve ser true fora de Development.");
await host.RunAsync();
