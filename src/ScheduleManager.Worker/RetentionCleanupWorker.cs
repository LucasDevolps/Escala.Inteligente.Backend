using Microsoft.Extensions.Options;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Infrastructure.Bootstrap;

namespace ScheduleManager.Worker;

public sealed class RetentionCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<RetentionOptions> options,
    ILogger<RetentionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CleanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError("Retention cleanup failed with {ExceptionType}", exception.GetType().FullName);
            }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }

    private async Task CleanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = clock.UtcNow;
        var retention = options.Value;
        var notifications = db.Notifications.Where(x => x.CreatedAt < now.AddDays(-retention.NotificationsDays)).Take(1000).ToList();
        var errors = db.ApplicationErrors.Where(x => x.Timestamp < now.AddDays(-retention.ApplicationErrorsDays)).Take(1000).ToList();
        var sessions = db.UserSessions.Where(x => x.RevokedAt != null && x.RevokedAt < now.AddDays(-retention.RevokedSessionsDays)).Take(1000).ToList();
        var sessionIds = sessions.Select(x => x.Id).ToArray();
        var refreshTokens = db.RefreshTokens.Where(x => sessionIds.Contains(x.SessionId)).ToList();
        await db.ExecuteInTransactionAsync(async ct =>
        {
            db.RemoveRange(notifications);
            db.RemoveRange(errors);
            db.RemoveRange(refreshTokens);
            db.RemoveRange(sessions);
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
        if (notifications.Count + errors.Count + sessions.Count > 0)
            logger.LogInformation("Retention cleanup removed {NotificationCount} notifications, {ErrorCount} errors and {SessionCount} sessions",
                notifications.Count, errors.Count, sessions.Count);
    }
}
