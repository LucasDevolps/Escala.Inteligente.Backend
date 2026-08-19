using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Infrastructure.Messaging;

namespace ScheduleManager.Worker;

public sealed class NotificationConsumerWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<RabbitMqOptions> options,
    ILogger<NotificationConsumerWorker> logger) : BackgroundService
{
    private const string ConsumerName = "notification-dispatch-v1";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private static readonly ActivitySource ActivitySource = new("ScheduleManager.Worker.Notifications");
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.UserName) || string.IsNullOrWhiteSpace(settings.Password))
            throw new InvalidOperationException("RabbitMq:UserName e RabbitMq:Password devem ser fornecidos por secret/env.");
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            Ssl = new SslOption { Enabled = settings.UseTls, ServerName = settings.HostName }
        };
        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        var queue = settings.NotificationQueue;
        var deadQueue = $"{queue}.dead";
        await _channel.QueueDeclareAsync(deadQueue, true, false, false, null, false, false, stoppingToken);
        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = deadQueue
        };
        await _channel.QueueDeclareAsync(queue, true, false, false, arguments!, false, false, stoppingToken);
        await _channel.BasicQosAsync(0, 1, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += HandleDeliveryAsync;
        await _channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);

        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task HandleDeliveryAsync(object sender, BasicDeliverEventArgs args)
    {
        if (_channel is null) return;
        var cancellationToken = args.CancellationToken;
        var messageId = Guid.TryParse(args.BasicProperties.MessageId, out var parsed) ? parsed : Guid.Empty;
        var correlationId = args.BasicProperties.CorrelationId ?? Guid.CreateVersion7().ToString("N");
        Exception? finalError = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelays[attempt - 1], cancellationToken);
            try
            {
                await ProcessOnceAsync(messageId, args.Body, correlationId, cancellationToken);
                await _channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                finalError = exception;
                logger.LogWarning("Notification message {MessageId} attempt {Attempt} failed with {ExceptionType}",
                    messageId, attempt + 1, exception.GetType().FullName);
            }
        }

        await PersistFinalFailureAsync(messageId, correlationId, finalError!, cancellationToken);
        // The source queue owns the DLX routing. NACK without requeue lets RabbitMQ
        // atomically dead-letter the original delivery and avoids publish-then-ACK loss.
        await _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
    }

    private async Task ProcessOnceAsync(Guid messageId, ReadOnlyMemory<byte> body, string correlationId, CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty) throw new InvalidDataException("MessageId ausente ou inválido.");
        using var activity = ActivitySource.StartActivity("notification.dispatch", ActivityKind.Consumer);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("correlation.id", correlationId);
        var envelope = JsonSerializer.Deserialize<NotificationEnvelope>(body.Span,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Envelope inválido.");
        if (envelope.MessageId != messageId || envelope.NotificationId == Guid.Empty || envelope.OrganizationId == Guid.Empty || envelope.RecipientUserId == Guid.Empty)
            throw new InvalidDataException("Envelope inconsistente.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        if (db.InboxMessages.Any(x => x.MessageId == messageId && x.ConsumerName == ConsumerName)) return;
        var exists = db.Notifications.Any(x => x.Id == envelope.NotificationId && x.OrganizationId == envelope.OrganizationId &&
                                               x.RecipientUserId == envelope.RecipientUserId);
        if (!exists) throw new InvalidOperationException("Notificação persistida não localizada.");
        await db.ExecuteInTransactionAsync(async ct =>
        {
            if (!db.InboxMessages.Any(x => x.MessageId == messageId && x.ConsumerName == ConsumerName))
            {
                db.Add(new InboxMessage(messageId, ConsumerName, clock.UtcNow));
                await db.SaveChangesAsync(ct);
            }
        }, cancellationToken);
    }

    private async Task PersistFinalFailureAsync(Guid messageId, string correlationId, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            db.Add(new ApplicationError(clock.UtcNow, exception.GetType().FullName ?? exception.GetType().Name,
                "Notification consumer failed after all retries", null, correlationId,
                Activity.Current?.TraceId.ToString(), messageId == Guid.Empty ? null : messageId,
                null, null, null, null, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError("Consumer ApplicationError persistence failed with {ExceptionType}; recursion suppressed",
                persistenceException.GetType().FullName);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    private sealed record NotificationEnvelope(
        Guid MessageId,
        Guid NotificationId,
        Guid RecipientUserId,
        Guid OrganizationId,
        string CorrelationId);
}
