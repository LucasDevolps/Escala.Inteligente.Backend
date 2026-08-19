using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Infrastructure.Persistence;

public sealed class ScheduleManagerDbContext(
    DbContextOptions<ScheduleManagerDbContext> options,
    ICurrentRequest currentRequest) : DbContext(options), IApplicationDbContext
{
    public DbSet<Organization> OrganizationSet => Set<Organization>();
    public DbSet<UserAccount> UserSet => Set<UserAccount>();
    public DbSet<Employee> EmployeeSet => Set<Employee>();
    public DbSet<ActivationToken> ActivationTokenSet => Set<ActivationToken>();
    public DbSet<UserSession> UserSessionSet => Set<UserSession>();
    public DbSet<RefreshTokenRecord> RefreshTokenSet => Set<RefreshTokenRecord>();
    public DbSet<OrganizationScheduleSettings> ScheduleSettingsSet => Set<OrganizationScheduleSettings>();
    public DbSet<Schedule> ScheduleSet => Set<Schedule>();
    public DbSet<ScheduleAssignment> ScheduleAssignmentSet => Set<ScheduleAssignment>();
    public DbSet<ScheduleWarning> ScheduleWarningSet => Set<ScheduleWarning>();
    public DbSet<TimeOffRequest> TimeOffRequestSet => Set<TimeOffRequest>();
    public DbSet<ShiftSwapRequest> ShiftSwapSet => Set<ShiftSwapRequest>();
    public DbSet<Notification> NotificationSet => Set<Notification>();
    public DbSet<OutboxMessage> OutboxMessageSet => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessageSet => Set<InboxMessage>();
    public DbSet<ApplicationError> ApplicationErrorSet => Set<ApplicationError>();
    public DbSet<AuditLog> AuditLogSet => Set<AuditLog>();

    IQueryable<Organization> IApplicationDbContext.Organizations => OrganizationSet;
    IQueryable<UserAccount> IApplicationDbContext.Users => UserSet;
    IQueryable<Employee> IApplicationDbContext.Employees => EmployeeSet;
    IQueryable<ActivationToken> IApplicationDbContext.ActivationTokens => ActivationTokenSet;
    IQueryable<UserSession> IApplicationDbContext.UserSessions => UserSessionSet;
    IQueryable<RefreshTokenRecord> IApplicationDbContext.RefreshTokens => RefreshTokenSet;
    IQueryable<OrganizationScheduleSettings> IApplicationDbContext.ScheduleSettings => ScheduleSettingsSet;
    IQueryable<Schedule> IApplicationDbContext.Schedules => ScheduleSet;
    IQueryable<ScheduleAssignment> IApplicationDbContext.ScheduleAssignments => ScheduleAssignmentSet;
    IQueryable<ScheduleWarning> IApplicationDbContext.ScheduleWarnings => ScheduleWarningSet;
    IQueryable<TimeOffRequest> IApplicationDbContext.TimeOffRequests => TimeOffRequestSet;
    IQueryable<ShiftSwapRequest> IApplicationDbContext.ShiftSwaps => ShiftSwapSet;
    IQueryable<Notification> IApplicationDbContext.Notifications => NotificationSet;
    IQueryable<OutboxMessage> IApplicationDbContext.OutboxMessages => OutboxMessageSet;
    IQueryable<InboxMessage> IApplicationDbContext.InboxMessages => InboxMessageSet;
    IQueryable<ApplicationError> IApplicationDbContext.ApplicationErrors => ApplicationErrorSet;
    IQueryable<AuditLog> IApplicationDbContext.AuditLogs => AuditLogSet;

    private bool TenantFilterEnabled => currentRequest.OrganizationId.HasValue;
    private Guid TenantOrganizationId => currentRequest.OrganizationId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("schedule");
        ConfigureIdentity(modelBuilder);
        ConfigureScheduling(modelBuilder);
        ConfigureOperations(modelBuilder);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try { return await base.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new OptimisticConcurrencyException(exception); }
        catch (DbUpdateException exception) when (TryResolveUniqueConflict(exception, out var conflictKey))
        {
            throw new PersistenceConflictException(conflictKey, exception);
        }
    }

    public new void Add<TEntity>(TEntity entity) where TEntity : class => Set<TEntity>().Add(entity);
    public void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class => Set<TEntity>().AddRange(entities);
    public new void Remove<TEntity>(TEntity entity) where TEntity : class => Set<TEntity>().Remove(entity);
    public void RemoveRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class => Set<TEntity>().RemoveRange(entities);

    public void SetOriginalRowVersion<TEntity>(TEntity entity, byte[] rowVersion) where TEntity : class =>
        Entry(entity).Property("RowVersion").OriginalValue = rowVersion;

    public void ClearTrackedChanges() => ChangeTracker.Clear();

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            await operation(cancellationToken);
            return;
        }

        await using var transaction = await Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            try { await transaction.RollbackAsync(cancellationToken); }
            catch (Exception rollbackException) when (IsSerializationFailure(exception) && rollbackException is not OperationCanceledException)
            {
                // SQL Server may have already rolled a deadlock victim back.
            }
            throw new PersistenceSerializationException(exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException { Number: 1205 or 3960 }) return true;
            if (current.InnerException is null) break;
        }
        return false;
    }

    private static bool TryResolveUniqueConflict(DbUpdateException exception, out string conflictKey)
    {
        conflictKey = string.Empty;
        var current = exception.InnerException;
        while (current is not null && current is not SqlException) current = current.InnerException;
        if (current is not SqlException { Number: 2601 or 2627 } sqlException) return false;

        var message = sqlException.Message;
        conflictKey = message switch
        {
            _ when message.Contains("IX_Users_NormalizedEmail", StringComparison.OrdinalIgnoreCase) => "EMAIL",
            _ when message.Contains("IX_Employees_OrganizationId_EmployeeNumber", StringComparison.OrdinalIgnoreCase) => "EMPLOYEE_NUMBER",
            _ when message.Contains("IX_Schedules_OrganizationId_Year_Month", StringComparison.OrdinalIgnoreCase) => "SCHEDULE_PERIOD",
            _ when message.Contains("IX_TimeOffRequests_OrganizationId_EmployeeId_Date", StringComparison.OrdinalIgnoreCase) => "TIME_OFF_DATE",
            _ when message.Contains("IX_ShiftSwapRequests_OrganizationId_RequesterEmployeeId_Date", StringComparison.OrdinalIgnoreCase) => "SHIFT_SWAP_DATE",
            _ when message.Contains("IX_ScheduleAssignments_ScheduleId_EmployeeId_WorkDate", StringComparison.OrdinalIgnoreCase) => "SCHEDULE_ASSIGNMENT",
            _ when message.Contains("PK_InboxMessages", StringComparison.OrdinalIgnoreCase) => "INBOX_MESSAGE",
            _ when message.Contains("IX_ActivationTokens_TokenHash", StringComparison.OrdinalIgnoreCase) => "ACTIVATION_TOKEN",
            _ when message.Contains("IX_RefreshTokens_TokenHash", StringComparison.OrdinalIgnoreCase) => "REFRESH_TOKEN",
            _ when message.Contains("IX_UserSessions_RefreshTokenHash", StringComparison.OrdinalIgnoreCase) => "SESSION_TOKEN",
            _ when message.Contains("UX_UserSessions_ActiveUser", StringComparison.OrdinalIgnoreCase) => "ACTIVE_USER_SESSION",
            _ => "UNIQUE_CONSTRAINT"
        };
        return true;
    }

    private void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("Organizations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.UpdatedAt).HasPrecision(3);
            entity.Property(x => x.FailedLoginWindowStartedAt).HasPrecision(3);
            entity.Property(x => x.LockoutUntil).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.NormalizedEmail }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Role, x.IsActive });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.UpdatedAt).HasPrecision(3);
            entity.Property(x => x.DeletedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.EmployeeNumber }).IsUnique();
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithOne().HasForeignKey<Employee>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<ActivationToken>(entity =>
        {
            entity.ToTable("ActivationTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(32).IsFixedLength().IsRequired();
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.ExpiresAt).HasPrecision(3);
            entity.Property(x => x.UsedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.ToTable("UserSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RefreshTokenHash).HasMaxLength(32).IsFixedLength().IsRequired();
            entity.Property(x => x.RevocationReason).HasMaxLength(100);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(500);
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.LastRefreshAt).HasPrecision(3);
            entity.Property(x => x.ExpiresAt).HasPrecision(3);
            entity.Property(x => x.RevokedAt).HasPrecision(3);
            entity.HasIndex(x => x.RefreshTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt });
            entity.HasIndex(x => x.UserId)
                .HasDatabaseName("UX_UserSessions_ActiveUser")
                .HasFilter("[RevokedAt] IS NULL")
                .IsUnique();
            entity.HasIndex(x => x.TokenFamilyId);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<RefreshTokenRecord>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(32).IsFixedLength().IsRequired();
            entity.Property(x => x.RevocationReason).HasMaxLength(100);
            entity.Property(x => x.IssuedAt).HasPrecision(3);
            entity.Property(x => x.ExpiresAt).HasPrecision(3);
            entity.Property(x => x.UsedAt).HasPrecision(3);
            entity.Property(x => x.RevokedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.TokenFamilyId);
            entity.HasOne<UserSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });
    }

    private void ConfigureScheduling(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrganizationScheduleSettings>(entity =>
        {
            entity.ToTable("OrganizationScheduleSettings", table =>
            {
                table.HasCheckConstraint("CK_ScheduleSettings_MinEmployees", "[MinEmployeesPerDay] >= 1");
                table.HasCheckConstraint("CK_ScheduleSettings_MaxEmployees", "[MaxEmployeesPerDay] >= [MinEmployeesPerDay]");
                table.HasCheckConstraint("CK_ScheduleSettings_Consecutive", "[MaxConsecutiveWorkDays] >= 1");
                table.HasCheckConstraint("CK_ScheduleSettings_DaysOff", "[MinDaysOffPerMonth] >= 0");
                table.HasCheckConstraint("CK_ScheduleSettings_ProductivityWeight", "[ProductivityWeight] BETWEEN 0 AND 20");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.HasOne<Organization>().WithOne().HasForeignKey<OrganizationScheduleSettings>(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<Schedule>(entity =>
        {
            entity.ToTable("Schedules", table =>
            {
                table.HasCheckConstraint("CK_Schedules_Month", "[Month] BETWEEN 1 AND 12");
                table.HasCheckConstraint("CK_Schedules_Year", "[Year] BETWEEN 2000 AND 2200");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.PublishedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.Year, x.Month }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PublishedBy).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<ScheduleAssignment>(entity =>
        {
            entity.ToTable("ScheduleAssignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WorkDate).HasColumnType("date");
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.ExplanationJson).HasMaxLength(4000).IsRequired();
            entity.HasIndex(x => new { x.ScheduleId, x.EmployeeId, x.WorkDate }).IsUnique();
            entity.HasIndex(x => new { x.ScheduleId, x.WorkDate });
            entity.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScheduleWarning>(entity =>
        {
            entity.ToTable("ScheduleWarnings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Date).HasColumnType("date");
            entity.Property(x => x.Code).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(500).IsRequired();
            entity.HasIndex(x => new { x.ScheduleId, x.Date });
            entity.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimeOffRequest>(entity =>
        {
            entity.ToTable("TimeOffRequests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Date).HasColumnType("date");
            entity.Property(x => x.ReasonDescription).HasMaxLength(500);
            entity.Property(x => x.RejectionReason).HasMaxLength(500);
            entity.Property(x => x.RequestedAt).HasPrecision(3);
            entity.Property(x => x.ReviewedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Date })
                .HasFilter("[Status] IN (0, 1)").IsUnique();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<ShiftSwapRequest>(entity =>
        {
            entity.ToTable("ShiftSwapRequests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Date).HasColumnType("date");
            entity.Property(x => x.RequestedAt).HasPrecision(3);
            entity.Property(x => x.RespondedAt).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.RequesterEmployeeId, x.Date })
                .HasFilter("[Status] = 0").IsUnique();
            entity.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.RequesterEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.TargetEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });
    }

    private void ConfigureOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.KeyId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Nonce).HasMaxLength(12).IsFixedLength().IsRequired();
            entity.Property(x => x.AuthenticationTag).HasMaxLength(16).IsFixedLength().IsRequired();
            entity.Property(x => x.Ciphertext).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.Property(x => x.ReadAt).HasPrecision(3);
            entity.HasIndex(x => new { x.OrganizationId, x.RecipientUserId, x.CreatedAt });
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Payload).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.OccurredAt).HasPrecision(3);
            entity.Property(x => x.ProcessedAt).HasPrecision(3);
            entity.Property(x => x.NextAttemptAt).HasPrecision(3);
            entity.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt, x.OccurredAt });
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages");
            entity.HasKey(x => new { x.MessageId, x.ConsumerName });
            entity.Property(x => x.ConsumerName).HasMaxLength(200);
            entity.Property(x => x.ProcessedAt).HasPrecision(3);
        });

        modelBuilder.Entity<ApplicationError>(entity =>
        {
            entity.ToTable("ApplicationErrors");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExceptionType).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SanitizedMessage).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.SanitizedStackTrace).HasMaxLength(8000);
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TraceId).HasMaxLength(100);
            entity.Property(x => x.Endpoint).HasMaxLength(500);
            entity.Property(x => x.HttpMethod).HasMaxLength(20);
            entity.Property(x => x.Environment).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Timestamp).HasPrecision(3);
            entity.HasIndex(x => x.Timestamp);
            entity.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(150).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(150).IsRequired();
            entity.Property(x => x.ChangedFields).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.CreatedAt).HasPrecision(3);
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => !TenantFilterEnabled || x.OrganizationId == TenantOrganizationId);
        });
    }
}
