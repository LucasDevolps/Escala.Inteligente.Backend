using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Services;

public sealed class ShiftSwapService(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    INotificationCipher cipher,
    IRealtimeNotifier realtime)
    : ApplicationServiceBase(db, current, clock, cipher, realtime), IShiftSwapService
{
    public Task<IReadOnlyList<ShiftSwapCandidateResponse>> CandidatesAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireEmployee();
        cancellationToken.ThrowIfCancellationRequested();
        if (date == default)
            throw AppException.Validation(new Dictionary<string, string[]> { ["date"] = ["Data é obrigatória."] });
        var requester = CurrentEmployee(userId, organizationId);
        var schedule = PublishedSchedule(date, organizationId);
        if (!Db.ScheduleAssignments.Any(x => x.ScheduleId == schedule.Id && x.EmployeeId == requester.Id && x.WorkDate == date))
            throw AppException.Rule("SHIFT_SWAP_NOT_ALLOWED", "O solicitante não está escalado nesta data.");
        var assigned = Db.ScheduleAssignments.Where(x => x.ScheduleId == schedule.Id && x.WorkDate == date).Select(x => x.EmployeeId).ToHashSet();
        var unavailable = Db.TimeOffRequests
            .Where(x => x.OrganizationId == organizationId && x.Date == date &&
                        (x.Status == TimeOffStatus.Pending || x.Status == TimeOffStatus.Approved))
            .Select(x => x.EmployeeId).ToHashSet();
        var candidateEmployees = Db.Employees
            .Where(x => x.OrganizationId == organizationId && x.IsActive && x.Id != requester.Id)
            .ToList()
            .Where(x => !assigned.Contains(x.Id) && !unavailable.Contains(x.Id));
        var activeUsers = Db.Users.Where(x => x.OrganizationId == organizationId && x.IsActive).ToDictionary(x => x.Id);
        var candidates = candidateEmployees
            .Where(x => activeUsers.ContainsKey(x.UserId))
            .Select(x => new ShiftSwapCandidateResponse(x.Id, activeUsers[x.UserId].Name, x.EmployeeNumber))
            .OrderBy(x => x.Name)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ShiftSwapCandidateResponse>>(candidates);
    }

    public async Task<ShiftSwapResponse> CreateAsync(CreateShiftSwapRequest request, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireEmployee();
        if (request.Date == default || request.TargetEmployeeId == Guid.Empty)
            throw AppException.Validation(new Dictionary<string, string[]> { ["shiftSwap"] = ["Data e colaborador alvo são obrigatórios."] });
        var requester = CurrentEmployee(userId, organizationId);
        var target = Db.Employees.SingleOrDefault(x => x.Id == request.TargetEmployeeId && x.OrganizationId == organizationId && x.IsActive)
            ?? throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo indisponível.");
        var targetUser = Db.Users.SingleOrDefault(x => x.Id == target.UserId && x.OrganizationId == organizationId && x.IsActive)
            ?? throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo indisponível.");
        var schedule = PublishedSchedule(request.Date, organizationId);
        ValidateAvailability(schedule, requester, target, request.Date, organizationId);
        if (Db.ShiftSwaps.Any(x => x.OrganizationId == organizationId && x.RequesterEmployeeId == requester.Id &&
                                  x.Date == request.Date && x.Status == ShiftSwapStatus.Pending))
            throw AppException.Rule("SHIFT_SWAP_NOT_ALLOWED", "Já existe uma troca pendente para o solicitante nesta data.");

        var swap = new ShiftSwapRequest(organizationId, schedule.Id, requester.Id, target.Id, request.Date, Clock.UtcNow);
        var notifications = new List<Notification>();
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.Add(swap);
                notifications.Add(AddNotification(organizationId, targetUser.Id, NotificationType.ShiftSwapRequested, swap.Id,
                    $"Você recebeu uma solicitação de troca para {swap.Date:dd/MM/yyyy}."));
                foreach (var managerId in ManagerIds(organizationId))
                    notifications.Add(AddNotification(organizationId, managerId, NotificationType.ShiftSwapRequested, swap.Id,
                        $"Há uma nova solicitação de troca para {swap.Date:dd/MM/yyyy}."));
                AddAudit("ShiftSwapRequested", "ShiftSwapRequest", swap.Id, "{\"fields\":[\"date\",\"targetEmployeeId\",\"status\"]}");
                await Db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("SHIFT_SWAP_NOT_ALLOWED", "Já existe uma troca pendente para o solicitante nesta data.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("SHIFT_SWAP_NOT_ALLOWED", "A disponibilidade mudou durante a solicitação; tente novamente.");
        }
        await NotifyRealtimeAsync(Realtime, notifications, cancellationToken);
        return Map(swap);
    }

    public Task<PagedResponse<ShiftSwapResponse>> ListAsync(int page, int pageSize, string? status, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireUser();
        (page, pageSize) = Validation.Page(page, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var query = Db.ShiftSwaps.Where(x => x.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ShiftSwapStatus>(status, true, out var parsedStatus) || !Enum.IsDefined(parsedStatus))
                throw AppException.Validation(new Dictionary<string, string[]> { ["status"] = ["Status de troca inválido."] });
            query = query.Where(x => x.Status == parsedStatus);
        }
        if (string.Equals(Current.Role, "EMPLOYEE", StringComparison.OrdinalIgnoreCase))
        {
            var employeeId = CurrentEmployee(userId, organizationId).Id;
            query = query.Where(x => x.RequesterEmployeeId == employeeId || x.TargetEmployeeId == employeeId);
        }
        var ordered = query.OrderByDescending(x => x.RequestedAt);
        var total = ordered.LongCount();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList().Select(Map).ToArray();
        return Task.FromResult(new PagedResponse<ShiftSwapResponse>(items, page, pageSize, total, TotalPages(total, pageSize)));
    }

    public async Task<ShiftSwapResponse> AcceptAsync(Guid id, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireEmployee();
        var targetId = CurrentEmployee(userId, organizationId).Id;
        var notifications = new List<Notification>();
        ShiftSwapRequest? acceptedSwap = null;

        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                Db.ClearTrackedChanges();
                var target = Db.Employees.SingleOrDefault(x => x.Id == targetId && x.OrganizationId == organizationId && x.IsActive)
                    ?? throw AppException.Conflict("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo indisponível.");
                var swap = Find(id, organizationId);
                if (swap.TargetEmployeeId != target.Id) throw AppException.Forbidden();
                if (swap.Status != ShiftSwapStatus.Pending)
                    throw AppException.Conflict("SHIFT_SWAP_ALREADY_PROCESSED", "A solicitação de troca já foi processada.");
                var requester = Db.Employees.SingleOrDefault(x => x.Id == swap.RequesterEmployeeId && x.OrganizationId == organizationId && x.IsActive)
                    ?? throw AppException.Conflict("SHIFT_SWAP_TARGET_UNAVAILABLE", "O solicitante não está mais disponível.");
                var schedule = Db.Schedules.SingleOrDefault(x => x.Id == swap.ScheduleId && x.OrganizationId == organizationId && x.Status == ScheduleStatus.Published)
                    ?? throw AppException.Conflict("SCHEDULE_NOT_PUBLISHED", "A escala não está publicada.");
                ValidateAvailability(schedule, requester, target, swap.Date, organizationId);
                ValidateTargetLimits(schedule, target.Id, swap.Date, organizationId);
                var requesterAssignment = Db.ScheduleAssignments.SingleOrDefault(x => x.ScheduleId == schedule.Id &&
                    x.EmployeeId == requester.Id && x.WorkDate == swap.Date)
                    ?? throw AppException.Conflict("SHIFT_SWAP_TARGET_UNAVAILABLE", "A atribuição original não está mais disponível.");
                Db.Remove(requesterAssignment);
                Db.Add(new ScheduleAssignment(schedule.Id, target.Id, swap.Date, AssignmentSource.Swap, userId, Clock.UtcNow,
                    "[\"Troca aceita pelo colaborador alvo.\",\"Disponibilidade revalidada no aceite.\"]"));
                swap.Accept(Clock.UtcNow);
                schedule.IncrementRevision();
                notifications.Add(AddNotification(organizationId, requester.UserId, NotificationType.ShiftSwapAccepted, swap.Id,
                    $"Sua troca para {swap.Date:dd/MM/yyyy} foi aceita."));
                notifications.Add(AddNotification(organizationId, target.UserId, NotificationType.ShiftSwapAccepted, swap.Id,
                    $"A troca para {swap.Date:dd/MM/yyyy} foi concluída."));
                foreach (var managerId in ManagerIds(organizationId))
                    notifications.Add(AddNotification(organizationId, managerId, NotificationType.ShiftSwapAccepted, swap.Id,
                        $"Uma troca para {swap.Date:dd/MM/yyyy} foi concluída."));
                AddAudit("ShiftSwapAccepted", "ShiftSwapRequest", swap.Id,
                    "{\"fields\":[\"status\",\"respondedAt\",\"scheduleRevision\"]}");
                await Db.SaveChangesAsync(ct);
                acceptedSwap = swap;
            }, cancellationToken);
        }
        catch (AppException exception) when (
            exception.Kind == ErrorKind.Unprocessable &&
            exception.Code is "SHIFT_SWAP_TARGET_UNAVAILABLE" or "SHIFT_SWAP_NOT_ALLOWED")
        {
            throw AppException.Conflict(exception.Code, exception.Message);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A troca ou escala foi alterada por outra operação.");
        }
        catch (PersistenceConflictException)
        {
            throw AppException.Conflict("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo não está mais disponível para esta troca.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A troca ou escala foi alterada por outra operação.");
        }
        await NotifyRealtimeAsync(Realtime, notifications, cancellationToken);
        return Map(acceptedSwap!);
    }

    public async Task<ShiftSwapResponse> RejectAsync(Guid id, CancellationToken cancellationToken)
    {
        var (userId, organizationId) = RequireEmployee();
        var target = CurrentEmployee(userId, organizationId);
        var swap = Find(id, organizationId);
        if (swap.TargetEmployeeId != target.Id) throw AppException.Forbidden();
        if (swap.Status != ShiftSwapStatus.Pending)
            throw AppException.Conflict("SHIFT_SWAP_ALREADY_PROCESSED", "A solicitação de troca já foi processada.");
        var requester = Db.Employees.Single(x => x.Id == swap.RequesterEmployeeId && x.OrganizationId == organizationId);
        var notifications = new List<Notification>();
        try
        {
            await Db.ExecuteInTransactionAsync(async ct =>
            {
                swap.Reject(Clock.UtcNow);
                notifications.Add(AddNotification(organizationId, requester.UserId, NotificationType.ShiftSwapRejected, swap.Id,
                    $"Sua troca para {swap.Date:dd/MM/yyyy} foi recusada."));
                foreach (var managerId in ManagerIds(organizationId))
                    notifications.Add(AddNotification(organizationId, managerId, NotificationType.ShiftSwapRejected, swap.Id,
                        $"Uma troca para {swap.Date:dd/MM/yyyy} foi recusada."));
                AddAudit("ShiftSwapRejected", "ShiftSwapRequest", swap.Id, "{\"fields\":[\"status\",\"respondedAt\"]}");
                await Db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A troca foi alterada por outra operação.");
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("CONCURRENCY_CONFLICT", "A troca foi alterada por outra operação.");
        }
        await NotifyRealtimeAsync(Realtime, notifications, cancellationToken);
        return Map(swap);
    }

    private void ValidateAvailability(Schedule schedule, Employee requester, Employee target, DateOnly date, Guid organizationId)
    {
        if (requester.OrganizationId != organizationId || target.OrganizationId != organizationId || !requester.IsActive || !target.IsActive || requester.Id == target.Id)
            throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo indisponível.");
        if (!Db.ScheduleAssignments.Any(x => x.ScheduleId == schedule.Id && x.EmployeeId == requester.Id && x.WorkDate == date))
            throw AppException.Rule("SHIFT_SWAP_NOT_ALLOWED", "O solicitante não está escalado nesta data.");
        if (Db.ScheduleAssignments.Any(x => x.ScheduleId == schedule.Id && x.EmployeeId == target.Id && x.WorkDate == date))
            throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo já está escalado nesta data.");
        if (Db.TimeOffRequests.Any(x => x.OrganizationId == organizationId && x.EmployeeId == target.Id && x.Date == date &&
                                      (x.Status == TimeOffStatus.Pending || x.Status == TimeOffStatus.Approved)))
            throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo possui solicitação de folga nesta data.");
    }

    private void ValidateTargetLimits(Schedule schedule, Guid targetEmployeeId, DateOnly date, Guid organizationId)
    {
        var settings = Db.ScheduleSettings.SingleOrDefault(x => x.OrganizationId == organizationId)
            ?? new OrganizationScheduleSettings(organizationId);
        var start = new DateOnly(schedule.Year, schedule.Month, 1);
        var scheduleIds = Db.Schedules.Where(x => x.OrganizationId == organizationId).Select(x => x.Id).ToHashSet();
        var dates = Db.ScheduleAssignments
            .Where(x => scheduleIds.Contains(x.ScheduleId) && x.EmployeeId == targetEmployeeId &&
                        x.WorkDate >= start.AddMonths(-1) && x.WorkDate < start.AddMonths(1))
            .Select(x => x.WorkDate).ToHashSet();
        var maxDays = Math.Max(0, DateTime.DaysInMonth(schedule.Year, schedule.Month) - settings.MinDaysOffPerMonth);
        if (settings.MaxWorkDaysPerMonth.HasValue) maxDays = Math.Min(maxDays, settings.MaxWorkDaysPerMonth.Value);
        if (dates.Count(x => x.Year == schedule.Year && x.Month == schedule.Month) >= maxDays)
            throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo atingiu o limite mensal.");
        var before = CountDirection(dates, date, -1);
        var after = CountDirection(dates, date, 1);
        if (before + 1 + after > settings.MaxConsecutiveWorkDays)
            throw AppException.Rule("SHIFT_SWAP_TARGET_UNAVAILABLE", "Colaborador alvo atingiria o limite de dias consecutivos.");
    }

    private static int CountDirection(HashSet<DateOnly> dates, DateOnly date, int direction)
    {
        var count = 0;
        for (var current = date.AddDays(direction); dates.Contains(current); current = current.AddDays(direction)) count++;
        return count;
    }

    private Employee CurrentEmployee(Guid userId, Guid organizationId) =>
        Db.Employees.SingleOrDefault(x => x.UserId == userId && x.OrganizationId == organizationId && x.IsActive)
        ?? throw AppException.NotFound("EMPLOYEE_NOT_FOUND", "Colaborador não encontrado.");

    private Schedule PublishedSchedule(DateOnly date, Guid organizationId) =>
        Db.Schedules.SingleOrDefault(x => x.OrganizationId == organizationId && x.Year == date.Year && x.Month == date.Month &&
                                          x.Status == ScheduleStatus.Published)
        ?? throw AppException.Conflict("SCHEDULE_NOT_PUBLISHED", "Não existe escala publicada para esta data.");

    private ShiftSwapRequest Find(Guid id, Guid organizationId) =>
        Db.ShiftSwaps.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
        ?? throw AppException.NotFound("SHIFT_SWAP_NOT_FOUND", "Solicitação de troca não encontrada.");

    private IReadOnlyList<Guid> ManagerIds(Guid organizationId) => Db.Users
        .Where(x => x.OrganizationId == organizationId && x.Role == UserRole.Manager && x.IsActive)
        .Select(x => x.Id).ToList();

    private ShiftSwapResponse Map(ShiftSwapRequest swap)
    {
        var requester = Db.Employees.Single(x => x.Id == swap.RequesterEmployeeId && x.OrganizationId == swap.OrganizationId);
        var target = Db.Employees.Single(x => x.Id == swap.TargetEmployeeId && x.OrganizationId == swap.OrganizationId);
        var requesterUser = Db.Users.Single(x => x.Id == requester.UserId && x.OrganizationId == swap.OrganizationId);
        var targetUser = Db.Users.Single(x => x.Id == target.UserId && x.OrganizationId == swap.OrganizationId);
        var canRespond = Current.UserId == target.UserId && swap.Status == ShiftSwapStatus.Pending;
        return new ShiftSwapResponse(
            swap.Id,
            requester.Id,
            requesterUser.Name,
            target.Id,
            targetUser.Name,
            swap.Date,
            Status(swap.Status),
            swap.RequestedAt,
            swap.RespondedAt,
            Convert.ToBase64String(swap.RowVersion),
            canRespond);
    }

    private static int TotalPages(long total, int pageSize) => total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
}
