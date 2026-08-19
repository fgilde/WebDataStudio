using System.Security.Cryptography;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class SecretProtectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-secret").FullName;
    public void Dispose() => TestDirectory.Remove(_dir);

    [Fact]
    public void Roundtrips_a_secret()
    {
        var protector = new SecretProtector(_dir, configuredKeyBase64: null);
        var payload = protector.Protect("Host=db;Password=hunter2");
        Assert.Equal("Host=db;Password=hunter2", protector.Unprotect(payload));
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var protector = new SecretProtector(_dir, configuredKeyBase64: null);
        Assert.DoesNotContain("hunter2", protector.Protect("Password=hunter2"));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var protector = new SecretProtector(_dir, configuredKeyBase64: null);
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void Generated_key_is_reused_across_instances()
    {
        var payload = new SecretProtector(_dir, null).Protect("value");
        Assert.Equal("value", new SecretProtector(_dir, null).Unprotect(payload));
    }

    [Fact]
    public void Configured_key_is_honoured()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = new SecretProtector(_dir, key).Protect("value");
        Assert.Equal("value", new SecretProtector(_dir, key).Unprotect(payload));
        Assert.False(File.Exists(Path.Combine(_dir, ".key")));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var protector = new SecretProtector(_dir, null);
        var payload = protector.Protect("value");
        var bytes = Convert.FromBase64String(payload);
        bytes[^1] ^= 0xFF;
        // AesGcm throws AuthenticationTagMismatchException, a CryptographicException subclass.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
    }
}
