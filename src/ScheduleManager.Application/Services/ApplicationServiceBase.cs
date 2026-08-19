using System.Text;
using System.Text.Json;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Services;

public abstract class ApplicationServiceBase(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    INotificationCipher cipher,
    IRealtimeNotifier realtime)
{
    protected IApplicationDbContext Db { get; } = db;
    protected ICurrentRequest Current { get; } = current;
    protected IClock Clock { get; } = clock;
    protected INotificationCipher Cipher { get; } = cipher;
    protected IRealtimeNotifier Realtime { get; } = realtime;

    protected (Guid UserId, Guid OrganizationId) RequireUser()
    {
        if (Current.UserId is not Guid userId || Current.OrganizationId is not Guid organizationId)
            throw AppException.Unauthorized("SESSION_REVOKED", "A sessão não é válida.");
        return (userId, organizationId);
    }

    protected (Guid UserId, Guid OrganizationId) RequireManager()
    {
        var identity = RequireUser();
        if (!string.Equals(Current.Role, "MANAGER", StringComparison.OrdinalIgnoreCase)) throw AppException.Forbidden();
        return identity;
    }

    protected (Guid UserId, Guid OrganizationId) RequireEmployee()
    {
        var identity = RequireUser();
        if (!string.Equals(Current.Role, "EMPLOYEE", StringComparison.OrdinalIgnoreCase)) throw AppException.Forbidden();
        return identity;
    }

    protected void AddAudit(string action, string entityType, Guid? entityId, string changedFields = "{}")
    {
        var (userId, organizationId) = RequireUser();
        Db.Add(new AuditLog(
            organizationId,
            userId,
            action,
            entityType,
            entityId,
            changedFields,
            Current.CorrelationId,
            Current.IpAddress,
            Clock.UtcNow));
    }

    protected Notification AddNotification(
        Guid organizationId,
        Guid recipientUserId,
        NotificationType type,
        Guid referenceId,
        string content)
    {
        var notificationId = DomainIds.New();
        var aad = NotificationAssociatedData(notificationId, organizationId, recipientUserId, type);
        var encrypted = Cipher.Encrypt(content, aad);
        var notification = new Notification(
            notificationId,
            organizationId,
            recipientUserId,
            type,
            referenceId,
            encrypted.KeyId,
            encrypted.Nonce,
            encrypted.Ciphertext,
            encrypted.AuthenticationTag,
            Current.CorrelationId,
            Clock.UtcNow);
        Db.Add(notification);
        var messageId = DomainIds.New();
        Db.Add(new OutboxMessage(
            messageId,
            organizationId,
            "notification.created",
            JsonSerializer.Serialize(new
            {
                messageId,
                notificationId = notification.Id,
                recipientUserId,
                organizationId,
                correlationId = Current.CorrelationId
            }),
            Clock.UtcNow,
            Current.CorrelationId));
        return notification;
    }

    protected static byte[] NotificationAssociatedData(
        Guid notificationId,
        Guid organizationId,
        Guid recipientUserId,
        NotificationType type) =>
        Encoding.UTF8.GetBytes($"{notificationId:N}|{organizationId:N}|{recipientUserId:N}|{(int)type}");

    protected static string Status(Enum value)
    {
        var source = value.ToString();
        var builder = new StringBuilder(source.Length + 4);
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (index > 0 && char.IsUpper(character)) builder.Append('_');
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    protected static async Task NotifyRealtimeAsync(
        IRealtimeNotifier realtime,
        IEnumerable<Notification> notifications,
        CancellationToken cancellationToken)
    {
        foreach (var notification in notifications)
            await realtime.NotificationCreatedAsync(notification.RecipientUserId, notification.Id, cancellationToken);
    }
}
