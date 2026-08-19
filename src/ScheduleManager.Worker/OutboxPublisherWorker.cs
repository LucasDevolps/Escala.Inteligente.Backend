using System.Diagnostics;
using Microsoft.Extensions.Options;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Infrastructure.Messaging;

namespace ScheduleManager.Worker;

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    IClock clock,
    IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("ScheduleManager.Worker.Outbox");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError("Outbox batch failed with {ExceptionType}", exception.GetType().FullName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = clock.UtcNow;
        var messages = db.OutboxMessages
            .Where(x => x.ProcessedAt == null && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.OccurredAt).Take(50).ToList();
        foreach (var message in messages)
        {
            using var activity = ActivitySource.StartActivity("outbox.publish", ActivityKind.Producer);
            activity?.SetTag("messaging.message.id", message.Id);
            activity?.SetTag("messaging.destination", QueueFor(message.Type));
            activity?.SetTag("correlation.id", message.CorrelationId);
            try
            {
                await publisher.PublishAsync(QueueFor(message.Type), message.Id, message.Type, message.Payload,
                    message.CorrelationId, cancellationToken);
                message.MarkProcessed(clock.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                message.RegisterFailure(clock.UtcNow, exception.GetType().Name);
                if (message.AttemptCount == 4)
                {
                    db.Add(new ApplicationError(clock.UtcNow, exception.GetType().FullName ?? exception.GetType().Name,
                        "Outbox publication failed after retry threshold", null, message.CorrelationId,
                        Activity.Current?.TraceId.ToString(), message.Id, null, null, null, message.OrganizationId,
                        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"));
                }
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Outbox message {MessageId} publication attempt {AttemptCount} failed with {ExceptionType}",
                    message.Id, message.AttemptCount, exception.GetType().FullName);
            }
        }
        return messages.Count;
    }

    private string QueueFor(string type) => string.Equals(type, "notification.created", StringComparison.Ordinal)
        ? rabbitOptions.Value.NotificationQueue
        : "schedule.events";
}
