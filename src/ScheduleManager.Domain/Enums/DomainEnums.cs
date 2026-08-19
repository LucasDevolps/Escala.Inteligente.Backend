namespace ScheduleManager.Domain.Enums;

public enum UserRole { Manager = 0, Employee = 1 }
public enum ProductivityLevel { Low = 0, Moderate = 1, High = 2 }
public enum ScheduleStatus { Draft = 0, Suggested = 1, InReview = 2, Published = 3, Closed = 4 }
public enum AssignmentSource { Suggested = 0, Manual = 1, Swap = 2, TimeOffAdjustment = 3 }
public enum TimeOffStatus { Pending = 0, Approved = 1, Rejected = 2, Cancelled = 3 }
public enum TimeOffReasonCategory { Personal = 0, Appointment = 1, Other = 2 }
public enum ShiftSwapStatus { Pending = 0, Accepted = 1, Rejected = 2, Cancelled = 3, Expired = 4 }
public enum NotificationType
{
    TimeOffRequested = 0,
    TimeOffApproved = 1,
    TimeOffRejected = 2,
    ShiftSwapRequested = 3,
    ShiftSwapAccepted = 4,
    ShiftSwapRejected = 5,
    SchedulePublished = 6,
    SessionRevoked = 7
}
