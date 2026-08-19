using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Domain.Entities;

public sealed class OrganizationScheduleSettings : Entity, ITenantEntity
{
    private OrganizationScheduleSettings() { }

    public OrganizationScheduleSettings(Guid organizationId) : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        MinEmployeesPerDay = 1;
        MaxEmployeesPerDay = 1;
        MaxConsecutiveWorkDays = 6;
        MinDaysOffPerMonth = 4;
        BalanceWeekends = true;
        ProductivityWeight = 10;
    }

    public Guid OrganizationId { get; private set; }
    public int MinEmployeesPerDay { get; private set; }
    public int MaxEmployeesPerDay { get; private set; }
    public int MaxConsecutiveWorkDays { get; private set; }
    public int MinDaysOffPerMonth { get; private set; }
    public int? MaxWorkDaysPerMonth { get; private set; }
    public bool BalanceWeekends { get; private set; }
    public int ProductivityWeight { get; private set; }

    public void Configure(
        int minEmployeesPerDay,
        int maxEmployeesPerDay,
        int maxConsecutiveWorkDays,
        int minDaysOffPerMonth,
        int? maxWorkDaysPerMonth,
        bool balanceWeekends,
        int productivityWeight)
    {
        if (minEmployeesPerDay < 1 || maxEmployeesPerDay < minEmployeesPerDay || maxConsecutiveWorkDays < 1 ||
            minDaysOffPerMonth < 0 || productivityWeight is < 0 or > 20 || maxWorkDaysPerMonth is < 1)
        {
            throw new DomainRuleException("VALIDATION_ERROR", "Configuração de escala inválida.");
        }

        MinEmployeesPerDay = minEmployeesPerDay;
        MaxEmployeesPerDay = maxEmployeesPerDay;
        MaxConsecutiveWorkDays = maxConsecutiveWorkDays;
        MinDaysOffPerMonth = minDaysOffPerMonth;
        MaxWorkDaysPerMonth = maxWorkDaysPerMonth;
        BalanceWeekends = balanceWeekends;
        ProductivityWeight = productivityWeight;
    }
}

public sealed class Schedule : Entity, ITenantEntity
{
    private Schedule() { }

    public Schedule(Guid organizationId, int year, int month, Guid createdBy, DateTimeOffset now) : base(DomainIds.New())
    {
        if (year is < 2000 or > 2200 || month is < 1 or > 12)
        {
            throw new DomainRuleException("VALIDATION_ERROR", "Ano ou mês inválido.");
        }

        OrganizationId = organizationId;
        Year = year;
        Month = month;
        CreatedBy = createdBy;
        CreatedAt = now;
        Status = ScheduleStatus.Draft;
    }

    public Guid OrganizationId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public ScheduleStatus Status { get; private set; }
    public int Revision { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? PublishedBy { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void MarkSuggested()
    {
        EnsureEditable();
        Status = ScheduleStatus.Suggested;
    }

    public void MarkEdited()
    {
        EnsureEditable();
        Status = ScheduleStatus.InReview;
    }

    public void Publish(Guid managerId, DateTimeOffset now)
    {
        EnsureEditable();
        Status = ScheduleStatus.Published;
        PublishedBy = managerId;
        PublishedAt = now;
        Revision++;
    }

    public void IncrementRevision() => Revision++;

    private void EnsureEditable()
    {
        if (Status is ScheduleStatus.Published or ScheduleStatus.Closed)
        {
            throw new DomainRuleException("SCHEDULE_ALREADY_PUBLISHED", "A escala publicada ou encerrada não pode ser editada.");
        }
    }
}

public sealed class ScheduleAssignment : Entity
{
    private ScheduleAssignment() { }

    public ScheduleAssignment(
        Guid scheduleId,
        Guid employeeId,
        DateOnly workDate,
        AssignmentSource source,
        Guid createdBy,
        DateTimeOffset now,
        string explanationJson = "[]") : base(DomainIds.New())
    {
        ScheduleId = scheduleId;
        EmployeeId = employeeId;
        WorkDate = workDate;
        Source = source;
        CreatedBy = createdBy;
        CreatedAt = now;
        ExplanationJson = explanationJson;
    }

    public Guid ScheduleId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public DateOnly WorkDate { get; private set; }
    public AssignmentSource Source { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public string ExplanationJson { get; private set; } = "[]";
}

public sealed class ScheduleWarning : Entity
{
    private ScheduleWarning() { }

    public ScheduleWarning(Guid scheduleId, DateOnly date, string code, string message) : base(DomainIds.New())
    {
        ScheduleId = scheduleId;
        Date = date;
        Code = code;
        Message = message;
    }

    public Guid ScheduleId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
}

public sealed class TimeOffRequest : Entity, ITenantEntity
{
    private TimeOffRequest() { }

    public TimeOffRequest(
        Guid organizationId,
        Guid employeeId,
        DateOnly date,
        TimeOffReasonCategory reasonCategory,
        string? reasonDescription,
        DateTimeOffset now) : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        EmployeeId = employeeId;
        Date = date;
        ReasonCategory = reasonCategory;
        ReasonDescription = reasonDescription;
        Status = TimeOffStatus.Pending;
        RequestedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public TimeOffReasonCategory ReasonCategory { get; private set; }
    public string? ReasonDescription { get; private set; }
    public TimeOffStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public string? RejectionReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Approve(Guid reviewerId, DateTimeOffset now)
    {
        EnsurePending();
        Status = TimeOffStatus.Approved;
        ReviewedAt = now;
        ReviewedBy = reviewerId;
    }

    public void Reject(Guid reviewerId, string reason, DateTimeOffset now)
    {
        EnsurePending();
        Status = TimeOffStatus.Rejected;
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        RejectionReason = reason;
    }

    private void EnsurePending()
    {
        if (Status != TimeOffStatus.Pending)
        {
            throw new DomainRuleException("TIME_OFF_ALREADY_PROCESSED", "A solicitação de folga já foi processada.");
        }
    }
}

public sealed class ShiftSwapRequest : Entity, ITenantEntity
{
    private ShiftSwapRequest() { }

    public ShiftSwapRequest(
        Guid organizationId,
        Guid scheduleId,
        Guid requesterEmployeeId,
        Guid targetEmployeeId,
        DateOnly date,
        DateTimeOffset now) : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        ScheduleId = scheduleId;
        RequesterEmployeeId = requesterEmployeeId;
        TargetEmployeeId = targetEmployeeId;
        Date = date;
        Status = ShiftSwapStatus.Pending;
        RequestedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid ScheduleId { get; private set; }
    public Guid RequesterEmployeeId { get; private set; }
    public Guid TargetEmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public ShiftSwapStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? RespondedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Accept(DateTimeOffset now)
    {
        EnsurePending();
        Status = ShiftSwapStatus.Accepted;
        RespondedAt = now;
    }

    public void Reject(DateTimeOffset now)
    {
        EnsurePending();
        Status = ShiftSwapStatus.Rejected;
        RespondedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != ShiftSwapStatus.Pending)
        {
            throw new DomainRuleException("SHIFT_SWAP_ALREADY_PROCESSED", "A solicitação de troca já foi processada.");
        }
    }
}
