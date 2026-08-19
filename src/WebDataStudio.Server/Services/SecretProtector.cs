using System.Security.Cryptography;
using System.Text;

namespace WebDataStudio.Server.Services;

/// AES-GCM protection for stored connection secrets. The key comes from WDS_SECRET_KEY
/// (base64, 32 bytes); without it a key is generated once and kept next to the database.
public sealed class SecretProtector
{
    private const int NonceSize = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;     // AesGcm.TagByteSizes.MaxSize
    private readonly byte[] _key;

    public SecretProtector(string keyDirectory, string? configuredKeyBase64)
    {
        if (!string.IsNullOrWhiteSpace(configuredKeyBase64))
        {
            _key = Convert.FromBase64String(configuredKeyBase64);
            if (_key.Length != 32)
                throw new InvalidOperationException("WDS_SECRET_KEY must be 32 bytes, base64 encoded");
            return;
        }

        // A directory the studio cannot write is not a reason to refuse to start: without it
        // there is nowhere to store a connection either, so a key held in memory is exactly as
        // useful as a persisted one. The stores report the same problem with the path in it.
        try
        {
            _key = LoadOrCreateKey(keyDirectory);
        }
        catch (Exception)
        {
            Ephemeral = true;
            _key = RandomNumberGenerator.GetBytes(32);
        }
    }

    /// True when the key lives only in this process, because its directory could not be used.
    /// Anything encrypted with it dies with the container — which is fine, since nothing can be
    /// stored in that state anyway.
    public bool Ephemeral { get; }

    private static byte[] LoadOrCreateKey(string keyDirectory)
    {
        Directory.CreateDirectory(keyDirectory);
        var keyFile = Path.Combine(keyDirectory, ".key");
        if (File.Exists(keyFile)) return Convert.FromBase64String(File.ReadAllText(keyFile).Trim());

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFile, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return key;
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string payload)
    {
        var bytes = Convert.FromBase64String(payload);
        if (bytes.Length < NonceSize + TagSize) throw new CryptographicException("payload too short");

        var nonce = bytes.AsSpan(0, NonceSize);
        var tag = bytes.AsSpan(NonceSize, TagSize);
        var cipher = bytes.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
