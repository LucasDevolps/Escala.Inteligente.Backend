using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Services;

public sealed class TimeOffService(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    INotificationCipher cipher,
    IRealtimeNotifier realtime)
    : ApplicationServiceBase(db, current, clock, cipher, realtime), ITimeOffService
{
    public async Task<TimeOffResponse> CreateAsync(CreateTimeOffRequest request, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireEmployee();
        var organization = Db.Organizations.Single(x => x.Id == organizationId);
        if (request.Date == default)
            throw AppException.Validation(new Dictionary<string, string[]> { ["date"] = ["Data é obrigatória."] });
        if (request.Date < Clock.Today(organization.TimeZoneId))
            throw AppException.Validation(new Dictionary<string, string[]> { ["date"] = ["Não é permitido solicitar folga para data passada."] });
        if (!Enum.TryParse<TimeOffReasonCategory>(request.ReasonCategory, true, out var category) || !Enum.IsDefined(category))
            throw AppException.Validation(new Dictionary<string, string[]> { ["reasonCategory"] = ["Categoria deve ser PERSONAL, APPOINTMENT ou OTHER."] });
        var description = string.IsNullOrWhiteSpace(request.ReasonDescription) ? null : request.ReasonDescription.Trim();
        if (description?.Length > 500)
            throw AppException.Validation(new Dictionary<string, string[]> { ["reasonDescription"] = ["Descrição deve possuir no máximo 500 caracteres."] });
        var employee = Db.Employees.SingleOrDefault(x => x.OrganizationId == organizationId && x.UserId == userId && x.IsActive)
            ?? throw AppException.NotFound("EMPLOYEE_NOT_FOUND", "Colaborador não encontrado.");
        if (Db.TimeOffRequests.Any(x => x.OrganizationId == organizationId && x.EmployeeId == employee.Id && x.Date == request.Date &&
                                        (x.Status == TimeOffStatus.Pending || x.Status == TimeOffStatus.Approved)))
            throw AppException.Rule("TIME_OFF_ALREADY_EXISTS", "Já existe uma solicitação ativa para esta data.");

        var timeOff = new TimeOffRequest(organizationId, employee.Id, request.Date, category, description, Clock.UtcNow);
        var notifications = new List<Notification>();
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.Add(timeOff);
                var managerIds = Db.Users.Where(x => x.OrganizationId == organizationId && x.Role == UserRole.Manager && x.IsActive).Select(x => x.Id).ToList();
                foreach (var managerId in managerIds)
                    notifications.Add(AddNotification(organizationId, managerId, NotificationType.TimeOffRequested, timeOff.Id,
                        $"Há uma nova solicitação de folga para {timeOff.Date:dd/MM/yyyy}."));
                AddAudit("TimeOffRequested", "TimeOffRequest", timeOff.Id, "{\"fields\":[\"date\",\"reasonCategory\",\"status\"]}");
                await Db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Rule("TIME_OFF_ALREADY_EXISTS", "Já existe uma solicitação ativa para esta data.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("TIME_OFF_CONFLICT", "A solicitação de folga sofreu uma alteração simultânea; tente novamente.");
        }
        await NotifyRealtimeAsync(Realtime, notifications, cancellationToken);
        return Map(timeOff);
    }

    public Task<PagedResponse<TimeOffResponse>> ListAsync(int page, int pageSize, string? status, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireUser();
        (page, pageSize) = Validation.Page(page, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var query = Db.TimeOffRequests.Where(x => x.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TimeOffStatus>(status, true, out var parsedStatus) || !Enum.IsDefined(parsedStatus))
                throw AppException.Validation(new Dictionary<string, string[]> { ["status"] = ["Status de folga inválido."] });
            query = query.Where(x => x.Status == parsedStatus);
        }
        if (string.Equals(Current.Role, "EMPLOYEE", StringComparison.OrdinalIgnoreCase))
        {
            var employeeId = Db.Employees.Where(x => x.OrganizationId == organizationId && x.UserId == userId).Select(x => x.Id).SingleOrDefault();
            query = query.Where(x => x.EmployeeId == employeeId);
        }
        var ordered = query.OrderByDescending(x => x.RequestedAt);
        var total = ordered.LongCount();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList().Select(Map).ToArray();
        return Task.FromResult(new PagedResponse<TimeOffResponse>(items, page, pageSize, total, TotalPages(total, pageSize)));
    }

    public async Task<TimeOffResponse> ApproveAsync(Guid id, ApproveTimeOffRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        Notification? notification = null;
        TimeOffRequest? approvedTimeOff = null;
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.ClearTrackedChanges();
                var timeOff = Find(id, organizationId);
                if (timeOff.Status != TimeOffStatus.Pending)
                    throw AppException.Conflict("TIME_OFF_ALREADY_PROCESSED", "A solicitação de folga já foi processada.");
                var employee = Db.Employees.Single(x => x.Id == timeOff.EmployeeId && x.OrganizationId == organizationId);
                var schedule = Db.Schedules.SingleOrDefault(x => x.OrganizationId == organizationId &&
                    x.Year == timeOff.Date.Year && x.Month == timeOff.Date.Month);
                var assignment = schedule is null
                    ? null
                    : Db.ScheduleAssignments.SingleOrDefault(x => x.ScheduleId == schedule.Id &&
                        x.EmployeeId == employee.Id && x.WorkDate == timeOff.Date);
                var settings = Db.ScheduleSettings.SingleOrDefault(x => x.OrganizationId == organizationId)
                    ?? new OrganizationScheduleSettings(organizationId);
                var remainingCoverage = assignment is null || schedule is null
                    ? int.MaxValue
                    : Db.ScheduleAssignments.Count(x => x.ScheduleId == schedule.Id && x.WorkDate == timeOff.Date) - 1;
                if (assignment is not null && schedule?.Status == ScheduleStatus.Published &&
                    remainingCoverage < settings.MinEmployeesPerDay && !request.AcknowledgeCoverageRisk)
                    throw AppException.Conflict("COVERAGE_RISK", "A aprovação reduzirá a cobertura abaixo do mínimo; confirme explicitamente o risco.");

                timeOff.Approve(managerId, Clock.UtcNow);
                if (assignment is not null && schedule is not null)
                {
                    Db.Remove(assignment);
                    if (schedule.Status == ScheduleStatus.Published) schedule.IncrementRevision();
                    else
                    {
                        EnsureScheduleCanBeAdjusted(schedule);
                        schedule.MarkEdited();
                    }
                    if (remainingCoverage < settings.MinEmployeesPerDay)
                    {
                        Db.Add(new ScheduleWarning(schedule.Id, timeOff.Date, "MINIMUM_COVERAGE_UNAVAILABLE",
                            $"Cobertura após aprovação de folga: {remainingCoverage} de {settings.MinEmployeesPerDay} colaboradores."));
                    }
                }
                notification = AddNotification(organizationId, employee.UserId, NotificationType.TimeOffApproved, timeOff.Id,
                    $"Sua solicitação de folga para {timeOff.Date:dd/MM/yyyy} foi aprovada.");
                AddAudit("TimeOffApproved", "TimeOffRequest", timeOff.Id, "{\"fields\":[\"status\",\"reviewedAt\",\"reviewedBy\"]}");
                await Db.SaveChangesAsync(ct);
                approvedTimeOff = timeOff;
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A solicitação ou escala foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A solicitação ou escala foi alterada por outra operação.");
        }
        if (notification is not null) await Realtime.NotificationCreatedAsync(notification.RecipientUserId, notification.Id, cancellationToken);
        return Map(approvedTimeOff!);
    }

    public async Task<TimeOffResponse> RejectAsync(Guid id, RejectTimeOffRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 1 or > 500)
            throw AppException.Validation(new Dictionary<string, string[]> { ["reason"] = ["O motivo deve possuir entre 1 e 500 caracteres."] });
        var timeOff = Find(id, organizationId);
        if (timeOff.Status != TimeOffStatus.Pending)
            throw AppException.Conflict("TIME_OFF_ALREADY_PROCESSED", "A solicitação de folga já foi processada.");
        var employee = Db.Employees.Single(x => x.Id == timeOff.EmployeeId && x.OrganizationId == organizationId);
        Notification? notification = null;
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                timeOff.Reject(managerId, reason, Clock.UtcNow);
                notification = AddNotification(organizationId, employee.UserId, NotificationType.TimeOffRejected, timeOff.Id,
                    $"Sua solicitação de folga para {timeOff.Date:dd/MM/yyyy} foi recusada.");
                AddAudit("TimeOffRejected", "TimeOffRequest", timeOff.Id, "{\"fields\":[\"status\",\"reviewedAt\",\"reviewedBy\"]}");
                await Db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A solicitação foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A solicitação foi alterada por outra operação.");
        }
        if (notification is not null) await Realtime.NotificationCreatedAsync(notification.RecipientUserId, notification.Id, cancellationToken);
        return Map(timeOff);
    }

    private TimeOffRequest Find(Guid id, Guid organizationId) =>
        Db.TimeOffRequests.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
        ?? throw AppException.NotFound("TIME_OFF_NOT_FOUND", "Solicitação de folga não encontrada.");

    private static void EnsureScheduleCanBeAdjusted(Schedule schedule)
    {
        if (schedule.Status == ScheduleStatus.Closed)
            throw AppException.Conflict("SCHEDULE_CLOSED", "Não é possível aprovar a folga porque a escala está encerrada.");
    }

    private TimeOffResponse Map(TimeOffRequest request)
    {
        var employee = Db.Employees.Single(x => x.Id == request.EmployeeId && x.OrganizationId == request.OrganizationId);
        var user = Db.Users.Single(x => x.Id == employee.UserId && x.OrganizationId == request.OrganizationId);
        return new TimeOffResponse(
            request.Id,
            request.EmployeeId,
            user.Name,
            request.Date,
            Status(request.ReasonCategory),
            request.ReasonDescription,
            Status(request.Status),
            request.RequestedAt,
            request.ReviewedAt,
            request.ReviewedBy,
            request.RejectionReason,
            Convert.ToBase64String(request.RowVersion));
    }

    private static int TotalPages(long total, int pageSize) => total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
}
