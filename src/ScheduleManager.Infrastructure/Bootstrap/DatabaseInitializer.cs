using System.Text.RegularExpressions;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;
using ScheduleManager.Infrastructure.Persistence;

namespace ScheduleManager.Infrastructure.Bootstrap;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool MigrateOnStartup { get; init; }
}

public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";
    public bool Enabled { get; init; }
    public string OrganizationName { get; init; } = "";
    public string TimeZoneId { get; init; } = "";
    public string ManagerName { get; init; } = "";
    public string ManagerEmail { get; init; } = "";
    public string ManagerPhone { get; init; } = "";
    public string ManagerPassword { get; init; } = "";
}

public sealed class ReferenceScheduleSeedOptions
{
    public const string SectionName = "ReferenceScheduleSeed";
    public bool Enabled { get; init; }
    public int Year { get; init; } = 2026;
    public int Month { get; init; } = 7;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    public int NotificationsDays { get; init; } = 180;
    public int ApplicationErrorsDays { get; init; } = 180;
    public int RevokedSessionsDays { get; init; } = 90;
}

public sealed partial class DatabaseInitializer(
    ScheduleManagerDbContext db,
    IPasswordService passwords,
    IClock clock,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<BootstrapOptions> bootstrapOptions,
    IOptions<ReferenceScheduleSeedOptions> referenceScheduleSeedOptions,
    ILogger<DatabaseInitializer> logger)
{
    [GeneratedRegex("[^0-9+]", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCharacters();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (databaseOptions.Value.MigrateOnStartup)
        {
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Database migrations applied successfully");
        }

        if (bootstrapOptions.Value.Enabled)
        {
            await BootstrapAsync(bootstrapOptions.Value, cancellationToken);
        }

        await SeedReferenceScheduleAsync(cancellationToken);
    }

    private async Task BootstrapAsync(BootstrapOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        var normalizedEmail = options.ManagerEmail.Trim().ToUpperInvariant();
        if (await db.UserSet.IgnoreQueryFilters().AnyAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            logger.LogInformation("Bootstrap manager already exists; seed skipped");
            return;
        }

        _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var now = clock.UtcNow;
        var organization = new Organization(options.OrganizationName.Trim(), options.TimeZoneId.Trim(), now);
        var manager = new UserAccount(
            organization.Id,
            options.ManagerName.Trim(),
            normalizedEmail,
            PhoneCharacters().Replace(options.ManagerPhone.Trim(), string.Empty),
            UserRole.Manager,
            passwords.Hash(options.ManagerPassword),
            false,
            now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Add(organization);
        db.Add(manager);
        db.Add(new OrganizationScheduleSettings(organization.Id));
        db.Add(new AuditLog(organization.Id, manager.Id, "BootstrapCompleted", "Organization", organization.Id,
            "{\"fields\":[\"organization\",\"manager\",\"scheduleSettings\"]}", "bootstrap", null, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bootstrap completed for organization {OrganizationId} and manager {ManagerId}", organization.Id, manager.Id);
    }

    private async Task SeedReferenceScheduleAsync(CancellationToken cancellationToken)
    {
        var options = referenceScheduleSeedOptions.Value;
        if (!options.Enabled) return;
        if (options.Year is < 2000 or > 2200 || options.Month is < 1 or > 12)
            throw new InvalidOperationException("ReferenceScheduleSeed possui ano ou mês inválido.");

        var employees = await (
            from employee in db.EmployeeSet.IgnoreQueryFilters()
            join user in db.UserSet.IgnoreQueryFilters() on employee.UserId equals user.Id
            where employee.IsActive && (user.Name == "Miriam" || user.Name == "Eli")
            select new { Employee = employee, UserName = user.Name })
            .ToListAsync(cancellationToken);

        var pairs = employees
            .GroupBy(x => x.Employee.OrganizationId)
            .Select(group => new
            {
                OrganizationId = group.Key,
                Miriams = group.Where(x => x.UserName == "Miriam").Select(x => x.Employee).DistinctBy(x => x.Id).ToArray(),
                Elis = group.Where(x => x.UserName == "Eli").Select(x => x.Employee).DistinctBy(x => x.Id).ToArray()
            })
            .Where(x => x.Miriams.Length == 1 && x.Elis.Length == 1)
            .ToList();

        foreach (var pair in pairs)
        {
            if (await db.ScheduleSet.IgnoreQueryFilters().AnyAsync(
                    x => x.OrganizationId == pair.OrganizationId && x.Year == options.Year && x.Month == options.Month,
                    cancellationToken))
            {
                logger.LogInformation(
                    "Reference schedule {Year}/{Month} already exists for organization {OrganizationId}; seed skipped",
                    options.Year,
                    options.Month,
                    pair.OrganizationId);
                continue;
            }

            var manager = await db.UserSet.IgnoreQueryFilters()
                .Where(x => x.OrganizationId == pair.OrganizationId && x.Role == UserRole.Manager && x.IsActive)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (manager is null)
            {
                logger.LogWarning(
                    "Reference schedule seed skipped because organization {OrganizationId} has no active manager",
                    pair.OrganizationId);
                continue;
            }

            var now = clock.UtcNow;
            var schedule = new Schedule(pair.OrganizationId, options.Year, options.Month, manager.Id, now);
            db.Add(schedule);
            var start = new DateOnly(options.Year, options.Month, 1);
            var end = start.AddMonths(1);
            for (var date = start; date < end; date = date.AddDays(1))
            {
                if (date.DayOfWeek != DayOfWeek.Sunday)
                    db.Add(new ScheduleAssignment(schedule.Id, pair.Miriams[0].Id, date, AssignmentSource.Manual, manager.Id, now,
                        "[\"Escala de referência baseada no padrão operacional de agosto de 2026.\"]"));
                if (date.DayOfWeek != DayOfWeek.Saturday)
                    db.Add(new ScheduleAssignment(schedule.Id, pair.Elis[0].Id, date, AssignmentSource.Manual, manager.Id, now,
                        "[\"Escala de referência baseada no padrão operacional de agosto de 2026.\"]"));
            }

            schedule.MarkEdited();
            schedule.Publish(manager.Id, now);
            db.Add(new AuditLog(pair.OrganizationId, manager.Id, "ReferenceScheduleSeeded", "Schedule", schedule.Id,
                "{\"fields\":[\"year\",\"month\",\"assignments\",\"status\"]}", "reference-schedule-seed", null, now));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Reference schedule {Year}/{Month} seeded for Miriam and Eli in organization {OrganizationId}",
                options.Year,
                options.Month,
                pair.OrganizationId);
        }
    }

    private static void Validate(BootstrapOptions options)
    {
        var phone = PhoneCharacters().Replace(options.ManagerPhone?.Trim() ?? string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(options.OrganizationName) || options.OrganizationName.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(options.TimeZoneId) || options.TimeZoneId.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(options.ManagerName) || options.ManagerName.Trim().Length is < 2 or > 150 ||
            !IsValidEmail(options.ManagerEmail) ||
            phone.Length is < 1 or > 20 ||
            options.ManagerPassword is null || options.ManagerPassword.Length is < 12 or > 128)
            throw new InvalidOperationException("Configuração Bootstrap inválida. Revise os campos obrigatórios e a senha de 12..128 caracteres.");
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 320) return false;
        try { return new MailAddress(email.Trim()).Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }
}
