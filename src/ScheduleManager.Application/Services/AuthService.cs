using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Application.Services;

public sealed class AuthService(
    IApplicationDbContext db,
    IPasswordService passwords,
    ITokenService tokens,
    ITokenHasher tokenHasher,
    IClock clock,
    ICurrentRequest current,
    IRealtimeNotifier realtime) : IAuthService
{
    private static readonly TimeSpan AbsoluteSessionLifetime = TimeSpan.FromDays(30);

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!Validation.IsEmail(request.Email) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
            throw InvalidCredentials();

        var now = clock.UtcNow;
        var normalizedEmail = Validation.NormalizeEmail(request.Email);
        var user = db.Users.SingleOrDefault(x => x.NormalizedEmail == normalizedEmail);
        if (user is null)
        {
            passwords.PerformDummyVerification(request.Password);
            throw InvalidCredentials();
        }

        var passwordIsValid = passwords.Verify(user.PasswordHash, request.Password);
        if (user.IsLocked(now) || !user.IsActive || user.MustChangePassword)
            throw InvalidCredentials();
        if (!passwordIsValid)
        {
            await RegisterFailedLoginAsync(user.Id, request.Password, now, cancellationToken);
            throw InvalidCredentials();
        }

        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            db.ClearTrackedChanges();
            var revokedSessionIds = new List<Guid>();
            UserAccount? authenticatedUser = null;
            UserSession? session = null;
            string? refreshToken = null;
            try
            {
                await db.ExecuteInTransactionAsync(async ct =>
                {
                    var freshUser = db.Users.SingleOrDefault(x => x.Id == user.Id && x.NormalizedEmail == normalizedEmail);
                    if (freshUser is null || freshUser.IsLocked(now) || !freshUser.IsActive || freshUser.MustChangePassword ||
                        !passwords.Verify(freshUser.PasswordHash, request.Password))
                        throw InvalidCredentials();
                    var organization = db.Organizations.SingleOrDefault(x => x.Id == freshUser.OrganizationId);
                    if (organization is null || !organization.IsActive) throw InvalidCredentials();

                    var previousSessions = db.UserSessions.Where(x => x.UserId == freshUser.Id && x.RevokedAt == null).ToList();
                    foreach (var previous in previousSessions)
                    {
                        previous.Revoke(now, "NEW_LOGIN");
                        revokedSessionIds.Add(previous.Id);
                    }
                    foreach (var previous in db.RefreshTokens.Where(x => x.UserId == freshUser.Id && x.RevokedAt == null).ToList())
                        previous.Revoke(now, "NEW_LOGIN");
                    freshUser.RegisterSuccessfulLogin(now);

                    // Persist revocations first inside the same transaction so the filtered
                    // unique index can never observe two active sessions for one user.
                    await db.SaveChangesAsync(ct);

                    refreshToken = tokens.GenerateOpaqueToken();
                    var refreshHash = tokenHasher.Hash(refreshToken);
                    var familyId = DomainIds.New();
                    session = new UserSession(
                        freshUser.Id,
                        freshUser.OrganizationId,
                        refreshHash,
                        familyId,
                        now,
                        now.Add(AbsoluteSessionLifetime),
                        current.IpAddress,
                        Truncate(current.UserAgent, 500));
                    db.Add(session);
                    db.Add(new RefreshTokenRecord(
                        session.Id,
                        freshUser.Id,
                        freshUser.OrganizationId,
                        familyId,
                        refreshHash,
                        now,
                        session.ExpiresAt));
                    db.Add(Audit(freshUser, "Login", "UserSession", session.Id, now));
                    foreach (var previousId in revokedSessionIds)
                        db.Add(Audit(freshUser, "SessionRevoked", "UserSession", previousId, now));
                    await db.SaveChangesAsync(ct);
                    authenticatedUser = freshUser;
                }, cancellationToken);

                foreach (var previousId in revokedSessionIds)
                    await realtime.SessionRevokedAsync(authenticatedUser!.Id, previousId, "NEW_LOGIN", cancellationToken);
                return new LoginResult(CreateAuthResponse(authenticatedUser!, session!, now), refreshToken!, revokedSessionIds);
            }
            catch (Exception exception) when (
                exception is OptimisticConcurrencyException or PersistenceSerializationException ||
                exception is PersistenceConflictException { ConflictKey: "ACTIVE_USER_SESSION" or "SESSION_TOKEN" or "REFRESH_TOKEN" })
            {
                if (attempt == maxAttempts)
                    throw AppException.Conflict("LOGIN_CONFLICT", "Outro login ocorreu simultaneamente; tente novamente.");
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 10), cancellationToken);
            }
        }

        throw AppException.Conflict("LOGIN_CONFLICT", "Outro login ocorreu simultaneamente; tente novamente.");
    }

    public async Task ActivateAsync(ActivateRequest request, CancellationToken cancellationToken)
    {
        Validation.Password(request.NewPassword);
        if (!Validation.IsEmail(request.Email) || string.IsNullOrWhiteSpace(request.ActivationCode))
            throw AppException.Rule("INVALID_ACTIVATION_TOKEN", "Código de ativação inválido ou expirado.");

        var normalizedEmail = Validation.NormalizeEmail(request.Email);
        var user = db.Users.SingleOrDefault(x => x.NormalizedEmail == normalizedEmail);
        if (user is null || !user.IsActive || !user.MustChangePassword)
            throw AppException.Rule("INVALID_ACTIVATION_TOKEN", "Código de ativação inválido ou expirado.");

        var hash = tokenHasher.Hash(request.ActivationCode);
        var activation = db.ActivationTokens
            .SingleOrDefault(x => x.UserId == user.Id && x.TokenHash == hash);
        var now = clock.UtcNow;
        if (activation is null || !activation.IsUsable(now))
            throw AppException.Rule("INVALID_ACTIVATION_TOKEN", "Código de ativação inválido ou expirado.");

        try
        {
            await db.ExecuteInTransactionAsync(async ct =>
            {
                user.Activate(passwords.Hash(request.NewPassword), now);
                activation.MarkUsed(now);
                await db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            throw AppException.Rule("INVALID_ACTIVATION_TOKEN", "Código de ativação inválido ou expirado.");
        }
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw AppException.Unauthorized("INVALID_REFRESH_TOKEN", "Refresh token inválido.");

        var hash = tokenHasher.Hash(refreshToken);
        var now = clock.UtcNow;
        var nextToken = tokens.GenerateOpaqueToken();
        var nextHash = tokenHasher.Hash(nextToken);
        AuthResponse? response = null;
        (string Code, string Message)? failure = null;
        Guid? revokedUserId = null;
        Guid? revokedSessionId = null;
        try
        {
            await db.ExecuteInTransactionAsync(async ct =>
            {
                // The lookup and every validation run under SERIALIZABLE isolation.
                // A token can therefore be consumed by at most one concurrent request.
                db.ClearTrackedChanges();
                var record = db.RefreshTokens.SingleOrDefault(x => x.TokenHash == hash);
                if (record is null)
                {
                    failure = ("INVALID_REFRESH_TOKEN", "Refresh token inválido.");
                    return;
                }

                var session = db.UserSessions.SingleOrDefault(x => x.Id == record.SessionId && x.UserId == record.UserId);
                if (record.WasConsumed)
                {
                    foreach (var token in db.RefreshTokens.Where(x => x.TokenFamilyId == record.TokenFamilyId).ToList())
                        token.Revoke(now, "REFRESH_TOKEN_REUSE");
                    foreach (var familySession in db.UserSessions.Where(x => x.TokenFamilyId == record.TokenFamilyId).ToList())
                        familySession.Revoke(now, "REFRESH_TOKEN_REUSE");
                    var replayUser = db.Users.Single(x => x.Id == record.UserId && x.OrganizationId == record.OrganizationId);
                    db.Add(Audit(replayUser, "SessionRevoked", "UserSession", record.SessionId, now));
                    await db.SaveChangesAsync(ct);
                    failure = ("REFRESH_TOKEN_REUSE", "Reutilização de refresh token detectada; a sessão foi revogada.");
                    revokedUserId = record.UserId;
                    revokedSessionId = record.SessionId;
                    return;
                }

                if (session is null || session.RevokedAt is not null)
                {
                    failure = ("SESSION_REVOKED", "A sessão foi revogada.");
                    return;
                }

                if (!session.CanRefresh(now))
                {
                    session.Revoke(now, "SESSION_EXPIRED");
                    foreach (var token in db.RefreshTokens.Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToList())
                        token.Revoke(now, "SESSION_EXPIRED");
                    await db.SaveChangesAsync(ct);
                    failure = ("SESSION_EXPIRED", "A sessão expirou por inatividade.");
                    return;
                }

                var user = db.Users.SingleOrDefault(x => x.Id == session.UserId && x.OrganizationId == session.OrganizationId);
                var organization = db.Organizations.SingleOrDefault(x => x.Id == session.OrganizationId);
                if (user is null || !user.IsActive || organization is null || !organization.IsActive)
                {
                    session.Revoke(now, "ACCOUNT_INACTIVE");
                    foreach (var token in db.RefreshTokens.Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToList())
                        token.Revoke(now, "ACCOUNT_INACTIVE");
                    await db.SaveChangesAsync(ct);
                    failure = ("SESSION_REVOKED", "A sessão foi revogada.");
                    return;
                }

                record.MarkRotated(now);
                session.Rotate(nextHash, now);
                db.Add(new RefreshTokenRecord(
                    session.Id,
                    user.Id,
                    user.OrganizationId,
                    session.TokenFamilyId,
                    nextHash,
                    now,
                    session.ExpiresAt));
                await db.SaveChangesAsync(ct);
                response = CreateAuthResponse(user, session, now);
            }, cancellationToken);
        }
        catch (OptimisticConcurrencyException)
        {
            await HandleConcurrentRefreshAsync(hash, cancellationToken);
        }
        catch (PersistenceSerializationException)
        {
            await HandleConcurrentRefreshAsync(hash, cancellationToken);
        }

        if (failure is { } failed)
        {
            if (revokedUserId is Guid revokedUser && revokedSessionId is Guid revokedSession)
                await realtime.SessionRevokedAsync(revokedUser, revokedSession, failed.Code, cancellationToken);
            throw AppException.Unauthorized(failed.Code, failed.Message);
        }
        return new RefreshResult(response!, nextToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (current.SessionId is not Guid sessionId || current.UserId is not Guid userId || current.OrganizationId is not Guid organizationId)
            return;

        var now = clock.UtcNow;
        var session = db.UserSessions.SingleOrDefault(x => x.Id == sessionId && x.UserId == userId && x.OrganizationId == organizationId);
        if (session is null) return;

        await db.ExecuteInTransactionAsync(async ct =>
        {
            session.Revoke(now, "LOGOUT");
            foreach (var token in db.RefreshTokens.Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToList())
                token.Revoke(now, "LOGOUT");
            var user = db.Users.Single(x => x.Id == userId && x.OrganizationId == organizationId);
            db.Add(Audit(user, "Logout", "UserSession", session.Id, now));
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
    }

    public Task<CurrentUserResponse> MeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current.UserId is not Guid userId || current.OrganizationId is not Guid organizationId)
            throw AppException.Unauthorized("SESSION_REVOKED", "A sessão não é válida.");
        var user = db.Users.SingleOrDefault(x => x.Id == userId && x.OrganizationId == organizationId);
        if (user is null) throw AppException.Unauthorized("SESSION_REVOKED", "A sessão não é válida.");
        var employeeId = db.Employees.Where(x => x.UserId == user.Id && x.OrganizationId == organizationId).Select(x => (Guid?)x.Id).SingleOrDefault();
        return Task.FromResult(new CurrentUserResponse(user.Id, user.Name, user.Email, Role(user), user.OrganizationId, employeeId));
    }

    public Task<SessionValidationResult> ValidateSessionAsync(
        Guid userId,
        Guid organizationId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        var session = db.UserSessions.SingleOrDefault(x =>
            x.Id == sessionId && x.UserId == userId && x.OrganizationId == organizationId);
        if (session is null || session.RevokedAt is not null) return Task.FromResult(SessionValidationResult.Invalid("SESSION_REVOKED"));
        if (!session.IsActive(now)) return Task.FromResult(SessionValidationResult.Invalid("SESSION_EXPIRED"));
        var user = db.Users.SingleOrDefault(x => x.Id == userId && x.OrganizationId == organizationId);
        var organization = db.Organizations.SingleOrDefault(x => x.Id == organizationId);
        if (user is null || !user.IsActive || organization is null || !organization.IsActive)
            return Task.FromResult(SessionValidationResult.Invalid("SESSION_REVOKED"));
        return Task.FromResult(SessionValidationResult.Valid);
    }

    private async Task RegisterFailedLoginAsync(
        Guid userId,
        string attemptedPassword,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            db.ClearTrackedChanges();
            try
            {
                await db.ExecuteInTransactionAsync(async ct =>
                {
                    var freshUser = db.Users.SingleOrDefault(x => x.Id == userId);
                    if (freshUser is null || freshUser.IsLocked(now) || !freshUser.IsActive || freshUser.MustChangePassword ||
                        passwords.Verify(freshUser.PasswordHash, attemptedPassword))
                        return;
                    freshUser.RegisterFailedLogin(now);
                    await db.SaveChangesAsync(ct);
                }, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is OptimisticConcurrencyException or PersistenceSerializationException)
            {
                if (attempt == maxAttempts) return;
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 10), cancellationToken);
            }
        }
    }

    private async Task RevokeTokenFamilyAsync(RefreshTokenRecord record, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        await db.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var token in db.RefreshTokens.Where(x => x.TokenFamilyId == record.TokenFamilyId).ToList()) token.Revoke(now, reason);
            foreach (var session in db.UserSessions.Where(x => x.TokenFamilyId == record.TokenFamilyId).ToList()) session.Revoke(now, reason);
            var user = db.Users.Single(x => x.Id == record.UserId && x.OrganizationId == record.OrganizationId);
            db.Add(Audit(user, "SessionRevoked", "UserSession", record.SessionId, now));
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
        await realtime.SessionRevokedAsync(record.UserId, record.SessionId, reason, cancellationToken);
    }

    private async Task HandleConcurrentRefreshAsync(byte[] hash, CancellationToken cancellationToken)
    {
        db.ClearTrackedChanges();
        var replayed = db.RefreshTokens.SingleOrDefault(x => x.TokenHash == hash);
        if (replayed is null)
            throw AppException.Unauthorized("INVALID_REFRESH_TOKEN", "Refresh token inválido.");
        await RevokeTokenFamilyAsync(replayed, clock.UtcNow, "REFRESH_TOKEN_REUSE", cancellationToken);
        throw AppException.Unauthorized("REFRESH_TOKEN_REUSE", "Reutilização de refresh token detectada; a sessão foi revogada.");
    }

    private async Task RevokeSessionAsync(UserSession session, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        await db.ExecuteInTransactionAsync(async ct =>
        {
            session.Revoke(now, reason);
            foreach (var token in db.RefreshTokens.Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToList()) token.Revoke(now, reason);
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
    }

    private AuthResponse CreateAuthResponse(UserAccount user, UserSession session, DateTimeOffset now)
    {
        var employeeId = db.Employees
            .Where(x => x.UserId == user.Id && x.OrganizationId == user.OrganizationId)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefault();
        return new AuthResponse(tokens.CreateAccessToken(user, session, now), "Bearer", 300,
            new AuthUserResponse(user.Id, user.Name, Role(user), user.OrganizationId, employeeId));
    }

    private AuditLog Audit(UserAccount user, string action, string entityType, Guid entityId, DateTimeOffset now) =>
        new(user.OrganizationId, user.Id, action, entityType, entityId, "{}", current.CorrelationId, current.IpAddress, now);

    private static AppException InvalidCredentials() =>
        AppException.Unauthorized("INVALID_CREDENTIALS", "Credenciais inválidas.");

    private static string Role(UserAccount user) => user.Role.ToString().ToUpperInvariant();
    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
