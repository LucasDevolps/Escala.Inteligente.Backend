using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string SigningKeyBase64 { get; init; } = "";
}

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";
    public string KeyId { get; init; } = "";
    public string KeyBase64 { get; init; } = "";
    public Dictionary<string, string> PreviousKeys { get; init; } = [];
}

public sealed class AspNetPasswordService : IPasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object Marker = new();
    private readonly string _dummyHash;

    public AspNetPasswordService() => _dummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public string Hash(string password) => _hasher.HashPassword(Marker, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(Marker, hash, password) is not PasswordVerificationResult.Failed;

    public void PerformDummyVerification(string password) => _ = Verify(_dummyHash, password);
}

public sealed class Sha256TokenHasher : ITokenHasher
{
    public byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public string CreateAccessToken(UserAccount user, UserSession session, DateTimeOffset now)
    {
        var keyBytes = DecodeSigningKey(_options.SigningKeyBase64);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("sid", session.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new Claim("organization_id", user.OrganizationId.ToString()),
            new Claim("role", user.Role.ToString().ToUpperInvariant())
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = credentials
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public string GenerateOpaqueToken(int sizeInBytes = 32) =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(sizeInBytes));

    public static byte[] DecodeSigningKey(string base64)
    {
        try
        {
            var key = Convert.FromBase64String(base64);
            if (key.Length < 32) throw new InvalidOperationException("Jwt:SigningKeyBase64 deve possuir pelo menos 256 bits.");
            return key;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Jwt:SigningKeyBase64 deve ser Base64 válido.", exception);
        }
    }
}

public sealed class ConfigurationEncryptionKeyProvider(IOptions<EncryptionOptions> options) : IEncryptionKeyProvider
{
    private readonly EncryptionOptions _options = options.Value;

    public EncryptionKey GetCurrentKey()
    {
        if (string.IsNullOrWhiteSpace(_options.KeyId)) throw new InvalidOperationException("Encryption:KeyId não foi configurado.");
        return new EncryptionKey(_options.KeyId, Decode(_options.KeyBase64));
    }

    public EncryptionKey GetKey(string keyId)
    {
        if (string.Equals(keyId, _options.KeyId, StringComparison.Ordinal)) return GetCurrentKey();
        if (_options.PreviousKeys.TryGetValue(keyId, out var encoded)) return new EncryptionKey(keyId, Decode(encoded));
        throw new CryptographicException("Chave de criptografia não disponível.");
    }

    private static byte[] Decode(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length != 32) throw new InvalidOperationException("A chave AES deve possuir exatamente 256 bits.");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Encryption:KeyBase64 deve ser Base64 válido.", exception);
        }
    }
}

public sealed class AesGcmNotificationCipher(IEncryptionKeyProvider keys) : INotificationCipher
{
    public EncryptedPayload Encrypt(string plaintext, ReadOnlySpan<byte> associatedData)
    {
        var key = keys.GetCurrentKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key.KeyBytes, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
        CryptographicOperations.ZeroMemory(plaintextBytes);
        return new EncryptedPayload(key.KeyId, nonce, ciphertext, tag);
    }

    public string Decrypt(EncryptedPayload payload, ReadOnlySpan<byte> associatedData)
    {
        var key = keys.GetKey(payload.KeyId);
        var plaintext = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(key.KeyBytes, payload.AuthenticationTag.Length);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.AuthenticationTag, plaintext, associatedData);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today(string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, zone).DateTime);
    }
}

public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task SessionRevokedAsync(Guid userId, Guid sessionId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task NotificationCreatedAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken) => Task.CompletedTask;
}
