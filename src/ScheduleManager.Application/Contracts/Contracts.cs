namespace ScheduleManager.Application.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalItems, int TotalPages);

public sealed record LoginRequest(string Email, string Password);
public sealed record ActivateRequest(string Email, string ActivationCode, string NewPassword);
public sealed record AuthUserResponse(Guid Id, string Name, string Role, Guid OrganizationId, Guid? EmployeeId);
public sealed record AuthResponse(string AccessToken, string TokenType, int ExpiresIn, AuthUserResponse User);
public sealed record CurrentUserResponse(Guid Id, string Name, string Email, string Role, Guid OrganizationId, Guid? EmployeeId);
public sealed record RefreshResult(AuthResponse Response, string RefreshToken);
public sealed record LoginResult(AuthResponse Response, string RefreshToken, IReadOnlyList<Guid> RevokedSessionIds);

public sealed record CreateEmployeeRequest(
    string Name,
    string Phone,
    string EmployeeNumber,
    string Email,
    int ProductivityLevel);

public sealed record UpdateEmployeeRequest(
    string Name,
    string Phone,
    string EmployeeNumber,
    string Email,
    int ProductivityLevel,
    string RowVersion);

public sealed record EmployeeResponse(
    Guid Id,
    Guid UserId,
    string Name,
    string Phone,
    string EmployeeNumber,
    string Email,
    int ProductivityLevel,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string RowVersion);

public sealed record CreateEmployeeResponse(EmployeeResponse Employee, string ActivationCode);

public sealed record CreateScheduleRequest(int Year, int Month);
public sealed record UpdateScheduleDayRequest(IReadOnlyList<Guid> EmployeeIds, string RowVersion);
public sealed record PublishScheduleRequest(string RowVersion);
public sealed record ScheduleAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly WorkDate,
    string Source,
    IReadOnlyList<string> Reasons);
public sealed record ScheduleWarningResponse(DateOnly Date, string Code, string Message);
public sealed record ScheduleResponse(
    Guid Id,
    int Year,
    int Month,
    string Status,
    int Revision,
    DateTimeOffset CreatedAt,
    Guid? PublishedBy,
    DateTimeOffset? PublishedAt,
    string RowVersion,
    IReadOnlyList<ScheduleAssignmentResponse> Assignments,
    IReadOnlyList<ScheduleWarningResponse> Warnings);

public sealed record CreateTimeOffRequest(DateOnly Date, string ReasonCategory, string? ReasonDescription);
public sealed record ApproveTimeOffRequest(bool AcknowledgeCoverageRisk);
public sealed record RejectTimeOffRequest(string Reason);
public sealed record TimeOffResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly Date,
    string ReasonCategory,
    string? ReasonDescription,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedBy,
    string? RejectionReason,
    string RowVersion);

public sealed record ShiftSwapCandidateResponse(Guid EmployeeId, string Name, string EmployeeNumber);
public sealed record CreateShiftSwapRequest(DateOnly Date, Guid TargetEmployeeId);
public sealed record ShiftSwapResponse(
    Guid Id,
    Guid RequesterEmployeeId,
    string RequesterName,
    Guid TargetEmployeeId,
    string TargetName,
    DateOnly Date,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RespondedAt,
    string RowVersion,
    bool CanRespond);

public sealed record NotificationResponse(
    Guid Id,
    string Type,
    Guid ReferenceId,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
