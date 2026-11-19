namespace Zelvo.Secrets;

/// <summary>
/// A key-encryption key: wraps and unwraps tenants' data keys. The key itself never leaves its
/// store (the key file is read into memory only by this process; Vault keeps Transit keys inside Vault).
/// </summary>
public interface IKeyEncryptionKey
{
    /// <summary>Id stored with every data key this key wraps, for example <c>file:3f2a…</c>.</summary>
    string Id { get; }

    /// <summary>Whether this key can unwrap a data key wrapped by <paramref name="kekId"/>.</summary>
    bool CanUnwrap(string kekId);

    /// <summary>Encrypts a data key.</summary>
    Task<byte[]> WrapAsync(byte[] dataKey, CancellationToken cancellationToken);

    /// <summary>Decrypts a data key wrapped by <paramref name="kekId"/>.</summary>
    Task<byte[]> UnwrapAsync(string kekId, byte[] wrapped, CancellationToken cancellationToken);
}

/// <summary>A key-encryption key could not be used: missing, wrong, or its store is unavailable.</summary>
public sealed class KeyEncryptionKeyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public KeyEncryptionKeyException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public KeyEncryptionKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public KeyEncryptionKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
