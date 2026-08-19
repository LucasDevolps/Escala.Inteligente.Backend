using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ScheduleManager.Application.Abstractions;

namespace ScheduleManager.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string HostName { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string VirtualHost { get; init; } = "/";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public bool UseTls { get; init; }
    public string NotificationQueue { get; init; } = "notifications.dispatch";
}

public sealed class RabbitMqPublisher(IOptions<RabbitMqOptions> options) : IMessagePublisher
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(
        string queue,
        Guid messageId,
        string type,
        string payload,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken);
        var deadQueue = $"{queue}.dead";
        await channel.QueueDeclareAsync(deadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, passive: false, noWait: false, cancellationToken: cancellationToken);
        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = deadQueue
        };
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments!, passive: false, noWait: false, cancellationToken: cancellationToken);
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = messageId.ToString(),
            Type = type,
            CorrelationId = correlationId
        };
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties,
            Encoding.UTF8.GetBytes(payload), cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;
            if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
                throw new InvalidOperationException("RabbitMq:UserName e RabbitMq:Password devem ser fornecidos por secret/env.");
            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                Ssl = new SslOption { Enabled = _options.UseTls, ServerName = _options.HostName }
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true), cancellationToken);
            return _channel;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
