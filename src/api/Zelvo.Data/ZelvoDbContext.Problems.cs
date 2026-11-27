using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Identity;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>Problem management (module 16, task H1).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Problem details of tickets of record type Problem.</summary>
    public DbSet<ProblemRecord> Problems => Set<ProblemRecord>();

    /// <summary>The known error database.</summary>
    public DbSet<KnownError> KnownErrors => Set<KnownError>();

    /// <summary>Last known error number per tenant.</summary>
    public DbSet<KnownErrorNumberSequence> KnownErrorNumberSequences => Set<KnownErrorNumberSequence>();

    /// <summary>Incidents linked to problems.</summary>
    public DbSet<ProblemIncident> ProblemIncidents => Set<ProblemIncident>();

    /// <summary>Knowledge articles linked to problems.</summary>
    public DbSet<ProblemArticle> ProblemArticles => Set<ProblemArticle>();

    private static void ConfigureProblems(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProblemRecord>(problem =>
        {
            problem.ToTable("problems", table =>
            {
                table.HasCheckConstraint("ck_problems_phase", EnumCheck("phase", Enum.GetNames<ProblemPhase>()));
                table.HasCheckConstraint("ck_problems_priority", "priority BETWEEN 1 AND 5");
                table.HasCheckConstraint("ck_problems_five_whys", "jsonb_typeof(five_whys) = 'object'");
                table.HasCheckConstraint("ck_problems_fishbone", "jsonb_typeof(fishbone) = 'object'");
            });
            problem.HasKey(p => p.TicketId);
            problem.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            problem.Property(p => p.Phase).HasConversion<string>().HasMaxLength(30);
            problem.Property(p => p.RootCause).HasMaxLength(ProblemRecord.RootCauseMaxLength);
            problem.Property(p => p.FiveWhys).HasColumnType("jsonb");
            problem.Property(p => p.Fishbone).HasColumnType("jsonb");
        });

        modelBuilder.Entity<KnownError>(error =>
        {
            error.ToTable("known_errors", table =>
            {
                table.HasCheckConstraint("ck_known_errors_status", EnumCheck("status", Enum.GetNames<KnownErrorStatus>()));
                table.HasCheckConstraint("ck_known_errors_sequence", "sequence >= 1");
                table.HasCheckConstraint("ck_known_errors_title", "coalesce(length(btrim(title_en)), 0) + coalesce(length(btrim(title_ar)), 0) > 0");
                table.HasCheckConstraint("ck_known_errors_workaround", "coalesce(length(btrim(workaround_en)), 0) + coalesce(length(btrim(workaround_ar)), 0) > 0");
            });
            error.HasKey(e => e.Id);
            error.Property(e => e.Id).ValueGeneratedNever();
            error.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            error.HasOne<Ticket>().WithMany().HasForeignKey(e => e.ProblemId).OnDelete(DeleteBehavior.Cascade);
            error.HasOne<KbArticle>().WithMany().HasForeignKey(e => e.ArticleId).OnDelete(DeleteBehavior.SetNull);
            error.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.CreatedById).OnDelete(DeleteBehavior.SetNull);
            error.Property(e => e.Number).HasMaxLength(KnownError.NumberMaxLength);
            error.ConfigureLocalizedText(e => e.Title, "title", KnownError.TitleMaxLength);
            error.ConfigureLocalizedText(e => e.Symptoms, "symptoms", KnownError.TextMaxLength);
            error.ConfigureLocalizedText(e => e.Workaround, "workaround", KnownError.TextMaxLength);
            error.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            error.Property(e => e.Version).IsRowVersion();
            error.HasIndex(e => new { e.TenantId, e.Number }).IsUnique();
            error.HasIndex(e => new { e.TenantId, e.Sequence }).IsUnique();
            error.HasIndex(e => e.ProblemId).IsUnique();
            error.HasIndex(e => e.ArticleId);
        });

        modelBuilder.Entity<KnownErrorNumberSequence>(sequence =>
        {
            sequence.ToTable("known_error_number_sequences");
            sequence.HasKey(s => s.TenantId);
            sequence.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProblemIncident>(link =>
        {
            link.ToTable("problem_incidents", table => table.HasCheckConstraint("ck_problem_incidents_not_self", "problem_id <> incident_id"));
            link.HasKey(l => new { l.ProblemId, l.IncidentId });
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.ProblemId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.IncidentId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.LinkedById).OnDelete(DeleteBehavior.SetNull);
            link.HasIndex(l => new { l.TenantId, l.IncidentId });
        });

        modelBuilder.Entity<ProblemArticle>(link =>
        {
            link.ToTable("problem_articles");
            link.HasKey(l => new { l.ProblemId, l.ArticleId });
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.ProblemId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<KbArticle>().WithMany().HasForeignKey(l => l.ArticleId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.LinkedById).OnDelete(DeleteBehavior.SetNull);
            link.HasIndex(l => new { l.TenantId, l.ArticleId });
        });
    }
}
