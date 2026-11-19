using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Secrets;

/// <summary>
/// A tenant's data key (AES-256) for secrets stored in the database, such as AI provider keys and
/// payment gateway credentials. Only the wrapped form is stored: the key is encrypted by the
/// installation's key-encryption key (a master key file or a Vault/OpenBao Transit key) and is
/// unwrapped in memory when needed. A tenant has one active key; retired keys still decrypt
/// values written with them. See docs/architecture/secrets-encryption.md.
/// </summary>
public sealed class TenantDataKey : ITenantOwned
{
    /// <summary>Maximum length of <see cref="KekId"/>.</summary>
    public const int KekIdMaxLength = 200;

    /// <summary>Key id; part of every value encrypted with this key.</summary>
    public Guid Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Which key-encryption key wrapped this key, for example <c>file:3f2a…</c> or <c>vault:transit/zelvo</c>.</summary>
    public required string KekId { get; set; }

    /// <summary>The data key, encrypted by the key-encryption key.</summary>
    public required byte[] WrappedKey { get; set; }

    /// <summary>When the key was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the key was last wrapped again (key-encryption key rotation).</summary>
    public DateTimeOffset? RewrappedAt { get; set; }

    /// <summary>When the key stopped being used for new values; null for the active key.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
