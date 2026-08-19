using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Domain.Entities;

public sealed class Notification : Entity, ITenantEntity
{
    private Notification() { }

    public Notification(
        Guid id,
        Guid organizationId,
        Guid recipientUserId,
        NotificationType type,
        Guid referenceId,
        string keyId,
        byte[] nonce,
        byte[] ciphertext,
        byte[] authenticationTag,
        string correlationId,
        DateTimeOffset now) : base(id)
    {
        OrganizationId = organizationId;
        RecipientUserId = recipientUserId;
        Type = type;
        ReferenceId = referenceId;
        KeyId = keyId;
        Nonce = nonce;
        Ciphertext = ciphertext;
        AuthenticationTag = authenticationTag;
        CorrelationId = correlationId;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public NotificationType Type { get; private set; }
    public Guid ReferenceId { get; private set; }
    public string KeyId { get; private set; } = string.Empty;
    public byte[] Nonce { get; private set; } = [];
    public byte[] Ciphertext { get; private set; } = [];
    public byte[] AuthenticationTag { get; private set; } = [];
    public string CorrelationId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

public sealed class OutboxMessage : Entity
{
    private OutboxMessage() { }

    public OutboxMessage(
        Guid? organizationId,
        string type,
        string payload,
        DateTimeOffset now,
        string correlationId) : this(DomainIds.New(), organizationId, type, payload, now, correlationId) { }

    public OutboxMessage(
        Guid id,
        Guid? organizationId,
        string type,
        string payload,
        DateTimeOffset now,
        string correlationId) : base(id)
    {
        OrganizationId = organizationId;
        Type = type;
        Payload = payload;
        OccurredAt = now;
        CorrelationId = correlationId;
    }

    public Guid? OrganizationId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        NextAttemptAt = null;
    }

    public void RegisterFailure(DateTimeOffset now, string sanitizedError)
    {
        AttemptCount++;
        LastError = sanitizedError.Length <= 1000 ? sanitizedError : sanitizedError[..1000];
        NextAttemptAt = now.Add(AttemptCount switch
        {
            1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromMinutes(2)
        });
    }
}

public sealed class InboxMessage
{
    private InboxMessage() { }

    public InboxMessage(Guid messageId, string consumerName, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        ConsumerName = consumerName;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }
    public string ConsumerName { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }
}

public sealed class ApplicationError : Entity
{
    private ApplicationError() { }

    public ApplicationError(
        DateTimeOffset timestamp,
        string exceptionType,
        string sanitizedMessage,
        string? sanitizedStackTrace,
        string correlationId,
        string? traceId,
        Guid? messageId,
        string? endpoint,
        string? httpMethod,
        Guid? userId,
        Guid? organizationId,
        string environment) : base(DomainIds.New())
    {
        Timestamp = timestamp;
        ExceptionType = exceptionType;
        SanitizedMessage = sanitizedMessage;
        SanitizedStackTrace = sanitizedStackTrace;
        CorrelationId = correlationId;
        TraceId = traceId;
        MessageId = messageId;
        Endpoint = endpoint;
        HttpMethod = httpMethod;
        UserId = userId;
        OrganizationId = organizationId;
        Environment = environment;
    }

    public DateTimeOffset Timestamp { get; private set; }
    public string ExceptionType { get; private set; } = string.Empty;
    public string SanitizedMessage { get; private set; } = string.Empty;
    public string? SanitizedStackTrace { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? TraceId { get; private set; }
    public Guid? MessageId { get; private set; }
    public string? Endpoint { get; private set; }
    public string? HttpMethod { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string Environment { get; private set; } = string.Empty;
}

public sealed class AuditLog : Entity, ITenantEntity
{
    private AuditLog() { }

    public AuditLog(
        Guid organizationId,
        Guid? userId,
        string action,
        string entityType,
        Guid? entityId,
        string changedFields,
        string correlationId,
        string? ipAddress,
        DateTimeOffset now) : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        UserId = userId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        ChangedFields = changedFields;
        CorrelationId = correlationId;
        IpAddress = ipAddress;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public Guid? EntityId { get; private set; }
    public string ChangedFields { get; private set; } = "{}";
    public string CorrelationId { get; private set; } = string.Empty;
    public string? IpAddress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
