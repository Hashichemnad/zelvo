using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>The audit log is append-only and hash-chained per tenant.</summary>
public sealed class AuditLogTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provisioning_a_tenant_starts_its_chain()
    {
        var tenant = await database.CreateTenantAsync(nameEn: "Ajman Municipality", nameAr: "بلدية عجمان");

        await using var db = TestData.AppContext(database, tenant);
        var entry = await db.AuditEvents.SingleAsync(Ct);

        Assert.Equal(1, entry.Sequence);
        Assert.Equal("tenant.created", entry.Action);
        Assert.Equal(AuditCategory.Create, entry.Category);
        Assert.Equal(AuditActorType.System, entry.ActorType);
        Assert.Equal("tenant", entry.EntityType);
        Assert.Equal(tenant.Id.ToString(), entry.EntityId);
        Assert.Null(entry.PreviousHash);
        Assert.Equal(32, entry.Hash!.Length);
        using var changes = JsonDocument.Parse(entry.Changes!);
        Assert.Equal("بلدية عجمان", changes.RootElement.GetProperty("name.ar").GetProperty("new").GetString());
        Assert.Equal(tenant.Slug, changes.RootElement.GetProperty("slug").GetProperty("new").GetString());
    }

    [Fact]
    public async Task Changes_are_audited_field_by_field_in_the_same_save()
    {
        var tenant = await database.CreateTenantAsync(nameEn: "Old name");
        var audit = new AuditContext();
        audit.SetActor(AuditActorType.User, "user-1", "Fatima");
        audit.SetMetadata("correlationId", "abc123");

        await using (var db = TestData.AppContext(database, tenant, audit))
        {
            var row = await db.Tenants.SingleAsync(Ct);
            row.Name = row.Name with { En = "New name" };
            await db.SaveChangesAsync(Ct);
        }

        await using var read = TestData.AppContext(database, tenant);
        var entries = await read.AuditEvents.OrderBy(e => e.Sequence).ToListAsync(Ct);
        var change = entries[^1];
        Assert.Equal(2, change.Sequence);
        Assert.Equal("tenant.updated", change.Action);
        Assert.Equal(AuditCategory.Change, change.Category);
        Assert.Equal("user-1", change.ActorId);
        Assert.Equal("Fatima", change.ActorName);
        Assert.Equal(entries[0].Hash, change.PreviousHash);
        using var changes = JsonDocument.Parse(change.Changes!);
        var name = changes.RootElement.GetProperty("name.en");
        Assert.Equal("Old name", name.GetProperty("old").GetString());
        Assert.Equal("New name", name.GetProperty("new").GetString());
        Assert.False(changes.RootElement.TryGetProperty("name.ar", out _), "Unchanged fields are not recorded.");
        using var metadata = JsonDocument.Parse(change.Metadata!);
        Assert.Equal("abc123", metadata.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Saving_without_real_changes_writes_no_entry()
    {
        var tenant = await database.CreateTenantAsync();
        await using var db = TestData.AppContext(database, tenant);
        var row = await db.Tenants.SingleAsync(Ct);
        row.Name = new LocalizedText(row.Name.En, row.Name.Ar);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await db.AuditEvents.CountAsync(Ct));
    }

    [Fact]
    public async Task An_intact_chain_verifies()
    {
        var tenant = await database.CreateTenantAsync();
        await AddEntriesAsync(tenant, 4);

        await using var db = TestData.AppContext(database, tenant);
        var result = await db.VerifyAuditChainAsync(tenant.Id, Ct);
        var head = await db.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync(Ct);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.EventsChecked);
        Assert.Equal(5, result.HeadSequence);
        Assert.Equal(head.Hash, result.HeadHash);
    }

    [Fact]
    public async Task The_application_role_cannot_change_or_delete_entries()
    {
        var tenant = await database.CreateTenantAsync();
        await using var connection = await database.OpenAppConnectionAsync(tenant.Id);

        await using var update = new NpgsqlCommand("UPDATE audit_events SET action = 'rewritten'", connection);
        await using var delete = new NpgsqlCommand("DELETE FROM audit_events", connection);

        Assert.Equal(0, await update.ExecuteNonQueryAsync(Ct));
        Assert.Equal(0, await delete.ExecuteNonQueryAsync(Ct));
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal("tenant.created", (await db.AuditEvents.SingleAsync(Ct)).Action);
        Assert.True((await db.VerifyAuditChainAsync(tenant.Id, Ct)).IsValid);
    }

    [Theory]
    [InlineData("UPDATE audit_events SET action = 'rewritten' WHERE tenant_id = @t")]
    [InlineData("DELETE FROM audit_events WHERE tenant_id = @t")]
    [InlineData("TRUNCATE audit_events")]
    public async Task Even_the_owner_cannot_change_delete_or_truncate_entries(string sql)
    {
        var tenant = await database.CreateTenantAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsOwnerAsync(sql, ("t", tenant.Id)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_entry_breaks_the_chain_at_that_entry()
    {
        var tenant = await database.CreateTenantAsync();
        await AddEntriesAsync(tenant, 4);

        await TamperAsync("UPDATE audit_events SET metadata = '{\"forged\": true}' WHERE tenant_id = @t AND sequence = 3", tenant);

        await using var db = TestData.AppContext(database, tenant);
        var result = await db.VerifyAuditChainAsync(tenant.Id, Ct);
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidSequence);
        Assert.Equal(2, result.EventsChecked);
    }

    [Fact]
    public async Task A_removed_entry_breaks_the_chain()
    {
        var tenant = await database.CreateTenantAsync();
        await AddEntriesAsync(tenant, 4);

        await TamperAsync("DELETE FROM audit_events WHERE tenant_id = @t AND sequence = 2", tenant);

        await using var db = TestData.AppContext(database, tenant);
        var result = await db.VerifyAuditChainAsync(tenant.Id, Ct);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidSequence);
    }

    [Fact]
    public async Task A_rehashed_entry_still_breaks_the_link_to_the_next_one()
    {
        var tenant = await database.CreateTenantAsync();
        await AddEntriesAsync(tenant, 3);

        // An attacker who also recomputes the changed entry's own hash breaks the next link.
        await TamperAsync("""
            UPDATE audit_events SET action = 'covered.up' WHERE tenant_id = @t AND sequence = 2;
            UPDATE audit_events SET hash = zelvo_audit_hash(audit_events) WHERE tenant_id = @t AND sequence = 2;
            """, tenant);

        await using var db = TestData.AppContext(database, tenant);
        var result = await db.VerifyAuditChainAsync(tenant.Id, Ct);
        Assert.Equal(3, result.FirstInvalidSequence);
    }

    [Fact]
    public async Task Concurrent_writers_produce_one_unbroken_chain()
    {
        var tenant = await database.CreateTenantAsync();

        await Parallel.ForEachAsync(Enumerable.Range(0, 24), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
            async (i, ct) =>
            {
                await using var db = TestData.AppContext(database, tenant);
                var tenantContext = new TenantContext();
                tenantContext.SetTenant(tenant.Id, tenant.Slug);
                await new AuditLog(db, tenantContext, new AuditContext()).RecordAsync(AuditCategory.Admin, $"test.parallel.{i}", cancellationToken: ct);
            });

        await using var read = TestData.AppContext(database, tenant);
        var sequences = await read.AuditEvents.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync(Ct);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), sequences);
        Assert.True((await read.VerifyAuditChainAsync(tenant.Id, Ct)).IsValid);
    }

    [Fact]
    public async Task Entries_cannot_be_backdated()
    {
        var tenant = await database.CreateTenantAsync();
        await using var db = TestData.AppContext(database, tenant);
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenant.Id,
            Category = AuditCategory.Admin,
            Action = "test.backdated",
            ActorType = AuditActorType.System,
            OccurredAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(Ct);

        var entry = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "test.backdated", Ct);
        Assert.True(entry.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private async Task AddEntriesAsync(Tenant tenant, int count)
    {
        await using var db = TestData.AppContext(database, tenant);
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        var log = new AuditLog(db, tenantContext, new AuditContext());
        for (var i = 0; i < count; i++)
        {
            await log.RecordAsync(AuditCategory.Admin, $"test.entry.{i}", details: new Dictionary<string, object?> { ["i"] = i }, cancellationToken: Ct);
        }
    }

    /// <summary>Simulates someone with database superuser access editing rows directly.</summary>
    private Task<int> TamperAsync(string sql, Tenant tenant) => database.ExecuteAsOwnerAsync($"""
        ALTER TABLE audit_events DISABLE TRIGGER audit_events_no_update_delete;
        {sql};
        ALTER TABLE audit_events ENABLE TRIGGER audit_events_no_update_delete;
        """, ("t", tenant.Id));
}
