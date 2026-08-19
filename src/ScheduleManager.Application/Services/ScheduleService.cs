using System.Text.Json;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Application.Scheduling;
using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Services;

public sealed class ScheduleService(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    INotificationCipher cipher,
    IRealtimeNotifier realtime,
    ScheduleEngine engine)
    : ApplicationServiceBase(db, current, clock, cipher, realtime), IScheduleService
{
    public async Task<ScheduleResponse> CreateAsync(CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        if (request.Year is < 2000 or > 2200 || request.Month is < 1 or > 12)
            throw AppException.Validation(new Dictionary<string, string[]> { ["period"] = ["Ano ou mês inválido."] });
        if (Db.Schedules.Any(x => x.OrganizationId == organizationId && x.Year == request.Year && x.Month == request.Month))
            throw AppException.Conflict("SCHEDULE_ALREADY_EXISTS", "Já existe uma escala para este mês.");

        var schedule = new Schedule(organizationId, request.Year, request.Month, managerId, Clock.UtcNow);
        Db.Add(schedule);
        AddAudit("ScheduleCreated", "Schedule", schedule.Id);
        try { await Db.SaveChangesAsync(cancellationToken); }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("SCHEDULE_ALREADY_EXISTS", "Já existe uma escala para este mês.");
        }
        return Map(schedule);
    }

    public Task<ScheduleResponse> GetAsync(int year, int month, CancellationToken cancellationToken)
    {
        var (_, organizationId) = RequireUser();
        cancellationToken.ThrowIfCancellationRequested();
        var schedule = Db.Schedules.SingleOrDefault(x => x.OrganizationId == organizationId && x.Year == year && x.Month == month)
            ?? throw AppException.NotFound("SCHEDULE_NOT_FOUND", "Escala não encontrada.");
        if (string.Equals(Current.Role, "EMPLOYEE", StringComparison.OrdinalIgnoreCase) &&
            schedule.Status is not (ScheduleStatus.Published or ScheduleStatus.Closed))
            throw AppException.NotFound("SCHEDULE_NOT_FOUND", "Escala não encontrada.");
        return Task.FromResult(Map(schedule));
    }

    public async Task<ScheduleResponse> GenerateAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        Schedule? generatedSchedule = null;
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.ClearTrackedChanges();
                var schedule = FindSchedule(scheduleId, organizationId);
                EnsureEditable(schedule);
                var settings = Db.ScheduleSettings.SingleOrDefault(x => x.OrganizationId == organizationId);
                if (settings is null)
                {
                    settings = new OrganizationScheduleSettings(organizationId);
                    Db.Add(settings);
                }

                var employees = Db.Employees.Where(x => x.OrganizationId == organizationId && x.IsActive).ToList();
                var start = new DateOnly(schedule.Year, schedule.Month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                var timeOff = Db.TimeOffRequests
                    .Where(x => x.OrganizationId == organizationId && x.Date >= start && x.Date <= end &&
                                (x.Status == TimeOffStatus.Pending || x.Status == TimeOffStatus.Approved))
                    .Select(x => new EngineTimeOff(x.EmployeeId, x.Date, x.Status))
                    .ToList();
                var organizationScheduleIds = Db.Schedules.Where(x => x.OrganizationId == organizationId).Select(x => x.Id).ToHashSet();
                var previousStart = start.AddMonths(-1);
                var history = Db.ScheduleAssignments
                    .Where(x => organizationScheduleIds.Contains(x.ScheduleId) && x.WorkDate >= previousStart && x.WorkDate < start)
                    .Select(x => new EngineHistoricalAssignment(x.EmployeeId, x.WorkDate))
                    .ToList();
                var result = engine.Generate(
                    organizationId,
                    schedule.Year,
                    schedule.Month,
                    settings,
                    employees.Select(x => new EngineEmployee(x.Id, x.OrganizationId, x.ProductivityLevel, x.IsActive)).ToArray(),
                    timeOff,
                    history);

                Db.RemoveRange(Db.ScheduleAssignments.Where(x => x.ScheduleId == schedule.Id).ToList());
                Db.RemoveRange(Db.ScheduleWarnings.Where(x => x.ScheduleId == schedule.Id).ToList());
                Db.AddRange(result.Assignments.Select(x => new ScheduleAssignment(
                    schedule.Id,
                    x.EmployeeId,
                    x.Date,
                    AssignmentSource.Suggested,
                    managerId,
                    Clock.UtcNow,
                    JsonSerializer.Serialize(x.Reasons))));
                Db.AddRange(result.Warnings.Select(x => new ScheduleWarning(schedule.Id, x.Date, x.Code, x.Message)));
                schedule.MarkSuggested();
                AddAudit("ScheduleGenerated", "Schedule", schedule.Id, "{\"fields\":[\"assignments\",\"warnings\",\"status\"]}");
                await Db.SaveChangesAsync(ct);
                generatedSchedule = schedule;
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        return Map(generatedSchedule!);
    }

    public async Task<ScheduleResponse> UpdateDayAsync(
        Guid scheduleId,
        DateOnly date,
        UpdateScheduleDayRequest request,
        CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        if (request.EmployeeIds is null)
            throw AppException.Validation(new Dictionary<string, string[]> { ["employeeIds"] = ["A lista de colaboradores é obrigatória."] });
        if (request.EmployeeIds.Count != request.EmployeeIds.Distinct().Count())
            throw AppException.Validation(new Dictionary<string, string[]> { ["employeeIds"] = ["A lista não pode conter IDs duplicados."] });
        var rowVersion = Validation.RowVersion(request.RowVersion);
        Schedule? updatedSchedule = null;

        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.ClearTrackedChanges();
                var schedule = FindSchedule(scheduleId, organizationId);
                EnsureEditable(schedule);
                if (date.Year != schedule.Year || date.Month != schedule.Month)
                    throw AppException.Validation(new Dictionary<string, string[]> { ["date"] = ["A data deve pertencer ao mês da escala."] });
                var settings = Db.ScheduleSettings.SingleOrDefault(x => x.OrganizationId == organizationId)
                    ?? new OrganizationScheduleSettings(organizationId);
                if (request.EmployeeIds.Count > settings.MaxEmployeesPerDay)
                    throw AppException.Rule("MAXIMUM_COVERAGE_EXCEEDED", "A quantidade excede o máximo de colaboradores por dia.");
                var employees = Db.Employees
                    .Where(x => x.OrganizationId == organizationId && request.EmployeeIds.Contains(x.Id) && x.IsActive)
                    .ToList();
                if (employees.Count != request.EmployeeIds.Count)
                    throw AppException.Rule("EMPLOYEE_UNAVAILABLE", "Um ou mais colaboradores estão inativos ou não pertencem à organização.");
                if (Db.TimeOffRequests.Any(x => x.OrganizationId == organizationId && request.EmployeeIds.Contains(x.EmployeeId) &&
                                                x.Date == date && x.Status == TimeOffStatus.Approved))
                    throw AppException.Rule("EMPLOYEE_UNAVAILABLE", "Colaborador com folga aprovada não pode ser escalado.");
                ValidateManualLimits(schedule, date, request.EmployeeIds, settings, organizationId);
                Db.SetOriginalRowVersion(schedule, rowVersion);
                var warnings = new List<ScheduleWarning>();
                if (request.EmployeeIds.Count < settings.MinEmployeesPerDay)
                    warnings.Add(new ScheduleWarning(schedule.Id, date, "MINIMUM_COVERAGE_UNAVAILABLE",
                        $"Cobertura manual: {request.EmployeeIds.Count} de {settings.MinEmployeesPerDay} colaboradores."));
                if (Db.TimeOffRequests.Any(x => x.OrganizationId == organizationId && request.EmployeeIds.Contains(x.EmployeeId) &&
                                                x.Date == date && x.Status == TimeOffStatus.Pending))
                    warnings.Add(new ScheduleWarning(schedule.Id, date, "PENDING_TIME_OFF_CONFLICT",
                        "A edição inclui colaborador com solicitação de folga pendente."));
                Db.RemoveRange(Db.ScheduleAssignments.Where(x => x.ScheduleId == schedule.Id && x.WorkDate == date).ToList());
                Db.RemoveRange(Db.ScheduleWarnings.Where(x => x.ScheduleId == schedule.Id && x.Date == date).ToList());
                Db.AddRange(request.EmployeeIds.Select(employeeId => new ScheduleAssignment(
                    schedule.Id,
                    employeeId,
                    date,
                    AssignmentSource.Manual,
                    managerId,
                    Clock.UtcNow,
                    "[\"Atribuição definida manualmente pelo gestor.\",\"Restrições obrigatórias validadas.\"]")));
                Db.AddRange(warnings);
                schedule.MarkEdited();
                AddAudit("ScheduleEdited", "Schedule", schedule.Id, "{\"fields\":[\"assignments\",\"warnings\",\"status\"]}");
                await Db.SaveChangesAsync(ct);
                updatedSchedule = schedule;
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        return Map(updatedSchedule!);
    }

    public async Task<ScheduleResponse> PublishAsync(Guid scheduleId, PublishScheduleRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        var rowVersion = Validation.RowVersion(request.RowVersion);
        var notifications = new List<Notification>();
        Schedule? publishedSchedule = null;
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.ClearTrackedChanges();
                var schedule = FindSchedule(scheduleId, organizationId);
                EnsureEditable(schedule);
                Db.SetOriginalRowVersion(schedule, rowVersion);
                var periodStart = new DateOnly(schedule.Year, schedule.Month, 1);
                var periodEnd = periodStart.AddMonths(1);
                var hasApprovedTimeOffConflict = Db.TimeOffRequests.Any(timeOff =>
                    timeOff.OrganizationId == organizationId &&
                    timeOff.Status == TimeOffStatus.Approved &&
                    timeOff.Date >= periodStart &&
                    timeOff.Date < periodEnd &&
                    Db.ScheduleAssignments.Any(assignment =>
                        assignment.ScheduleId == schedule.Id &&
                        assignment.EmployeeId == timeOff.EmployeeId &&
                        assignment.WorkDate == timeOff.Date));
                if (hasApprovedTimeOffConflict)
                    throw AppException.Conflict("SCHEDULE_HAS_APPROVED_TIME_OFF",
                        "A escala contém colaborador com folga aprovada; remova a atribuição antes de publicar.");
                var hasInactiveEmployee = Db.ScheduleAssignments.Any(assignment =>
                    assignment.ScheduleId == schedule.Id &&
                    !Db.Employees.Any(employee => employee.Id == assignment.EmployeeId &&
                        employee.OrganizationId == organizationId && employee.IsActive));
                if (hasInactiveEmployee)
                    throw AppException.Conflict("SCHEDULE_HAS_INACTIVE_EMPLOYEE",
                        "A escala contém colaborador inativo; remova a atribuição antes de publicar.");
                schedule.Publish(managerId, Clock.UtcNow);
                var messageId = DomainIds.New();
                var eventPayload = JsonSerializer.Serialize(new
                {
                    messageId,
                    scheduleId = schedule.Id,
                    organizationId,
                    revision = schedule.Revision,
                    correlationId = Current.CorrelationId
                });
                Db.Add(new OutboxMessage(messageId, organizationId, "schedule.published", eventPayload, Clock.UtcNow, Current.CorrelationId));
                var recipientIds = Db.Employees
                    .Where(x => x.OrganizationId == organizationId && x.IsActive)
                    .Join(Db.Users, employee => employee.UserId, user => user.Id, (_, user) => user)
                    .Where(x => x.IsActive)
                    .Select(x => x.Id)
                    .ToList();
                foreach (var recipientId in recipientIds)
                    notifications.Add(AddNotification(organizationId, recipientId, NotificationType.SchedulePublished, schedule.Id,
                        $"A escala de {schedule.Month:D2}/{schedule.Year} foi publicada."));
                AddAudit("SchedulePublished", "Schedule", schedule.Id, "{\"fields\":[\"status\",\"publishedBy\",\"publishedAt\",\"revision\"]}");
                await Db.SaveChangesAsync(ct);
                publishedSchedule = schedule;
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A escala foi alterada por outra operação.");
        }
        await NotifyRealtimeAsync(Realtime, notifications, cancellationToken);
        return Map(publishedSchedule!);
    }

    private Schedule FindSchedule(Guid id, Guid organizationId) =>
        Db.Schedules.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
        ?? throw AppException.NotFound("SCHEDULE_NOT_FOUND", "Escala não encontrada.");

    private static void EnsureEditable(Schedule schedule)
    {
        if (schedule.Status is ScheduleStatus.Published or ScheduleStatus.Closed)
            throw AppException.Conflict("SCHEDULE_ALREADY_PUBLISHED", "A escala publicada ou encerrada não pode ser alterada.");
    }

    private void ValidateManualLimits(
        Schedule schedule,
        DateOnly date,
        IReadOnlyCollection<Guid> employeeIds,
        OrganizationScheduleSettings settings,
        Guid organizationId)
    {
        var organizationScheduleIds = Db.Schedules.Where(x => x.OrganizationId == organizationId).Select(x => x.Id).ToHashSet();
        var periodStart = new DateOnly(schedule.Year, schedule.Month, 1);
        var assignments = Db.ScheduleAssignments
            .Where(x => organizationScheduleIds.Contains(x.ScheduleId) && x.WorkDate >= periodStart.AddMonths(-1) &&
                        x.WorkDate < periodStart.AddMonths(1) && x.WorkDate != date)
            .AsEnumerable()
            .Select(x => (x.EmployeeId, x.WorkDate))
            .ToHashSet();
        var daysInMonth = DateTime.DaysInMonth(schedule.Year, schedule.Month);
        var maxDays = Math.Max(0, daysInMonth - settings.MinDaysOffPerMonth);
        if (settings.MaxWorkDaysPerMonth.HasValue) maxDays = Math.Min(maxDays, settings.MaxWorkDaysPerMonth.Value);

        foreach (var employeeId in employeeIds)
        {
            var count = assignments.Count(x => x.EmployeeId == employeeId && x.WorkDate.Year == schedule.Year && x.WorkDate.Month == schedule.Month);
            if (count >= maxDays)
                throw AppException.Rule("EMPLOYEE_UNAVAILABLE", "Colaborador atingiu o limite mensal de dias trabalhados.");
            var before = CountDirection(assignments, employeeId, date, -1);
            var after = CountDirection(assignments, employeeId, date, 1);
            if (before + 1 + after > settings.MaxConsecutiveWorkDays)
                throw AppException.Rule("EMPLOYEE_UNAVAILABLE", "Colaborador atingiria o limite de dias consecutivos.");
        }
    }

    private static int CountDirection(HashSet<(Guid EmployeeId, DateOnly WorkDate)> assignments, Guid employeeId, DateOnly date, int direction)
    {
        var count = 0;
        for (var current = date.AddDays(direction); assignments.Contains((employeeId, current)); current = current.AddDays(direction)) count++;
        return count;
    }

    private ScheduleResponse Map(Schedule schedule)
    {
        var assignmentsQuery = Db.ScheduleAssignments.Where(x => x.ScheduleId == schedule.Id);
        var isEmployee = string.Equals(Current.Role, "EMPLOYEE", StringComparison.OrdinalIgnoreCase);
        if (isEmployee && Current.UserId is Guid currentUserId)
        {
            var currentEmployeeId = Db.Employees
                .Where(x => x.OrganizationId == schedule.OrganizationId && x.UserId == currentUserId)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefault();
            assignmentsQuery = assignmentsQuery.Where(x => x.EmployeeId == currentEmployeeId);
        }
        var assignments = assignmentsQuery.OrderBy(x => x.WorkDate).ThenBy(x => x.EmployeeId).ToList();
        var employeeIds = assignments.Select(x => x.EmployeeId).Distinct().ToArray();
        var employeeUserIds = Db.Employees.Where(x => employeeIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.UserId);
        var userIds = employeeUserIds.Values.ToArray();
        var names = Db.Users.Where(x => userIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.Name);
        var assignmentDtos = assignments.Select(x => new ScheduleAssignmentResponse(
            x.Id,
            x.EmployeeId,
            names.GetValueOrDefault(employeeUserIds.GetValueOrDefault(x.EmployeeId), "Colaborador"),
            x.WorkDate,
            Status(x.Source),
            DeserializeReasons(x.ExplanationJson))).ToArray();
        var warningDtos = isEmployee
            ? []
            : Db.ScheduleWarnings.Where(x => x.ScheduleId == schedule.Id).OrderBy(x => x.Date)
                .Select(x => new ScheduleWarningResponse(x.Date, x.Code, x.Message)).ToArray();
        return new ScheduleResponse(
            schedule.Id,
            schedule.Year,
            schedule.Month,
            Status(schedule.Status),
            schedule.Revision,
            schedule.CreatedAt,
            schedule.PublishedBy,
            schedule.PublishedAt,
            Convert.ToBase64String(schedule.RowVersion),
            assignmentDtos,
            warningDtos);
    }

    private static IReadOnlyList<string> DeserializeReasons(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
