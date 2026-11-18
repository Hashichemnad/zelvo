using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Secrets;

/// <summary>What a rewrap of one tenant's data keys did.</summary>
public sealed record RewrapResult(int Rewrapped, int Unchanged);

/// <summary>
/// Wraps a tenant's data keys again with the current key-encryption key: after a new master key
/// file was configured, after moving from a key file to Vault, or after the Transit key was
/// rotated in Vault. Stored secrets are not touched (they stay encrypted with the same data keys).
/// Optionally retires the active data key, so new secrets get a new one.
/// </summary>
public sealed class DataKeyRewrap(ZelvoDbContext db, ITenantContext tenant, IAuditLog audit, KeyRing keys)
{
    /// <summary>Rewraps the current tenant's data keys.</summary>
    public async Task<RewrapResult> RewrapAsync(bool retireActive, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant is set.");
        var dataKeys = await db.TenantDataKeys.Where(k => k.TenantId == tenantId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = keys.Current;
        int rewrapped = 0, unchanged = 0;
        foreach (var key in dataKeys)
        {
            if (string.Equals(key.KekId, current.Id, StringComparison.Ordinal))
            {
                if (current is VaultTransitKeyEncryptionKey vault)
                {
                    // Same Transit key; Vault moves it to the key's latest version.
                    key.WrappedKey = await vault.RewrapAsync(key.WrappedKey, cancellationToken).ConfigureAwait(false);
                    key.RewrappedAt = DateTimeOffset.UtcNow;
                    rewrapped++;
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var plain = await keys.For(key.KekId).UnwrapAsync(key.KekId, key.WrappedKey, cancellationToken).ConfigureAwait(false);
            try
            {
                key.WrappedKey = await current.WrapAsync(plain, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            key.KekId = current.Id;
            key.RewrappedAt = DateTimeOffset.UtcNow;
            rewrapped++;
        }

        var retired = 0;
        if (retireActive)
        {
            foreach (var key in dataKeys.Where(k => k.RetiredAt is null))
            {
                key.RetiredAt = DateTimeOffset.UtcNow;
                retired++;
            }
        }

        audit.Add(AuditCategory.Admin, "secrets.dataKeysRewrapped", details: new Dictionary<string, object?>
        {
            ["kekId"] = current.Id,
            ["rewrapped"] = rewrapped,
            ["unchanged"] = unchanged,
            ["retired"] = retired,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RewrapResult(rewrapped, unchanged);
    }
}
