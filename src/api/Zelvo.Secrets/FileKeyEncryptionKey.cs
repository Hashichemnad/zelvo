using System.Security.Cryptography;
using System.Text;

namespace Zelvo.Secrets;

/// <summary>
/// A master key read from a file (base64 of 32 bytes). Wraps data keys with AES-256-GCM. Its id is
/// <c>file:</c> and the first 16 hex digits of the key's SHA-256, so it identifies the key without
/// revealing it, and a rotated key file gets a new id.
/// </summary>
public sealed class FileKeyEncryptionKey : IKeyEncryptionKey
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("zelvo/data-key/v1");
    private readonly byte[] _key;

    /// <summary>Creates the key from its 32 bytes.</summary>
    public FileKeyEncryptionKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new KeyEncryptionKeyException("A master key must be 32 bytes (AES-256).");
        }

        _key = key.ToArray();
        Id = $"file:{Convert.ToHexStringLower(SHA256.HashData(_key))[..16]}";
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Reads a key file.</summary>
    public static FileKeyEncryptionKey Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KeyEncryptionKeyException($"The master key file '{path}' cannot be read.", ex);
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new KeyEncryptionKeyException($"The master key file '{path}' does not hold base64 text.", ex);
        }

        try
        {
            return new FileKeyEncryptionKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public bool CanUnwrap(string kekId) => string.Equals(kekId, Id, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<byte[]> WrapAsync(byte[] dataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        var output = new byte[NonceSize + dataKey.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, dataKey, output.AsSpan(NonceSize, dataKey.Length), output.AsSpan(NonceSize + dataKey.Length), Aad);
        return Task.FromResult(output);
    }

    /// <inheritdoc />
    public Task<byte[]> UnwrapAsync(string kekId, byte[] wrapped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (!CanUnwrap(kekId) || wrapped.Length <= NonceSize + TagSize)
        {
            throw new KeyEncryptionKeyException("This master key did not wrap the data key.");
        }

        var length = wrapped.Length - NonceSize - TagSize;
        var dataKey = new byte[length];
        using var aes = new AesGcm(_key, TagSize);
        try
        {
            aes.Decrypt(wrapped.AsSpan(0, NonceSize), wrapped.AsSpan(NonceSize, length), wrapped.AsSpan(NonceSize + length), dataKey, Aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new KeyEncryptionKeyException("The wrapped data key was changed or belongs to another master key.", ex);
        }

        return Task.FromResult(dataKey);
    }
}
