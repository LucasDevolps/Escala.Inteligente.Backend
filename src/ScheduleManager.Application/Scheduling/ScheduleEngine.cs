using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Scheduling;

public sealed record EngineEmployee(Guid Id, Guid OrganizationId, ProductivityLevel Productivity, bool IsActive);
public sealed record EngineTimeOff(Guid EmployeeId, DateOnly Date, TimeOffStatus Status);
public sealed record EngineHistoricalAssignment(Guid EmployeeId, DateOnly Date);
public sealed record SuggestedAssignment(Guid EmployeeId, DateOnly Date, IReadOnlyList<string> Reasons);
public sealed record GenerationWarning(DateOnly Date, string Code, string Message);
public sealed record ScheduleGenerationResult(IReadOnlyList<SuggestedAssignment> Assignments, IReadOnlyList<GenerationWarning> Warnings);

public sealed class ScheduleEngine
{
    public ScheduleGenerationResult Generate(
        Guid organizationId,
        int year,
        int month,
        OrganizationScheduleSettings settings,
        IReadOnlyCollection<EngineEmployee> employees,
        IReadOnlyCollection<EngineTimeOff> timeOff,
        IReadOnlyCollection<EngineHistoricalAssignment> previousMonthAssignments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var activeEmployees = employees
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .OrderBy(x => x.Id)
            .ToArray();
        var approved = timeOff.Where(x => x.Status == TimeOffStatus.Approved).Select(x => (x.EmployeeId, x.Date)).ToHashSet();
        var pending = timeOff.Where(x => x.Status == TimeOffStatus.Pending).Select(x => (x.EmployeeId, x.Date)).ToHashSet();
        var history = previousMonthAssignments.Select(x => (x.EmployeeId, x.Date)).ToHashSet();
        var selected = new HashSet<(Guid EmployeeId, DateOnly Date)>();
        var assignments = new List<SuggestedAssignment>();
        var warnings = new List<GenerationWarning>();
        var workCount = activeEmployees.ToDictionary(x => x.Id, _ => 0);
        var weekendCount = activeEmployees.ToDictionary(
            x => x.Id,
            x => previousMonthAssignments.Count(a => a.EmployeeId == x.Id && IsWeekend(a.Date)));
        var previousCount = activeEmployees.ToDictionary(x => x.Id, x => previousMonthAssignments.Count(a => a.EmployeeId == x.Id));
        var daysAllowedByTimeOff = Math.Max(0, daysInMonth - settings.MinDaysOffPerMonth);
        var effectiveMaxDays = settings.MaxWorkDaysPerMonth.HasValue
            ? Math.Min(settings.MaxWorkDaysPerMonth.Value, daysAllowedByTimeOff)
            : daysAllowedByTimeOff;

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(year, month, day);
            var hardEligible = activeEmployees
                .Where(employee => !approved.Contains((employee.Id, date)))
                .Where(employee => workCount[employee.Id] < effectiveMaxDays)
                .Where(employee => ConsecutiveBefore(employee.Id, date, selected, history) < settings.MaxConsecutiveWorkDays)
                .ToList();

            var preferred = hardEligible.Where(x => !pending.Contains((x.Id, date))).ToList();
            var candidates = Rank(preferred, date, workCount, weekendCount, previousCount, selected, history, settings).ToList();
            var pendingWasNeeded = false;
            if (candidates.Count < settings.MinEmployeesPerDay)
            {
                pendingWasNeeded = true;
                var fallbacks = hardEligible.Where(x => pending.Contains((x.Id, date)));
                candidates.AddRange(Rank(fallbacks, date, workCount, weekendCount, previousCount, selected, history, settings));
            }

            var daily = candidates.Take(settings.MinEmployeesPerDay).ToArray();
            if (pendingWasNeeded && daily.Any(x => pending.Contains((x.Id, date))))
            {
                warnings.Add(new GenerationWarning(
                    date,
                    "PENDING_TIME_OFF_CONFLICT",
                    "Foi necessário incluir colaborador com solicitação de folga pendente para tentar atender à cobertura mínima."));
            }

            if (daily.Length < settings.MinEmployeesPerDay)
            {
                warnings.Add(new GenerationWarning(
                    date,
                    "MINIMUM_COVERAGE_UNAVAILABLE",
                    $"Cobertura parcial: {daily.Length} de {settings.MinEmployeesPerDay} colaboradores, sem violar restrições obrigatórias."));
            }

            foreach (var employee in daily)
            {
                var belowAverage = workCount[employee.Id] <= (workCount.Count == 0 ? 0 : workCount.Values.Average());
                var reasons = new List<string>
                {
                    "Funcionário disponível.",
                    "Não possui folga aprovada.",
                    "Limite de dias consecutivos respeitado."
                };
                if (belowAverage) reasons.Add("Possui menos dias escalados que a média atual.");
                if (pending.Contains((employee.Id, date))) reasons.Add("Solicitação pendente considerada com alerta de cobertura.");

                selected.Add((employee.Id, date));
                workCount[employee.Id]++;
                if (IsWeekend(date)) weekendCount[employee.Id]++;
                assignments.Add(new SuggestedAssignment(employee.Id, date, reasons));
            }
        }

        return new ScheduleGenerationResult(assignments, warnings);
    }

    private static IOrderedEnumerable<EngineEmployee> Rank(
        IEnumerable<EngineEmployee> candidates,
        DateOnly date,
        IReadOnlyDictionary<Guid, int> workCount,
        IReadOnlyDictionary<Guid, int> weekendCount,
        IReadOnlyDictionary<Guid, int> previousCount,
        HashSet<(Guid EmployeeId, DateOnly Date)> selected,
        HashSet<(Guid EmployeeId, DateOnly Date)> history,
        OrganizationScheduleSettings settings) =>
        candidates
            .OrderBy(x => workCount[x.Id])
            .ThenBy(x => ConsecutiveBefore(x.Id, date, selected, history))
            .ThenBy(x => settings.BalanceWeekends ? weekendCount[x.Id] : 0)
            .ThenBy(x => previousCount[x.Id])
            // Bounded tie-break band: weight changes influence without ever overtaking the
            // four fairness keys above. The maximum band is 8 (High * weight 20 / 5).
            .ThenByDescending(x => ProductivityBand(x.Productivity, settings.ProductivityWeight))
            .ThenByDescending(x => DaysSinceLastWorked(x.Id, date, selected, history))
            .ThenBy(x => x.Id);

    private static int ConsecutiveBefore(
        Guid employeeId,
        DateOnly date,
        HashSet<(Guid EmployeeId, DateOnly Date)> selected,
        HashSet<(Guid EmployeeId, DateOnly Date)> history)
    {
        var count = 0;
        for (var current = date.AddDays(-1); ; current = current.AddDays(-1))
        {
            if (!selected.Contains((employeeId, current)) && !history.Contains((employeeId, current))) break;
            count++;
        }
        return count;
    }

    private static int DaysSinceLastWorked(
        Guid employeeId,
        DateOnly date,
        HashSet<(Guid EmployeeId, DateOnly Date)> selected,
        HashSet<(Guid EmployeeId, DateOnly Date)> history)
    {
        for (var days = 1; days <= 62; days++)
        {
            var candidate = date.AddDays(-days);
            if (selected.Contains((employeeId, candidate)) || history.Contains((employeeId, candidate))) return days;
        }
        return int.MaxValue;
    }

    private static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static int ProductivityBand(ProductivityLevel productivity, int weight) =>
        Math.Clamp((int)productivity * weight / 5, 0, 8);
}
