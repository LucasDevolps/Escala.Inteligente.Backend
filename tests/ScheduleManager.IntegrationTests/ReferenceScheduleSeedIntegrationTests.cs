using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;
using ScheduleManager.Infrastructure.Bootstrap;

namespace ScheduleManager.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class ReferenceScheduleSeedIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task July_seed_reuses_Miriam_and_Eli_and_is_idempotent()
    {
        var now = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var organization = new Organization($"Reference seed {Guid.NewGuid():N}", "UTC", now);
        var manager = User(organization.Id, "Gestor", UserRole.Manager, now);
        var miriamUser = User(organization.Id, "Miriam", UserRole.Employee, now);
        var eliUser = User(organization.Id, "Eli", UserRole.Employee, now);
        var anotherUser = User(organization.Id, "Outro", UserRole.Employee, now);
        var miriam = new Employee(organization.Id, miriamUser.Id, "MIRIAM-001", ProductivityLevel.Moderate, now);
        var eli = new Employee(organization.Id, eliUser.Id, "ELI-001", ProductivityLevel.Moderate, now);
        var another = new Employee(organization.Id, anotherUser.Id, "OUTRO-001", ProductivityLevel.Moderate, now);

        await using (var setup = fixture.CreateContext(new TestCurrentRequest()))
        {
            setup.Add(organization);
            setup.AddRange(manager, miriamUser, eliUser, anotherUser);
            setup.AddRange(miriam, eli, another);
            await setup.SaveChangesAsync();
        }

        await RunSeedAsync(now);
        await RunSeedAsync(now.AddMinutes(1));

        await using var verify = fixture.CreateContext(new TestCurrentRequest());
        var schedules = await verify.ScheduleSet.IgnoreQueryFilters()
            .Where(x => x.OrganizationId == organization.Id && x.Year == 2026 && x.Month == 7)
            .ToListAsync();
        var seeded = Assert.Single(schedules);
        Assert.Equal(ScheduleStatus.Published, seeded.Status);

        var assignments = await verify.ScheduleAssignmentSet
            .Where(x => x.ScheduleId == seeded.Id)
            .ToListAsync();
        Assert.Equal(54, assignments.Count);
        Assert.All(assignments, assignment =>
            Assert.Contains(assignment.EmployeeId, new[] { miriam.Id, eli.Id }));
        Assert.DoesNotContain(assignments, assignment => assignment.EmployeeId == another.Id);
        Assert.Equal(3, await verify.EmployeeSet.IgnoreQueryFilters()
            .CountAsync(x => x.OrganizationId == organization.Id));

        Assert.All(assignments.Where(x => x.WorkDate.DayOfWeek == DayOfWeek.Saturday),
            assignment => Assert.Equal(miriam.Id, assignment.EmployeeId));
        Assert.All(assignments.Where(x => x.WorkDate.DayOfWeek == DayOfWeek.Sunday),
            assignment => Assert.Equal(eli.Id, assignment.EmployeeId));
    }

    private async Task RunSeedAsync(DateTimeOffset now)
    {
        await using var db = fixture.CreateContext(new TestCurrentRequest());
        var initializer = new DatabaseInitializer(
            db,
            new FakePasswordService(),
            new FixedClock(now),
            Options.Create(new DatabaseOptions()),
            Options.Create(new BootstrapOptions()),
            Options.Create(new ReferenceScheduleSeedOptions { Enabled = true, Year = 2026, Month = 7 }),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    private static UserAccount User(Guid organizationId, string name, UserRole role, DateTimeOffset now) =>
        new(
            organizationId,
            name,
            $"{name}-{Guid.NewGuid():N}@EXAMPLE.TEST".ToUpperInvariant(),
            "+5511999999999",
            role,
            "not-a-real-password-hash",
            false,
            now);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public DateOnly Today(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }

    private sealed class FakePasswordService : IPasswordService
    {
        public string Hash(string password) => password;
        public bool Verify(string hash, string password) => hash == password;
        public void PerformDummyVerification(string password) { }
    }
}
