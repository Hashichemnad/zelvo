using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Secrets;
using Zelvo.Data.Tenancy;

namespace Zelvo.Secrets;

/// <summary>
/// Encrypts and decrypts secrets that Zelvo stores in the database (AI provider keys, payment
/// gateway credentials, connector passwords). Each value is encrypted with the current tenant's
/// data key (AES-256-GCM) and bound to the tenant and its <c>purpose</c>, so a value copied to
/// another tenant or another field does not decrypt. Store the returned text as is.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> for <paramref name="purpose"/>, for example <c>ai.provider.apiKey</c>.</summary>
    Task<string> ProtectAsync(string purpose, string plaintext, CancellationToken cancellationToken = default);

    /// <summary>Decrypts a value written by <see cref="ProtectAsync"/> for the same purpose and tenant.</summary>
    Task<string> UnprotectAsync(string purpose, string protectedValue, CancellationToken cancellationToken = default);
}

/// <summary>A protected value cannot be decrypted: changed, of another tenant or purpose, or not a protected value.</summary>
public sealed class SecretDecryptionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SecretDecryptionException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretDecryptionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <inheritdoc />
/// <remarks>
/// Format: <c>zv1.</c> then base64url of key id (16 bytes), nonce (12), ciphertext and tag (16).
/// The associated data is <c>zelvo/secret/v1|{tenant}|{purpose}</c>.
/// </remarks>
public sealed partial class SecretProtector(
    ZelvoDbContext db,
    ITenantContext tenant,
    IAuditLog audit,
    KeyRing keys,
    IMemoryCache cache,
    IOptions<SecretsOptions> options) : ISecretProtector
{
    /// <summary>Start of every protected value.</summary>
    public const string Prefix = "zv1.";

    private const int KeyIdSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataKeySize = 32;

    private static readonly Meter Meter = new("Zelvo.Secrets");
    private static readonly Counter<long> Unwraps = Meter.CreateCounter<long>("zelvo.secrets.data_key.unwraps", description: "Data keys unwrapped by the key-encryption key (cache misses).");

    /// <inheritdoc />
    public async Task<string> ProtectAsync(string purpose, string plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var tenantId = RequireTenant();
        var aad = AssociatedData(tenantId, purpose);
        var (keyId, dataKey) = await ActiveKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[KeyIdSize + NonceSize + input.Length + TagSize];
        keyId.TryWriteBytes(output.AsSpan(0, KeyIdSize));
        var nonce = output.AsSpan(KeyIdSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(dataKey, TagSize))
        {
            aes.Encrypt(nonce, input, output.AsSpan(KeyIdSize + NonceSize, input.Length), output.AsSpan(KeyIdSize + NonceSize + input.Length), aad);
        }

        CryptographicOperations.ZeroMemory(input);
        return Prefix + Base64Url.EncodeToString(output);
    }

    /// <inheritdoc />
    public async Task<string> UnprotectAsync(string purpose, string protectedValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        var tenantId = RequireTenant();
        var aad = AssociatedData(tenantId, purpose);
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new SecretDecryptionException("Not a protected value.");
        }

        byte[] data;
        try
        {
            data = Base64Url.DecodeFromChars(protectedValue.AsSpan(Prefix.Length));
        }
        catch (FormatException ex)
        {
            throw new SecretDecryptionException("Not a protected value.", ex);
        }

        if (data.Length < KeyIdSize + NonceSize + TagSize)
        {
            throw new SecretDecryptionException("Not a protected value.");
        }

        var keyId = new Guid(data.AsSpan(0, KeyIdSize));
        var dataKey = await KeyAsync(keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new SecretDecryptionException("The value was encrypted with a data key of another tenant or one that does not exist.");

        var length = data.Length - KeyIdSize - NonceSize - TagSize;
        var output = new byte[length];
        try
        {
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(data.AsSpan(KeyIdSize, NonceSize), data.AsSpan(KeyIdSize + NonceSize, length), data.AsSpan(KeyIdSize + NonceSize + length), output, aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SecretDecryptionException("The value was changed, or belongs to another tenant or purpose.", ex);
        }

        try
        {
            return Encoding.UTF8.GetString(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>Whether <paramref name="purpose"/> is a valid purpose: letters and digits (starting with a lower-case letter), '.', ':' and '-'.</summary>
    public static bool IsPurpose(string? purpose) => purpose is { Length: > 0 and <= 200 } && PurposePattern().IsMatch(purpose);

    private Guid RequireTenant() =>
        tenant.TenantId ?? throw new InvalidOperationException("Secrets are encrypted per tenant, but no tenant is set.");

    private static byte[] AssociatedData(Guid tenantId, string purpose) =>
        IsPurpose(purpose)
            ? Encoding.ASCII.GetBytes($"zelvo/secret/v1|{tenantId:N}|{purpose}")
            : throw new ArgumentException("A purpose is letters and digits (starting with a lower-case letter), '.', ':' and '-', at most 200 characters.", nameof(purpose));

    /// <summary>The tenant's active data key, created (and audited) on first use.</summary>
    private async Task<(Guid Id, byte[] Key)> ActiveKeyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var active = await db.TenantDataKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.RetiredAt == null)
            .Select(k => new { k.Id, k.KekId, k.WrappedKey })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return (active.Id, await UnwrapCachedAsync(active.Id, active.KekId, active.WrappedKey, cancellationToken).ConfigureAwait(false));
        }

        var dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        var id = Guid.CreateVersion7();
        var wrapped = await keys.Current.WrapAsync(dataKey, cancellationToken).ConfigureAwait(false);

        // Written straight away (not with the caller's pending changes), in the caller's
        // transaction if there is one. Two requests creating a key at once: one wins, the other
        // uses the winner's key.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tenant_data_keys (id, tenant_id, kek_id, wrapped_key, created_at)
            VALUES ({id}, {tenantId}, {keys.Current.Id}, {wrapped}, {DateTimeOffset.UtcNow})
            ON CONFLICT (tenant_id) WHERE retired_at IS NULL DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            return await ActiveKeyAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }

        audit.Add(AuditCategory.Security, "secrets.dataKeyCreated", "tenant_data_key", id.ToString(),
            new Dictionary<string, object?> { ["kekId"] = keys.Current.Id });
        cache.Set(CacheKey(id), dataKey, options.Value.DataKeyCacheDuration);
        return (id, dataKey);
    }

    private async Task<byte[]?> KeyAsync(Guid keyId, CancellationToken cancellationToken)
    {
        // Row-level security returns only the current tenant's keys.
        var key = await db.TenantDataKeys.AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => new { k.TenantId, k.KekId, k.WrappedKey })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return key is null || key.TenantId != tenant.TenantId
            ? null
            : await UnwrapCachedAsync(keyId, key.KekId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> UnwrapCachedAsync(Guid id, string kekId, byte[] wrapped, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey(id), out byte[]? cached) && cached is not null)
        {
            return cached;
        }

        var dataKey = await keys.For(kekId).UnwrapAsync(kekId, wrapped, cancellationToken).ConfigureAwait(false);
        Unwraps.Add(1);
        cache.Set(CacheKey(id), dataKey, options.Value.DataKeyCacheDuration);
        return dataKey;
    }

    private static string CacheKey(Guid id) => $"zelvo:data-key:{id:N}";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-zA-Z0-9.:-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PurposePattern();
}
