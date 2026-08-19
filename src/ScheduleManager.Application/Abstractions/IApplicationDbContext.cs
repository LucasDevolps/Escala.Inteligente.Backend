using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Application.Abstractions;

public interface IApplicationDbContext
{
    IQueryable<Organization> Organizations { get; }
    IQueryable<UserAccount> Users { get; }
    IQueryable<Employee> Employees { get; }
    IQueryable<ActivationToken> ActivationTokens { get; }
    IQueryable<UserSession> UserSessions { get; }
    IQueryable<RefreshTokenRecord> RefreshTokens { get; }
    IQueryable<OrganizationScheduleSettings> ScheduleSettings { get; }
    IQueryable<Schedule> Schedules { get; }
    IQueryable<ScheduleAssignment> ScheduleAssignments { get; }
    IQueryable<ScheduleWarning> ScheduleWarnings { get; }
    IQueryable<TimeOffRequest> TimeOffRequests { get; }
    IQueryable<ShiftSwapRequest> ShiftSwaps { get; }
    IQueryable<Notification> Notifications { get; }
    IQueryable<OutboxMessage> OutboxMessages { get; }
    IQueryable<InboxMessage> InboxMessages { get; }
    IQueryable<ApplicationError> ApplicationErrors { get; }
    IQueryable<AuditLog> AuditLogs { get; }

    void Add<TEntity>(TEntity entity) where TEntity : class;
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    void RemoveRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void SetOriginalRowVersion<TEntity>(TEntity entity, byte[] rowVersion) where TEntity : class;
    void ClearTrackedChanges();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly Today(string timeZoneId);
}

public interface ICurrentRequest
{
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
    Guid? SessionId { get; }
    string? Role { get; }
    string CorrelationId { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string hash, string password);
    void PerformDummyVerification(string password);
}

public interface ITokenService
{
    string CreateAccessToken(UserAccount user, UserSession session, DateTimeOffset now);
    string GenerateOpaqueToken(int sizeInBytes = 32);
}

public interface ITokenHasher
{
    byte[] Hash(string token);
}

public sealed record EncryptedPayload(string KeyId, byte[] Nonce, byte[] Ciphertext, byte[] AuthenticationTag);
public sealed record EncryptionKey(string KeyId, byte[] KeyBytes);

public interface IEncryptionKeyProvider
{
    EncryptionKey GetCurrentKey();
    EncryptionKey GetKey(string keyId);
}

public interface INotificationCipher
{
    EncryptedPayload Encrypt(string plaintext, ReadOnlySpan<byte> associatedData);
    string Decrypt(EncryptedPayload payload, ReadOnlySpan<byte> associatedData);
}

public interface IRealtimeNotifier
{
    Task SessionRevokedAsync(Guid userId, Guid sessionId, string reason, CancellationToken cancellationToken);
    Task NotificationCreatedAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken);
}

public interface IMessagePublisher : IAsyncDisposable
{
    Task PublishAsync(string queue, Guid messageId, string type, string payload, string correlationId, CancellationToken cancellationToken);
}

public sealed class OptimisticConcurrencyException : Exception
{
    public OptimisticConcurrencyException(Exception innerException)
        : base("A entidade foi alterada por outra operação.", innerException) { }
}

/// <summary>
/// Represents a database-enforced uniqueness conflict. The conflict key is a
/// stable internal value so application services can translate races into the
/// same public error contract used by their pre-flight validation.
/// </summary>
public sealed class PersistenceConflictException : Exception
{
    public PersistenceConflictException(string conflictKey, Exception innerException)
        : base("Uma restrição de unicidade foi violada.", innerException) => ConflictKey = conflictKey;

    public string ConflictKey { get; }
}

public sealed class PersistenceSerializationException : Exception
{
    public PersistenceSerializationException(Exception innerException)
        : base("A transação serializável foi interrompida por concorrência.", innerException) { }
}
