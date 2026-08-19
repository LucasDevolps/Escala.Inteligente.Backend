using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "schedule");

            migrationBuilder.CreateTable(
                name: "ApplicationErrors",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SanitizedMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SanitizedStackTrace = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TraceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationErrors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "schedule",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.ConsumerName });
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationScheduleSettings",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinEmployeesPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxEmployeesPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxConsecutiveWorkDays = table.Column<int>(type: "int", nullable: false),
                    MinDaysOffPerMonth = table.Column<int>(type: "int", nullable: false),
                    MaxWorkDaysPerMonth = table.Column<int>(type: "int", nullable: true),
                    BalanceWeekends = table.Column<bool>(type: "bit", nullable: false),
                    ProductivityWeight = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationScheduleSettings", x => x.Id);
                    table.CheckConstraint("CK_ScheduleSettings_Consecutive", "[MaxConsecutiveWorkDays] >= 1");
                    table.CheckConstraint("CK_ScheduleSettings_DaysOff", "[MinDaysOffPerMonth] >= 0");
                    table.CheckConstraint("CK_ScheduleSettings_MaxEmployees", "[MaxEmployeesPerDay] >= [MinEmployeesPerDay]");
                    table.CheckConstraint("CK_ScheduleSettings_MinEmployees", "[MinEmployeesPerDay] >= 1");
                    table.CheckConstraint("CK_ScheduleSettings_ProductivityWeight", "[ProductivityWeight] BETWEEN 0 AND 20");
                    table.ForeignKey(
                        name: "FK_OrganizationScheduleSettings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "schedule",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginAttempts = table.Column<int>(type: "int", nullable: false),
                    FailedLoginWindowStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    LockoutUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "schedule",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivationTokens",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivationTokens_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedFields = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "schedule",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductivityLevel = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employees_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "schedule",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employees_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", fixedLength: true, maxLength: 12, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                    table.CheckConstraint("CK_Schedules_Month", "[Month] BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_Schedules_Year", "[Year] BETWEEN 2000 AND 2200");
                    table.ForeignKey(
                        name: "FK_Schedules_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "schedule",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Schedules_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Schedules_Users_PublishedBy",
                        column: x => x.PublishedBy,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RefreshTokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    TokenFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    LastRefreshAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimeOffRequests",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ReasonCategory = table.Column<int>(type: "int", nullable: false),
                    ReasonDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeOffRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeOffRequests_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "schedule",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeOffRequests_Users_ReviewedBy",
                        column: x => x.ReviewedBy,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleAssignments",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExplanationJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "schedule",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAssignments_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "schedule",
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduleAssignments_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "schedule",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleWarnings",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleWarnings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleWarnings_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "schedule",
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShiftSwapRequests",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftSwapRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShiftSwapRequests_Employees_RequesterEmployeeId",
                        column: x => x.RequesterEmployeeId,
                        principalSchema: "schedule",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftSwapRequests_Employees_TargetEmployeeId",
                        column: x => x.TargetEmployeeId,
                        principalSchema: "schedule",
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftSwapRequests_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "schedule",
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                schema: "schedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", precision: 3, nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "schedule",
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationTokens_TokenHash",
                schema: "schedule",
                table: "ActivationTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationTokens_UserId",
                schema: "schedule",
                table: "ActivationTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationErrors_CorrelationId",
                schema: "schedule",
                table: "ApplicationErrors",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationErrors_Timestamp",
                schema: "schedule",
                table: "ApplicationErrors",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OrganizationId_CreatedAt",
                schema: "schedule",
                table: "AuditLogs",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                schema: "schedule",
                table: "AuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_OrganizationId_EmployeeNumber",
                schema: "schedule",
                table: "Employees",
                columns: new[] { "OrganizationId", "EmployeeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_UserId",
                schema: "schedule",
                table: "Employees",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_OrganizationId_RecipientUserId_CreatedAt",
                schema: "schedule",
                table: "Notifications",
                columns: new[] { "OrganizationId", "RecipientUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId",
                schema: "schedule",
                table: "Notifications",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationScheduleSettings_OrganizationId",
                schema: "schedule",
                table: "OrganizationScheduleSettings",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_NextAttemptAt_OccurredAt",
                schema: "schedule",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "NextAttemptAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId",
                schema: "schedule",
                table: "RefreshTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenFamilyId",
                schema: "schedule",
                table: "RefreshTokens",
                column: "TokenFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                schema: "schedule",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAssignments_CreatedBy",
                schema: "schedule",
                table: "ScheduleAssignments",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAssignments_EmployeeId",
                schema: "schedule",
                table: "ScheduleAssignments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAssignments_ScheduleId_EmployeeId_WorkDate",
                schema: "schedule",
                table: "ScheduleAssignments",
                columns: new[] { "ScheduleId", "EmployeeId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAssignments_ScheduleId_WorkDate",
                schema: "schedule",
                table: "ScheduleAssignments",
                columns: new[] { "ScheduleId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_CreatedBy",
                schema: "schedule",
                table: "Schedules",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_OrganizationId_Year_Month",
                schema: "schedule",
                table: "Schedules",
                columns: new[] { "OrganizationId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_PublishedBy",
                schema: "schedule",
                table: "Schedules",
                column: "PublishedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleWarnings_ScheduleId_Date",
                schema: "schedule",
                table: "ScheduleWarnings",
                columns: new[] { "ScheduleId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftSwapRequests_OrganizationId_RequesterEmployeeId_Date",
                schema: "schedule",
                table: "ShiftSwapRequests",
                columns: new[] { "OrganizationId", "RequesterEmployeeId", "Date" },
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftSwapRequests_RequesterEmployeeId",
                schema: "schedule",
                table: "ShiftSwapRequests",
                column: "RequesterEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftSwapRequests_ScheduleId",
                schema: "schedule",
                table: "ShiftSwapRequests",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftSwapRequests_TargetEmployeeId",
                schema: "schedule",
                table: "ShiftSwapRequests",
                column: "TargetEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_EmployeeId",
                schema: "schedule",
                table: "TimeOffRequests",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_OrganizationId_EmployeeId_Date",
                schema: "schedule",
                table: "TimeOffRequests",
                columns: new[] { "OrganizationId", "EmployeeId", "Date" },
                unique: true,
                filter: "[Status] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_TimeOffRequests_ReviewedBy",
                schema: "schedule",
                table: "TimeOffRequests",
                column: "ReviewedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                schema: "schedule",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrganizationId_NormalizedEmail",
                schema: "schedule",
                table: "Users",
                columns: new[] { "OrganizationId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrganizationId_Role_IsActive",
                schema: "schedule",
                table: "Users",
                columns: new[] { "OrganizationId", "Role", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                schema: "schedule",
                table: "UserSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_TokenFamilyId",
                schema: "schedule",
                table: "UserSessions",
                column: "TokenFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId_RevokedAt",
                schema: "schedule",
                table: "UserSessions",
                columns: new[] { "UserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_UserSessions_ActiveUser",
                schema: "schedule",
                table: "UserSessions",
                column: "UserId",
                unique: true,
                filter: "[RevokedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivationTokens",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "ApplicationErrors",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "OrganizationScheduleSettings",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "RefreshTokens",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "ScheduleAssignments",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "ScheduleWarnings",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "ShiftSwapRequests",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "TimeOffRequests",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "UserSessions",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "Schedules",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "schedule");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "schedule");
        }
    }
}
