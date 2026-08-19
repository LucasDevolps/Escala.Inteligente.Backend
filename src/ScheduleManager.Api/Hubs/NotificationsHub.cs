using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ScheduleManager.Application.Abstractions;

namespace ScheduleManager.Api.Hubs;

[Authorize]
public sealed class NotificationsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst("sub")?.Value;
        var sessionId = Context.User?.FindFirst("sid")?.Value;
        if (Guid.TryParse(userId, out var parsedUserId))
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(parsedUserId));
        if (Guid.TryParse(sessionId, out var parsedSessionId))
            await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(parsedSessionId));
        await base.OnConnectedAsync();
    }

    internal static string UserGroup(Guid userId) => $"user:{userId:N}";
    internal static string SessionGroup(Guid sessionId) => $"session:{sessionId:N}";
}

public sealed class SignalRRealtimeNotifier(
    IHubContext<NotificationsHub> hub,
    ILogger<SignalRRealtimeNotifier> logger) : IRealtimeNotifier
{
    public async Task SessionRevokedAsync(Guid userId, Guid sessionId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(NotificationsHub.SessionGroup(sessionId))
                .SendAsync("session.revoked", new { reason }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("SignalR session revocation delivery failed with {ExceptionType}", exception.GetType().FullName);
        }
    }

    public async Task NotificationCreatedAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(NotificationsHub.UserGroup(userId))
                .SendAsync("notification.created", new { notificationId }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("SignalR notification delivery failed with {ExceptionType}", exception.GetType().FullName);
        }
    }
}
