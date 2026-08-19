using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Domain.Entities;

public sealed class Organization : Entity
{
    private Organization() { }

    public Organization(string name, string timeZoneId, DateTimeOffset now) : base(DomainIds.New())
    {
        Name = name;
        TimeZoneId = timeZoneId;
        IsActive = true;
        CreatedAt = now;
    }

    public string Name { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
}

public sealed class UserAccount : Entity, ITenantEntity
{
    private UserAccount() { }

    public UserAccount(
        Guid organizationId,
        string name,
        string normalizedEmail,
        string phone,
        UserRole role,
        string passwordHash,
        bool mustChangePassword,
        DateTimeOffset now) : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        Name = name;
        NormalizedEmail = normalizedEmail;
        Email = normalizedEmail.ToLowerInvariant();
        Phone = phone;
        Role = role;
        PasswordHash = passwordHash;
        IsActive = true;
        MustChangePassword = mustChangePassword;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public string PasswordHash { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public bool MustChangePassword { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? FailedLoginWindowStartedAt { get; private set; }
    public DateTimeOffset? LockoutUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsLocked(DateTimeOffset now) => LockoutUntil is not null && LockoutUntil > now;

    public void RegisterFailedLogin(DateTimeOffset now)
    {
        if (FailedLoginWindowStartedAt is null || now - FailedLoginWindowStartedAt > TimeSpan.FromMinutes(15))
        {
            FailedLoginWindowStartedAt = now;
            FailedLoginAttempts = 0;
        }

        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
        {
            LockoutUntil = now.AddMinutes(15);
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginAttempts = 0;
        FailedLoginWindowStartedAt = null;
        LockoutUntil = null;
        UpdatedAt = now;
    }

    public void Activate(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        MustChangePassword = false;
        UpdatedAt = now;
    }

    public void UpdateProfile(string name, string email, string normalizedEmail, string phone, DateTimeOffset now)
    {
        Name = name;
        Email = email;
        NormalizedEmail = normalizedEmail;
        Phone = phone;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}

public sealed class Employee : Entity, ITenantEntity
{
    private Employee() { }

    public Employee(Guid organizationId, Guid userId, string employeeNumber, ProductivityLevel productivityLevel, DateTimeOffset now)
        : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        UserId = userId;
        EmployeeNumber = employeeNumber;
        ProductivityLevel = productivityLevel;
        IsActive = true;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public string EmployeeNumber { get; private set; } = string.Empty;
    public ProductivityLevel ProductivityLevel { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string employeeNumber, ProductivityLevel productivityLevel, DateTimeOffset now)
    {
        EmployeeNumber = employeeNumber;
        ProductivityLevel = productivityLevel;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        DeletedAt = now;
        UpdatedAt = now;
    }
}

public sealed class ActivationToken : Entity, ITenantEntity
{
    private ActivationToken() { }

    public ActivationToken(Guid organizationId, Guid userId, byte[] tokenHash, DateTimeOffset now)
        : base(DomainIds.New())
    {
        OrganizationId = organizationId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now.AddHours(24);
    }

    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now <= ExpiresAt;
    public void MarkUsed(DateTimeOffset now) => UsedAt = now;
}

public sealed class UserSession : Entity, ITenantEntity
{
    private UserSession() { }

    public UserSession(
        Guid userId,
        Guid organizationId,
        byte[] refreshTokenHash,
        Guid tokenFamilyId,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        string? ipAddress,
        string? userAgent) : base(DomainIds.New())
    {
        UserId = userId;
        OrganizationId = organizationId;
        RefreshTokenHash = refreshTokenHash;
        TokenFamilyId = tokenFamilyId;
        CreatedAt = now;
        LastRefreshAt = now;
        ExpiresAt = expiresAt;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public byte[] RefreshTokenHash { get; private set; } = [];
    public Guid TokenFamilyId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastRefreshAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public bool CanRefresh(DateTimeOffset now) => IsActive(now) && now - LastRefreshAt <= TimeSpan.FromMinutes(5);

    public void Rotate(byte[] nextHash, DateTimeOffset now)
    {
        if (!CanRefresh(now))
        {
            throw new DomainRuleException("SESSION_EXPIRED", "A sessão não pode mais ser renovada.");
        }

        RefreshTokenHash = nextHash;
        LastRefreshAt = now;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        RevocationReason = reason;
    }
}

public sealed class RefreshTokenRecord : Entity, ITenantEntity
{
    private RefreshTokenRecord() { }

    public RefreshTokenRecord(
        Guid sessionId,
        Guid userId,
        Guid organizationId,
        Guid tokenFamilyId,
        byte[] tokenHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt) : base(DomainIds.New())
    {
        SessionId = sessionId;
        UserId = userId;
        OrganizationId = organizationId;
        TokenFamilyId = tokenFamilyId;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public Guid SessionId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid TokenFamilyId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool WasConsumed => UsedAt is not null || RevokedAt is not null;

    public void MarkRotated(DateTimeOffset now)
    {
        UsedAt = now;
        RevokedAt = now;
        RevocationReason = "ROTATED";
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        RevokedAt ??= now;
        RevocationReason ??= reason;
    }
}
