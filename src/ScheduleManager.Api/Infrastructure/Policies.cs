namespace ScheduleManager.Api.Infrastructure;

public static class Policies
{
    public const string ManageEmployees = nameof(ManageEmployees);
    public const string ManageSchedules = nameof(ManageSchedules);
    public const string ApproveTimeOff = nameof(ApproveTimeOff);
    public const string ViewOwnSchedule = nameof(ViewOwnSchedule);
    public const string RequestShiftSwap = nameof(RequestShiftSwap);
    public const string Employee = nameof(Employee);
}
