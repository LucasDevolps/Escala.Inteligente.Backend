SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DATEFIRST 7;

BEGIN TRANSACTION;

DECLARE @OrganizationId uniqueidentifier;
DECLARE @MiriamId uniqueidentifier;
DECLARE @EliId uniqueidentifier;
DECLARE @ManagerId uniqueidentifier;
DECLARE @ScheduleId uniqueidentifier;
DECLARE @Now datetimeoffset(3) = SYSDATETIMEOFFSET();

SELECT TOP (1) @OrganizationId = employee.OrganizationId
FROM schedule.Employees employee
JOIN schedule.Users appUser ON appUser.Id = employee.UserId
WHERE appUser.Name = N'Miriam'
  AND employee.IsActive = 1
  AND EXISTS
  (
      SELECT 1
      FROM schedule.Employees eliEmployee
      JOIN schedule.Users eliUser ON eliUser.Id = eliEmployee.UserId
      WHERE eliEmployee.OrganizationId = employee.OrganizationId
        AND eliUser.Name = N'Eli'
        AND eliEmployee.IsActive = 1
  );

IF @OrganizationId IS NULL
    THROW 51000, 'Miriam e Eli existentes na mesma organização não foram localizadas.', 1;

IF (SELECT COUNT(*)
    FROM schedule.Employees employee
    JOIN schedule.Users appUser ON appUser.Id = employee.UserId
    WHERE employee.OrganizationId = @OrganizationId
      AND employee.IsActive = 1
      AND appUser.Name = N'Miriam') <> 1
    THROW 51001, 'A organização deve possuir exatamente uma Miriam ativa.', 1;

IF (SELECT COUNT(*)
    FROM schedule.Employees employee
    JOIN schedule.Users appUser ON appUser.Id = employee.UserId
    WHERE employee.OrganizationId = @OrganizationId
      AND employee.IsActive = 1
      AND appUser.Name = N'Eli') <> 1
    THROW 51002, 'A organização deve possuir exatamente um Eli ativo.', 1;

SELECT @MiriamId = employee.Id
FROM schedule.Employees employee
JOIN schedule.Users appUser ON appUser.Id = employee.UserId
WHERE employee.OrganizationId = @OrganizationId AND appUser.Name = N'Miriam' AND employee.IsActive = 1;

SELECT @EliId = employee.Id
FROM schedule.Employees employee
JOIN schedule.Users appUser ON appUser.Id = employee.UserId
WHERE employee.OrganizationId = @OrganizationId AND appUser.Name = N'Eli' AND employee.IsActive = 1;

SELECT TOP (1) @ManagerId = Id
FROM schedule.Users
WHERE OrganizationId = @OrganizationId AND Role = 0 AND IsActive = 1
ORDER BY CreatedAt;

IF @ManagerId IS NULL
    THROW 51003, 'A organização não possui gestor ativo para registrar a escala.', 1;

SELECT @ScheduleId = Id
FROM schedule.Schedules
WHERE OrganizationId = @OrganizationId AND Year = 2026 AND Month = 7;

IF @ScheduleId IS NULL
BEGIN
    SET @ScheduleId = NEWID();

    INSERT schedule.Schedules
        (Id, OrganizationId, Year, Month, Status, Revision, CreatedBy, CreatedAt, PublishedBy, PublishedAt)
    VALUES
        (@ScheduleId, @OrganizationId, 2026, 7, 3, 1, @ManagerId, @Now, @ManagerId, @Now);

    WITH Dates AS
    (
        SELECT CAST('2026-07-01' AS date) AS WorkDate
        UNION ALL
        SELECT DATEADD(day, 1, WorkDate)
        FROM Dates
        WHERE WorkDate < '2026-07-31'
    )
    INSERT schedule.ScheduleAssignments
        (Id, ScheduleId, EmployeeId, WorkDate, Source, CreatedAt, CreatedBy, ExplanationJson)
    SELECT
        NEWID(), @ScheduleId, @MiriamId, WorkDate, 1, @Now, @ManagerId,
        N'["Escala de referência baseada no padrão operacional de agosto de 2026."]'
    FROM Dates
    WHERE DATEPART(weekday, WorkDate) <> 1
    UNION ALL
    SELECT
        NEWID(), @ScheduleId, @EliId, WorkDate, 1, @Now, @ManagerId,
        N'["Escala de referência baseada no padrão operacional de agosto de 2026."]'
    FROM Dates
    WHERE DATEPART(weekday, WorkDate) <> 7
    OPTION (MAXRECURSION 31);

    INSERT schedule.AuditLogs
        (Id, OrganizationId, UserId, Action, EntityType, EntityId, ChangedFields, CorrelationId, IpAddress, CreatedAt)
    VALUES
        (NEWID(), @OrganizationId, @ManagerId, N'ReferenceScheduleSeeded', N'Schedule', @ScheduleId,
         N'{"fields":["year","month","assignments","status"]}', N'reference-schedule-seed', NULL, @Now);
END;

COMMIT TRANSACTION;

SELECT scheduleRecord.Id, scheduleRecord.Year, scheduleRecord.Month, scheduleRecord.Status,
       COUNT(assignment.Id) AS AssignmentCount
FROM schedule.Schedules scheduleRecord
LEFT JOIN schedule.ScheduleAssignments assignment ON assignment.ScheduleId = scheduleRecord.Id
WHERE scheduleRecord.OrganizationId = @OrganizationId
  AND scheduleRecord.Year = 2026
  AND scheduleRecord.Month = 7
GROUP BY scheduleRecord.Id, scheduleRecord.Year, scheduleRecord.Month, scheduleRecord.Status;
