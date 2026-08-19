using System.Security.Cryptography;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Infrastructure.Security;

namespace ScheduleManager.IntegrationTests;

public sealed class EncryptionTests
{
    [Fact]
    public void Aes_gcm_authenticates_notification_context_with_aad()
    {
        var cipher = new AesGcmNotificationCipher(new FixedKeyProvider());
        var aad = "notification|tenant|recipient"u8.ToArray();
        var encrypted = cipher.Encrypt("conteúdo confidencial", aad);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes("conteúdo confidencial"), encrypted.Ciphertext);
        Assert.Equal("conteúdo confidencial", cipher.Decrypt(encrypted, aad));
        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(encrypted, "different"u8.ToArray()));
    }

    private sealed class FixedKeyProvider : IEncryptionKeyProvider
    {
        private readonly EncryptionKey _key = new("test", RandomNumberGenerator.GetBytes(32));
        public EncryptionKey GetCurrentKey() => _key;
        public EncryptionKey GetKey(string keyId) => keyId == _key.KeyId ? _key : throw new CryptographicException();
    }
}
