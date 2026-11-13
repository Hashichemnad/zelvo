using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Change capture without a database: EF Core tracks changes in memory.</summary>
public sealed class AuditChangeCollectorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public void Sensitive_fields_are_recorded_as_changed_with_masked_values()
    {
        using var db = new SampleContext();
        var person = new Person { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Ali", EmiratesId = "784-1990-1234567-1" };
        db.Attach(person);
        person.Name = "Ali Hassan";
        person.EmiratesId = "784-1990-7654321-2";

        var entry = Assert.Single(Collect(db));

        Assert.Equal("person.updated", entry.Action);
        Assert.Equal(AuditCategory.Change, entry.Category);
        Assert.Equal(TenantId, entry.TenantId);
        using var changes = JsonDocument.Parse(entry.Changes!);
        Assert.Equal("Ali", changes.RootElement.GetProperty("name").GetProperty("old").GetString());
        Assert.Equal("[masked]", changes.RootElement.GetProperty("emiratesId").GetProperty("old").GetString());
        Assert.Equal("[masked]", changes.RootElement.GetProperty("emiratesId").GetProperty("new").GetString());
        Assert.DoesNotContain("784-", entry.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public void Creations_and_deletions_are_recorded()
    {
        using var db = new SampleContext();
        var removed = new Person { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Old" };
        db.Attach(removed);
        db.Remove(removed);
        db.Add(new Person { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "New" });

        var actions = Collect(db).Select(e => e.Action).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["person.created", "person.deleted"], actions);
    }

    [Fact]
    public void Records_that_are_not_audited_are_ignored()
    {
        using var db = new SampleContext();
        db.Add(new Note { Id = 1, Text = "not audited" });

        Assert.Empty(Collect(db));
    }

    private static List<AuditEvent> Collect(SampleContext db)
    {
        db.ChangeTracker.DetectChanges();
        var tenant = new TenantContext();
        tenant.SetTenant(TenantId, "sample");
        return AuditChangeCollector.Collect(db, tenant, new AuditContext());
    }

    private sealed class Person : IAuditedEntity, ITenantOwned
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; set; }

        public required string Name { get; set; }

        [SensitiveData]
        public string? EmiratesId { get; set; }
    }

    private sealed class Note
    {
        public int Id { get; init; }

        public required string Text { get; set; }
    }

    private sealed class SampleContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseZelvoNpgsql("Host=unused.invalid");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>();
            modelBuilder.Entity<Note>();
        }
    }
}
