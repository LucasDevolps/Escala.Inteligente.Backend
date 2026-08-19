using Microsoft.EntityFrameworkCore;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Application.Scheduling;
using ScheduleManager.Application.Services;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class SchedulingWorkflowIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Accept_swap_when_target_is_now_assigned_returns_conflict_without_partial_changes()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var date = new DateOnly(2026, 10, 12);
        var org = new Organization("Swap race", "UTC", now);
        var manager = User(org.Id, UserRole.Manager, now);
        var requesterUser = User(org.Id, UserRole.Employee, now);
        var targetUser = User(org.Id, UserRole.Employee, now);
        var requester = new Employee(org.Id, requesterUser.Id, "REQ-1", ProductivityLevel.Moderate, now);
        var target = new Employee(org.Id, targetUser.Id, "TGT-1", ProductivityLevel.High, now);
        var schedule = new Schedule(org.Id, date.Year, date.Month, manager.Id, now);
        schedule.Publish(manager.Id, now);
        var swap = new ShiftSwapRequest(org.Id, schedule.Id, requester.Id, target.Id, date, now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.AddRange(manager, requesterUser, targetUser);
            setup.AddRange(requester, target);
            setup.Add(schedule);
            setup.Add(new ScheduleAssignment(schedule.Id, requester.Id, date, AssignmentSource.Suggested, manager.Id, now));
            setup.Add(new ScheduleAssignment(schedule.Id, target.Id, date, AssignmentSource.Manual, manager.Id, now));
            setup.Add(swap);
            await setup.SaveChangesAsync();
        }

        var current = new TestCurrentRequest(org.Id, targetUser.Id, "EMPLOYEE");
        await using var db = fixture.CreateContext(current);
        var service = new ShiftSwapService(db, current, new FixedClock(now), new TestCipher(), new TestRealtime());

        var error = await Assert.ThrowsAsync<AppException>(() => service.AcceptAsync(swap.Id, CancellationToken.None));

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal("SHIFT_SWAP_TARGET_UNAVAILABLE", error.Code);
        await using var verify = fixture.CreateContext(new TestCurrentRequest(org.Id));
        Assert.Equal(2, await verify.ScheduleAssignmentSet.CountAsync(x => x.ScheduleId == schedule.Id && x.WorkDate == date));
        Assert.True(await verify.ScheduleAssignmentSet.AnyAsync(x => x.ScheduleId == schedule.Id && x.EmployeeId == requester.Id && x.WorkDate == date));
        Assert.Equal(ShiftSwapStatus.Pending, (await verify.ShiftSwapSet.SingleAsync(x => x.Id == swap.Id)).Status);
    }

    [Fact]
    public async Task Approving_time_off_removes_assignment_from_editable_schedule()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var date = new DateOnly(2026, 10, 14);
        var org = new Organization("Editable time off", "UTC", now);
        var manager = User(org.Id, UserRole.Manager, now);
        var employeeUser = User(org.Id, UserRole.Employee, now);
        var employee = new Employee(org.Id, employeeUser.Id, "EMP-1", ProductivityLevel.Moderate, now);
        var schedule = new Schedule(org.Id, date.Year, date.Month, manager.Id, now);
        schedule.MarkSuggested();
        var timeOff = new TimeOffRequest(org.Id, employee.Id, date, TimeOffReasonCategory.Personal, null, now);
        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(org);
            setup.AddRange(manager, employeeUser);
            setup.Add(employee);
            setup.Add(new OrganizationScheduleSettings(org.Id));
            setup.Add(schedule);
            setup.Add(new ScheduleAssignment(schedule.Id, employee.Id, date, AssignmentSource.Suggested, manager.Id, now));
            setup.Add(timeOff);
            await setup.SaveChangesAsync();
        }

        var current = new TestCurrentRequest(org.Id, manager.Id, "MANAGER");
        await using var db = fixture.CreateContext(current);
        var service = new TimeOffService(db, current, new FixedClock(now.AddMinutes(1)), new TestCipher(), new TestRealtime());

        var response = await service.ApproveAsync(timeOff.Id, new ApproveTimeOffRequest(false), CancellationToken.None);

        Assert.Equal("APPROVED", response.Status);
        await using var verify = fixture.CreateContext(new TestCurrentRequest(org.Id));
        Assert.False(await verify.ScheduleAssignmentSet.AnyAsync(x => x.ScheduleId == schedule.Id && x.EmployeeId == employee.Id && x.WorkDate == date));
        Assert.Equal(ScheduleStatus.InReview, (await verify.ScheduleSet.SingleAsync(x => x.Id == schedule.Id)).Status);
    }

    [Fact]
    public async Task Publish_with_approved_time_off_is_rejected_and_schedule_stays_editable()
    {
        var seeded = await SeedPublishConflictAsync(approvedTimeOff: true, inactiveEmployee: false);
        var current = new TestCurrentRequest(seeded.OrganizationId, seeded.ManagerId, "MANAGER");
        await using var db = fixture.CreateContext(current);
        var schedule = await db.ScheduleSet.SingleAsync(x => x.Id == seeded.ScheduleId);
        var service = new ScheduleService(db, current, new FixedClock(seeded.Now), new TestCipher(), new TestRealtime(), new ScheduleEngine());

        var error = await Assert.ThrowsAsync<AppException>(() => service.PublishAsync(
            schedule.Id,
            new PublishScheduleRequest(Convert.ToBase64String(schedule.RowVersion)),
            CancellationToken.None));

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal("SCHEDULE_HAS_APPROVED_TIME_OFF", error.Code);
        await using var verify = fixture.CreateContext(new TestCurrentRequest(seeded.OrganizationId));
        Assert.Equal(ScheduleStatus.Suggested, (await verify.ScheduleSet.SingleAsync(x => x.Id == schedule.Id)).Status);
    }

    [Fact]
    public async Task Publish_with_inactive_employee_is_rejected()
    {
        var seeded = await SeedPublishConflictAsync(approvedTimeOff: false, inactiveEmployee: true);
        var current = new TestCurrentRequest(seeded.OrganizationId, seeded.ManagerId, "MANAGER");
        await using var db = fixture.CreateContext(current);
        var schedule = await db.ScheduleSet.SingleAsync(x => x.Id == seeded.ScheduleId);
        var service = new ScheduleService(db, current, new FixedClock(seeded.Now), new TestCipher(), new TestRealtime(), new ScheduleEngine());

        var error = await Assert.ThrowsAsync<AppException>(() => service.PublishAsync(
            schedule.Id,
            new PublishScheduleRequest(Convert.ToBase64String(schedule.RowVersion)),
            CancellationToken.None));

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal("SCHEDULE_HAS_INACTIVE_EMPLOYEE", error.Code);
    }

    private async Task<PublishSeed> SeedPublishConflictAsync(bool approvedTimeOff, bool inactiveEmployee)
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z").AddTicks(Random.Shared.Next(1, 10_000));
        var date = new DateOnly(2026, 11, Random.Shared.Next(1, 25));
        var org = new Organization($"Publish constraint {Guid.NewGuid():N}", "UTC", now);
        var manager = User(org.Id, UserRole.Manager, now);
        var employeeUser = User(org.Id, UserRole.Employee, now);
        var employee = new Employee(org.Id, employeeUser.Id, $"E-{Guid.NewGuid():N}", ProductivityLevel.High, now);
        if (inactiveEmployee) employee.Deactivate(now.AddMinutes(1));
        var schedule = new Schedule(org.Id, date.Year, date.Month, manager.Id, now);
        schedule.MarkSuggested();
        await using var setup = fixture.CreateContext(new TestCurrentRequest());
        setup.Add(org);
        setup.AddRange(manager, employeeUser);
        setup.Add(employee);
        setup.Add(schedule);
        setup.Add(new ScheduleAssignment(schedule.Id, employee.Id, date, AssignmentSource.Suggested, manager.Id, now));
        if (approvedTimeOff)
        {
            var timeOff = new TimeOffRequest(org.Id, employee.Id, date, TimeOffReasonCategory.Personal, null, now);
            timeOff.Approve(manager.Id, now.AddMinutes(1));
            setup.Add(timeOff);
        }
        await setup.SaveChangesAsync();
        return new PublishSeed(org.Id, manager.Id, schedule.Id, now.AddMinutes(2));
    }

    private static UserAccount User(Guid organizationId, UserRole role, DateTimeOffset now) =>
        new(organizationId, "Integration User", $"{Guid.NewGuid():N}@EXAMPLE.TEST", "+5511999999999", role, "hash", false, now);

    private sealed record PublishSeed(Guid OrganizationId, Guid ManagerId, Guid ScheduleId, DateTimeOffset Now);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public DateOnly Today(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }

    private sealed class TestCipher : INotificationCipher
    {
        public EncryptedPayload Encrypt(string plaintext, ReadOnlySpan<byte> associatedData) =>
            new("test", new byte[12], System.Text.Encoding.UTF8.GetBytes(plaintext), new byte[16]);

        public string Decrypt(EncryptedPayload payload, ReadOnlySpan<byte> associatedData) =>
            System.Text.Encoding.UTF8.GetString(payload.Ciphertext);
    }

    private sealed class TestRealtime : IRealtimeNotifier
    {
        public Task SessionRevokedAsync(Guid userId, Guid sessionId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NotificationCreatedAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
