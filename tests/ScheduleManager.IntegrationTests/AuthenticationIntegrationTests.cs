using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Errors;
using ScheduleManager.Application.Services;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;
using ScheduleManager.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace ScheduleManager.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AuthenticationIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Reusing_rotated_refresh_token_revokes_entire_family()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string refreshToken = "original-refresh-token-with-high-entropy-simulation";
        var hasher = new Sha256TokenHasher();
        var org = new Organization("Replay", "UTC", now);
        var user = new UserAccount(org.Id, "Replay User", $"REPLAY-{Guid.NewGuid():N}@EXAMPLE.TEST", "+5511999999999",
            UserRole.Employee, "hash", false, now);
        var session = new UserSession(user.Id, org.Id, hasher.Hash(refreshToken), Guid.NewGuid(), now, now.AddDays(30), null, null);
        var record = new RefreshTokenRecord(session.Id, user.Id, org.Id, session.TokenFamilyId, hasher.Hash(refreshToken), now, session.ExpiresAt);
        await using var db = fixture.CreateContext(new TestCurrentRequest());
        db.Add(org);
        db.Add(user);
        db.Add(session);
        db.Add(record);
        await db.SaveChangesAsync();
        var auth = CreateService(db, now, hasher);

        _ = await auth.RefreshAsync(refreshToken, CancellationToken.None);
        var replay = await Assert.ThrowsAsync<AppException>(() => auth.RefreshAsync(refreshToken, CancellationToken.None));

        Assert.Equal("REFRESH_TOKEN_REUSE", replay.Code);
        var reloadedSession = await db.UserSessionSet.SingleAsync(x => x.Id == session.Id);
        Assert.NotNull(reloadedSession.RevokedAt);
        Assert.All(db.RefreshTokenSet.Where(x => x.TokenFamilyId == session.TokenFamilyId), token => Assert.NotNull(token.RevokedAt));
    }

    [Fact]
    public async Task Refresh_after_five_minutes_is_denied_and_session_stays_revoked()
    {
        var issuedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string refreshToken = "inactive-refresh-token-with-high-entropy-simulation";
        var hasher = new Sha256TokenHasher();
        var org = new Organization("Inactive", "UTC", issuedAt);
        var user = new UserAccount(org.Id, "Inactive User", $"INACTIVE-{Guid.NewGuid():N}@EXAMPLE.TEST", "+5511999999999",
            UserRole.Employee, "hash", false, issuedAt);
        var session = new UserSession(user.Id, org.Id, hasher.Hash(refreshToken), Guid.NewGuid(), issuedAt, issuedAt.AddDays(30), null, null);
        var record = new RefreshTokenRecord(session.Id, user.Id, org.Id, session.TokenFamilyId, hasher.Hash(refreshToken), issuedAt, session.ExpiresAt);
        await using var db = fixture.CreateContext(new TestCurrentRequest());
        db.Add(org);
        db.Add(user);
        db.Add(session);
        db.Add(record);
        await db.SaveChangesAsync();
        var auth = CreateService(db, issuedAt.AddMinutes(5).AddTicks(1), hasher);

        var expired = await Assert.ThrowsAsync<AppException>(() => auth.RefreshAsync(refreshToken, CancellationToken.None));

        Assert.Equal("SESSION_EXPIRED", expired.Code);
        Assert.NotNull((await db.UserSessionSet.SingleAsync(x => x.Id == session.Id)).RevokedAt);
        Assert.NotNull((await db.RefreshTokenSet.SingleAsync(x => x.Id == record.Id)).RevokedAt);
    }

    [Fact]
    public async Task Five_parallel_invalid_logins_are_all_counted_and_lock_the_account()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string password = "Correct-password-123!";
        var passwordService = new AspNetPasswordService();
        var org = new Organization("Concurrent failures", "UTC", now);
        var user = new UserAccount(org.Id, "Concurrent User", $"FAIL-{Guid.NewGuid():N}@EXAMPLE.TEST", "+5511999999999",
            UserRole.Employee, passwordService.Hash(password), false, now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.Add(user);
            await setup.SaveChangesAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 5).Select(async index =>
        {
            await start.Task;
            await using var db = fixture.CreateContext(new TestCurrentRequest());
            var auth = new AuthService(db, new AspNetPasswordService(), new TestTokenService(), new Sha256TokenHasher(),
                new FixedClock(now), new TestCurrentRequest(), new TestRealtime());
            var error = await Assert.ThrowsAsync<AppException>(() => auth.LoginAsync(
                new ScheduleManager.Application.Contracts.LoginRequest(user.Email, $"wrong-{index}"), CancellationToken.None));
            Assert.Equal("INVALID_CREDENTIALS", error.Code);
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts);

        await using var verify = fixture.CreateContext(new TestCurrentRequest());
        var locked = await verify.UserSet.SingleAsync(x => x.Id == user.Id);
        Assert.Equal(5, locked.FailedLoginAttempts);
        Assert.True(locked.IsLocked(now.AddSeconds(1)));
    }

    [Fact]
    public async Task Parallel_valid_logins_leave_exactly_one_active_session_without_server_error()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string password = "Correct-password-123!";
        var passwordService = new AspNetPasswordService();
        var org = new Organization("Concurrent success", "UTC", now);
        var user = new UserAccount(org.Id, "Concurrent User", $"SUCCESS-{Guid.NewGuid():N}@EXAMPLE.TEST", "+5511999999999",
            UserRole.Manager, passwordService.Hash(password), false, now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.Add(user);
            await setup.SaveChangesAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logins = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            await using var db = fixture.CreateContext(new TestCurrentRequest());
            var auth = new AuthService(db, new AspNetPasswordService(), new TestTokenService(), new Sha256TokenHasher(),
                new FixedClock(now), new TestCurrentRequest(), new TestRealtime());
            return await auth.LoginAsync(new ScheduleManager.Application.Contracts.LoginRequest(user.Email, password), CancellationToken.None);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(logins);

        Assert.Equal(2, results.Length);
        await using var verify = fixture.CreateContext(new TestCurrentRequest());
        Assert.Equal(1, await verify.UserSessionSet.CountAsync(x => x.UserId == user.Id && x.RevokedAt == null));
        Assert.Equal(2, await verify.UserSessionSet.CountAsync(x => x.UserId == user.Id));
    }

    private static AuthService CreateService(
        ScheduleManager.Infrastructure.Persistence.ScheduleManagerDbContext db,
        DateTimeOffset now,
        ITokenHasher hasher) =>
        new(db, new AspNetPasswordService(), new TestTokenService(), hasher, new FixedClock(now), new TestCurrentRequest(), new TestRealtime());

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public DateOnly Today(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }

    private sealed class TestTokenService : ITokenService
    {
        private int _counter;
        public string CreateAccessToken(UserAccount user, UserSession session, DateTimeOffset now) => "test-access-token";
        public string GenerateOpaqueToken(int sizeInBytes = 32) => $"next-refresh-token-{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}";
    }

    private sealed class TestRealtime : IRealtimeNotifier
    {
        public Task SessionRevokedAsync(Guid userId, Guid sessionId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NotificationCreatedAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
