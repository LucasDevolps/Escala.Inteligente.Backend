using ScheduleManager.Application.Contracts;

namespace ScheduleManager.Application.Services;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task ActivateAsync(ActivateRequest request, CancellationToken cancellationToken);
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
    Task<CurrentUserResponse> MeAsync(CancellationToken cancellationToken);
    Task<SessionValidationResult> ValidateSessionAsync(Guid userId, Guid organizationId, Guid sessionId, CancellationToken cancellationToken);
}

public sealed record SessionValidationResult(bool IsValid, string? ErrorCode)
{
    public static SessionValidationResult Valid { get; } = new(true, null);
    public static SessionValidationResult Invalid(string code) => new(false, code);
}

public interface IEmployeeService
{
    Task<PagedResponse<EmployeeResponse>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<EmployeeResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<CreateEmployeeResponse> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken);
    Task<EmployeeResponse> UpdateAsync(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken);
    Task DeactivateAsync(Guid id, CancellationToken cancellationToken);
}

public interface IScheduleService
{
    Task<ScheduleResponse> CreateAsync(CreateScheduleRequest request, CancellationToken cancellationToken);
    Task<ScheduleResponse> GetAsync(int year, int month, CancellationToken cancellationToken);
    Task<ScheduleResponse> GenerateAsync(Guid scheduleId, CancellationToken cancellationToken);
    Task<ScheduleResponse> UpdateDayAsync(Guid scheduleId, DateOnly date, UpdateScheduleDayRequest request, CancellationToken cancellationToken);
    Task<ScheduleResponse> PublishAsync(Guid scheduleId, PublishScheduleRequest request, CancellationToken cancellationToken);
}

public interface ITimeOffService
{
    Task<TimeOffResponse> CreateAsync(CreateTimeOffRequest request, CancellationToken cancellationToken);
    Task<PagedResponse<TimeOffResponse>> ListAsync(int page, int pageSize, string? status, CancellationToken cancellationToken);
    Task<TimeOffResponse> ApproveAsync(Guid id, ApproveTimeOffRequest request, CancellationToken cancellationToken);
    Task<TimeOffResponse> RejectAsync(Guid id, RejectTimeOffRequest request, CancellationToken cancellationToken);
}

public interface IShiftSwapService
{
    Task<IReadOnlyList<ShiftSwapCandidateResponse>> CandidatesAsync(DateOnly date, CancellationToken cancellationToken);
    Task<ShiftSwapResponse> CreateAsync(CreateShiftSwapRequest request, CancellationToken cancellationToken);
    Task<PagedResponse<ShiftSwapResponse>> ListAsync(int page, int pageSize, string? status, CancellationToken cancellationToken);
    Task<ShiftSwapResponse> AcceptAsync(Guid id, CancellationToken cancellationToken);
    Task<ShiftSwapResponse> RejectAsync(Guid id, CancellationToken cancellationToken);
}

public interface INotificationService
{
    Task<PagedResponse<NotificationResponse>> ListAsync(int page, int pageSize, bool unreadOnly, CancellationToken cancellationToken);
    Task<NotificationResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task MarkReadAsync(Guid id, CancellationToken cancellationToken);
}
