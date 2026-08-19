using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Application.Services;

public sealed class NotificationService(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    INotificationCipher cipher,
    IRealtimeNotifier realtime)
    : ApplicationServiceBase(db, current, clock, cipher, realtime), INotificationService
{
    public Task<PagedResponse<NotificationResponse>> ListAsync(int page, int pageSize, bool unreadOnly, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireUser();
        (page, pageSize) = Validation.Page(page, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var filtered = Db.Notifications.Where(x => x.OrganizationId == organizationId && x.RecipientUserId == userId);
        if (unreadOnly) filtered = filtered.Where(x => x.ReadAt == null);
        var query = filtered.OrderByDescending(x => x.CreatedAt);
        var total = query.LongCount();
        var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList().Select(Decrypt).ToArray();
        return Task.FromResult(new PagedResponse<NotificationResponse>(items, page, pageSize, total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize)));
    }

    public Task<NotificationResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireUser();
        cancellationToken.ThrowIfCancellationRequested();
        var notification = Db.Notifications.SingleOrDefault(x =>
            x.Id == id && x.OrganizationId == organizationId && x.RecipientUserId == userId)
            ?? throw AppException.NotFound("NOTIFICATION_NOT_FOUND", "Notificação não encontrada.");
        return Task.FromResult(Decrypt(notification));
    }

    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireUser();
        var notification = Db.Notifications.SingleOrDefault(x =>
            x.Id == id && x.OrganizationId == organizationId && x.RecipientUserId == userId)
            ?? throw AppException.NotFound("NOTIFICATION_NOT_FOUND", "Notificação não encontrada.");
        notification.MarkRead(Clock.UtcNow);
        await Db.SaveChangesAsync(cancellationToken);
    }

    private NotificationResponse Decrypt(Notification notification)
    {
        var payload = new EncryptedPayload(notification.KeyId, notification.Nonce, notification.Ciphertext, notification.AuthenticationTag);
        var aad = NotificationAssociatedData(notification.Id, notification.OrganizationId, notification.RecipientUserId, notification.Type);
        var content = Cipher.Decrypt(payload, aad);
        return new NotificationResponse(
            notification.Id,
            Status(notification.Type),
            notification.ReferenceId,
            content,
            notification.CreatedAt,
            notification.ReadAt);
    }
}
