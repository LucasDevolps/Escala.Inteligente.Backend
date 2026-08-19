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

        var options = bootstrapOptions.Value;
        if (!options.Enabled) return;
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
