using Microsoft.EntityFrameworkCore;

namespace Zelvo.Data.Auditing;

/// <summary>Result of checking a tenant's audit hash chain.</summary>
public sealed class AuditChainVerification
{
    /// <summary>Entries checked and found intact, from the start of the chain.</summary>
    public long EventsChecked { get; init; }

    /// <summary>First entry that was changed, removed or reordered; <see langword="null"/> when the chain is intact.</summary>
    public long? FirstInvalidSequence { get; init; }

    /// <summary>Sequence of the newest entry.</summary>
    public long? HeadSequence { get; init; }

    /// <summary>Hash of the newest entry. Recording it elsewhere lets a later check detect removed newest entries.</summary>
    public byte[]? HeadHash { get; init; }

    /// <summary>The chain is intact.</summary>
    public bool IsValid => FirstInvalidSequence is null;
}

/// <summary>Audit hash chain checks.</summary>
public static class AuditChain
{
    /// <summary>
    /// Recomputes every hash of the tenant's chain in the database. Row-level security applies,
    /// so the application can only check the current tenant.
    /// </summary>
    public static async Task<AuditChainVerification> VerifyAuditChainAsync(
        this ZelvoDbContext db, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Column names follow the snake_case naming convention, also for raw SQL results.
        var results = await db.Database.SqlQuery<AuditChainVerification>($"""
            SELECT events_checked, first_invalid_sequence, head_sequence, head_hash
            FROM zelvo_verify_audit_chain({tenantId})
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return results.Single();
    }
}
