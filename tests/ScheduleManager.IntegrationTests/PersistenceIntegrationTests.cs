using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Services;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;
using ScheduleManager.Infrastructure.Security;

namespace ScheduleManager.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class PersistenceIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Normalized_email_is_globally_unique_and_employee_number_is_tenant_unique()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext(new TestCurrentRequest());
        var orgA = new Organization("A", "UTC", now);
        var orgB = new Organization("B", "UTC", now);
        var userA = User(orgA.Id, "UNIQUE@EXAMPLE.TEST", now);
        var userB = User(orgB.Id, "UNIQUE@EXAMPLE.TEST", now);
        db.AddRange(orgA, orgB);
        db.Add(userA);
        await db.SaveChangesAsync();
        db.Add(userB);

        await Assert.ThrowsAsync<PersistenceConflictException>(() => db.SaveChangesAsync());

        await using var employeeDb = fixture.CreateContext(new TestCurrentRequest());
        var org = new Organization("Employee org", "UTC", now);
        var firstUser = User(org.Id, $"FIRST-{Guid.NewGuid():N}@EXAMPLE.TEST", now);
        var secondUser = User(org.Id, $"SECOND-{Guid.NewGuid():N}@EXAMPLE.TEST", now);
        employeeDb.Add(org);
        employeeDb.AddRange(firstUser, secondUser);
        employeeDb.Add(new Employee(org.Id, firstUser.Id, "E-1", ProductivityLevel.Moderate, now));
        employeeDb.Add(new Employee(org.Id, secondUser.Id, "E-1", ProductivityLevel.High, now));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => employeeDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_query_filter_hides_other_organization()
    {
        var now = DateTimeOffset.UtcNow;
        var orgA = new Organization("Tenant A", "UTC", now);
        var orgB = new Organization("Tenant B", "UTC", now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.AddRange(orgA, orgB);
            setup.Add(User(orgA.Id, $"A-{Guid.NewGuid():N}@EXAMPLE.TEST", now));
            setup.Add(User(orgB.Id, $"B-{Guid.NewGuid():N}@EXAMPLE.TEST", now));
            await setup.SaveChangesAsync();
        }

        await using var tenantA = fixture.CreateContext(new TestCurrentRequest(orgA.Id));
        var visible = await tenantA.UserSet.ToListAsync();
        Assert.NotEmpty(visible);
        Assert.All(visible, user => Assert.Equal(orgA.Id, user.OrganizationId));
        Assert.DoesNotContain(visible, user => user.OrganizationId == orgB.Id);
    }

    [Fact]
    public async Task Schedule_rowversion_rejects_lost_update()
    {
        var now = DateTimeOffset.UtcNow;
        var org = new Organization("Concurrency", "UTC", now);
        var manager = User(org.Id, $"M-{Guid.NewGuid():N}@EXAMPLE.TEST", now, UserRole.Manager);
        var schedule = new Schedule(org.Id, 2027, 1, manager.Id, now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.Add(manager);
            setup.Add(schedule);
            await setup.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext(new TestCurrentRequest(org.Id));
        await using var second = fixture.CreateContext(new TestCurrentRequest(org.Id));
        var firstCopy = await first.ScheduleSet.SingleAsync(x => x.Id == schedule.Id);
        var secondCopy = await second.ScheduleSet.SingleAsync(x => x.Id == schedule.Id);
        firstCopy.MarkSuggested();
        secondCopy.MarkSuggested();
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<OptimisticConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Refresh_token_rotation_is_concurrency_protected()
    {
        var now = DateTimeOffset.UtcNow;
        var org = new Organization("Refresh", "UTC", now);
        var user = User(org.Id, $"R-{Guid.NewGuid():N}@EXAMPLE.TEST", now);
        var session = new UserSession(user.Id, org.Id, RandomBytes(), Guid.NewGuid(), now, now.AddDays(1), null, null);
        var token = new RefreshTokenRecord(session.Id, user.Id, org.Id, session.TokenFamilyId, RandomBytes(), now, now.AddDays(1));
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.Add(user);
            setup.Add(session);
            setup.Add(token);
            await setup.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext(new TestCurrentRequest(org.Id));
        await using var second = fixture.CreateContext(new TestCurrentRequest(org.Id));
        var firstCopy = await first.RefreshTokenSet.SingleAsync(x => x.Id == token.Id);
        var secondCopy = await second.RefreshTokenSet.SingleAsync(x => x.Id == token.Id);
        firstCopy.MarkRotated(now.AddMinutes(1));
        secondCopy.MarkRotated(now.AddMinutes(1));
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<OptimisticConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Business_data_and_outbox_commit_together_and_inbox_is_idempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var org = new Organization("Outbox", "UTC", now);
        var messageId = Guid.CreateVersion7();
        await using (var db = fixture.CreateContext(new TestCurrentRequest()))
        {
            await db.ExecuteInTransactionAsync(async ct =>
            {
                db.Add(org);
                db.Add(new OutboxMessage(messageId, org.Id, "test.event", "{\"messageId\":\"" + messageId + "\"}", now, "integration"));
                await db.SaveChangesAsync(ct);
            });
        }

        await using (var verify = fixture.CreateContext(new TestCurrentRequest()))
        {
            Assert.True(await verify.OrganizationSet.AnyAsync(x => x.Id == org.Id));
            Assert.True(await verify.OutboxMessageSet.AnyAsync(x => x.Id == messageId));
            verify.Add(new InboxMessage(messageId, "consumer", now));
            await verify.SaveChangesAsync();
        }
        await using var duplicate = fixture.CreateContext(new TestCurrentRequest());
        duplicate.Add(new InboxMessage(messageId, "consumer", now));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task Notification_envelope_message_id_matches_outbox_and_amqp_header_source()
    {
        var now = DateTimeOffset.UtcNow;
        var org = new Organization("Notification envelope", "UTC", now);
        var recipient = User(org.Id, $"N-{Guid.NewGuid():N}@EXAMPLE.TEST", now);
        var current = new TestCurrentRequest(org.Id, recipient.Id);
        await using var db = fixture.CreateContext(current);
        db.Add(org);
        db.Add(recipient);
        await db.SaveChangesAsync();
        var composer = new TestNotificationComposer(db, current, new SystemClock(), new TestCipher(), new NullRealtimeNotifier());

        var notification = composer.Compose(org.Id, recipient.Id);
        var outbox = Assert.Single(db.OutboxMessageSet.Local);
        using var payload = JsonDocument.Parse(outbox.Payload);

        Assert.Equal(outbox.Id, payload.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(notification.Id, payload.RootElement.GetProperty("notificationId").GetGuid());
        await db.SaveChangesAsync();
    }

    private static UserAccount User(Guid organizationId, string normalizedEmail, DateTimeOffset now, UserRole role = UserRole.Employee) =>
        new(organizationId, "Test User", normalizedEmail, "+5511999999999", role, "not-a-real-password-hash", false, now);

    private static byte[] RandomBytes() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private sealed class TestNotificationComposer(
        IApplicationDbContext db,
        ICurrentRequest current,
        IClock clock,
        INotificationCipher cipher,
        IRealtimeNotifier realtime) : ApplicationServiceBase(db, current, clock, cipher, realtime)
    {
        public Notification Compose(Guid organizationId, Guid recipientId) => AddNotification(
            organizationId,
            recipientId,
            NotificationType.SchedulePublished,
            Guid.NewGuid(),
            "encrypted by test cipher");
    }

    private sealed class TestCipher : INotificationCipher
    {
        public EncryptedPayload Encrypt(string plaintext, ReadOnlySpan<byte> associatedData) =>
            new("test", new byte[12], System.Text.Encoding.UTF8.GetBytes(plaintext), new byte[16]);

        public string Decrypt(EncryptedPayload payload, ReadOnlySpan<byte> associatedData) =>
            System.Text.Encoding.UTF8.GetString(payload.Ciphertext);
    }
}
