using ScheduleManager.Application.Scheduling;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.UnitTests;

public sealed class ScheduleEngineTests
{
    private readonly ScheduleEngine _engine = new();
    private readonly Guid _organizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public void Same_input_produces_same_result()
    {
        var settings = Settings(min: 2, max: 3);
        var employees = Employees(4);

        var first = _engine.Generate(_organizationId, 2026, 9, settings, employees, [], []);
        var second = _engine.Generate(_organizationId, 2026, 9, settings, employees, [], []);

        Assert.Equal(
            first.Assignments.Select(x => (x.EmployeeId, x.Date, Reasons: string.Join('|', x.Reasons))),
            second.Assignments.Select(x => (x.EmployeeId, x.Date, Reasons: string.Join('|', x.Reasons))));
        Assert.Equal(first.Warnings, second.Warnings);
        Assert.All(first.Assignments.GroupBy(x => x.Date), day => Assert.Equal(2, day.Count()));
    }

    [Fact]
    public void Approved_time_off_is_a_hard_constraint()
    {
        var employees = Employees(2);
        var blocked = employees[0];
        var date = new DateOnly(2026, 9, 15);
        var result = _engine.Generate(_organizationId, 2026, 9, Settings(), employees,
            [new EngineTimeOff(blocked.Id, date, TimeOffStatus.Approved)], []);

        Assert.DoesNotContain(result.Assignments, x => x.EmployeeId == blocked.Id && x.Date == date);
    }

    [Fact]
    public void Pending_time_off_is_avoided_when_an_alternative_exists()
    {
        var employees = Employees(2);
        var date = new DateOnly(2026, 9, 1);
        var result = _engine.Generate(_organizationId, 2026, 9, Settings(), employees,
            [new EngineTimeOff(employees[0].Id, date, TimeOffStatus.Pending)], []);

        Assert.Contains(result.Assignments, x => x.EmployeeId == employees[1].Id && x.Date == date);
        Assert.DoesNotContain(result.Assignments, x => x.EmployeeId == employees[0].Id && x.Date == date);
    }

    [Fact]
    public void Pending_time_off_can_be_used_with_warning_but_hard_shortage_returns_partial_result()
    {
        var employees = Employees(1);
        var date = new DateOnly(2026, 9, 1);
        var pending = _engine.Generate(_organizationId, 2026, 9, Settings(min: 1, max: 1), employees,
            [new EngineTimeOff(employees[0].Id, date, TimeOffStatus.Pending)], []);
        Assert.Contains(pending.Assignments, x => x.Date == date);
        Assert.Contains(pending.Warnings, x => x.Date == date && x.Code == "PENDING_TIME_OFF_CONFLICT");

        var approved = _engine.Generate(_organizationId, 2026, 9, Settings(min: 2, max: 2), employees,
            [new EngineTimeOff(employees[0].Id, date, TimeOffStatus.Approved)], []);
        Assert.DoesNotContain(approved.Assignments, x => x.Date == date);
        Assert.Contains(approved.Warnings, x => x.Date == date && x.Code == "MINIMUM_COVERAGE_UNAVAILABLE");
    }

    [Fact]
    public void Previous_month_streak_is_respected()
    {
        var employees = Employees(2);
        var constrained = employees[0];
        var history = Enumerable.Range(28, 4)
            .Select(day => new EngineHistoricalAssignment(constrained.Id, new DateOnly(2026, 8, day)))
            .ToArray();
        var settings = Settings();
        settings.Configure(1, 1, 4, 0, null, true, 10);

        var result = _engine.Generate(_organizationId, 2026, 9, settings, employees, [], history);

        Assert.DoesNotContain(result.Assignments, x => x.EmployeeId == constrained.Id && x.Date == new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Work_distribution_remains_balanced()
    {
        var result = _engine.Generate(_organizationId, 2026, 9, Settings(), Employees(3), [], []);
        var counts = result.Assignments.GroupBy(x => x.EmployeeId).Select(x => x.Count()).ToArray();
        Assert.True(counts.Max() - counts.Min() <= 1);
    }

    [Fact]
    public void Productivity_weight_is_a_bounded_tie_breaker()
    {
        var lowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var highId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        EngineEmployee[] employees =
        [
            new(lowId, _organizationId, ProductivityLevel.Low, true),
            new(highId, _organizationId, ProductivityLevel.High, true)
        ];
        var noWeight = Settings();
        noWeight.Configure(1, 1, 6, 0, null, true, 0);
        var weighted = Settings();
        weighted.Configure(1, 1, 6, 0, null, true, 10);

        var firstWithoutWeight = _engine.Generate(_organizationId, 2026, 9, noWeight, employees, [], []).Assignments[0];
        var firstWeighted = _engine.Generate(_organizationId, 2026, 9, weighted, employees, [], []).Assignments[0];

        Assert.Equal(lowId, firstWithoutWeight.EmployeeId);
        Assert.Equal(highId, firstWeighted.EmployeeId);
    }

    private OrganizationScheduleSettings Settings(int min = 1, int max = 1)
    {
        var settings = new OrganizationScheduleSettings(_organizationId);
        settings.Configure(min, max, 6, 0, null, true, 10);
        return settings;
    }

    private EngineEmployee[] Employees(int count) => Enumerable.Range(1, count)
        .Select(index => new EngineEmployee(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), _organizationId,
            (ProductivityLevel)(index % 3), true))
        .ToArray();
}
