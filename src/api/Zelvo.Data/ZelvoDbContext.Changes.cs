using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Changes;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>Change management (module 17, task H3).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Change details of tickets of record type Change.</summary>
    public DbSet<ChangeRecord> Changes => Set<ChangeRecord>();

    /// <summary>The catalog of standard change templates.</summary>
    public DbSet<ChangeTemplate> ChangeTemplates => Set<ChangeTemplate>();

    /// <summary>Blackout periods.</summary>
    public DbSet<ChangeBlackout> ChangeBlackouts => Set<ChangeBlackout>();

    /// <summary>Problems changes fix and incidents they caused.</summary>
    public DbSet<ChangeLink> ChangeLinks => Set<ChangeLink>();

    /// <summary>The change advisory board and the emergency board (task H4).</summary>
    public DbSet<ChangeBoard> ChangeBoards => Set<ChangeBoard>();

    /// <summary>Members of the boards.</summary>
    public DbSet<ChangeBoardMember> ChangeBoardMembers => Set<ChangeBoardMember>();

    /// <summary>Meetings of the change advisory board.</summary>
    public DbSet<CabMeeting> CabMeetings => Set<CabMeeting>();

    /// <summary>Members invited to meetings.</summary>
    public DbSet<CabMeetingMember> CabMeetingMembers => Set<CabMeetingMember>();

    /// <summary>Changes on meetings' agendas.</summary>
    public DbSet<CabAgendaItem> CabAgendaItems => Set<CabAgendaItem>();

    /// <summary>Members' votes.</summary>
    public DbSet<CabVote> CabVotes => Set<CabVote>();

    private static void ConfigureChanges(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChangeRecord>(change =>
        {
            change.ToTable("changes", table =>
            {
                table.HasCheckConstraint("ck_changes_kind", EnumCheck("kind", Enum.GetNames<ChangeKind>()));
                table.HasCheckConstraint("ck_changes_phase", EnumCheck("phase", Enum.GetNames<ChangePhase>()));
                table.HasCheckConstraint("ck_changes_risk_level", EnumCheck("risk_level", Enum.GetNames<ChangeRiskLevel>()));
                table.HasCheckConstraint("ck_changes_outcome", EnumCheck("outcome", Enum.GetNames<ChangeOutcome>()));
                table.HasCheckConstraint("ck_changes_closure", EnumCheck("closure", Enum.GetNames<ChangeClosure>()));
                table.HasCheckConstraint("ck_changes_risk_answers", "jsonb_typeof(risk_answers) = 'object'");
                table.HasCheckConstraint("ck_changes_window", "planned_start IS NULL OR planned_end IS NULL OR planned_end > planned_start");
            });
            change.HasKey(c => c.TicketId);
            change.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            change.HasOne<ChangeTemplate>().WithMany().HasForeignKey(c => c.TemplateId).OnDelete(DeleteBehavior.SetNull);
            change.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.ReviewedById).OnDelete(DeleteBehavior.SetNull);
            change.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            change.Property(c => c.Phase).HasConversion<string>().HasMaxLength(20);
            change.Property(c => c.RiskLevel).HasConversion<string>().HasMaxLength(20);
            change.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(30);
            change.Property(c => c.Closure).HasConversion<string>().HasMaxLength(20);
            change.Property(c => c.RiskAnswers).HasColumnType("jsonb");
            change.Property(c => c.ImplementationPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            change.Property(c => c.TestPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            change.Property(c => c.BackoutPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            change.Property(c => c.ReviewNotes).HasMaxLength(ChangeRecord.PlanMaxLength);
            change.HasIndex(c => new { c.TenantId, c.PlannedStart });
            change.HasIndex(c => c.TemplateId);
        });

        modelBuilder.Entity<ChangeTemplate>(template =>
        {
            template.ToTable("change_templates", table =>
            {
                table.HasCheckConstraint("ck_change_templates_name", "coalesce(length(btrim(name_en)), 0) + coalesce(length(btrim(name_ar)), 0) > 0");
                table.HasCheckConstraint("ck_change_templates_tasks", "jsonb_typeof(tasks) = 'array'");
            });
            template.HasKey(t => t.Id);
            template.Property(t => t.Id).ValueGeneratedNever();
            template.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            template.HasOne<Category>().WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.SetNull);
            template.HasOne<SupportGroup>().WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.SetNull);
            template.ConfigureLocalizedText(t => t.Name, "name", ChangeTemplate.NameMaxLength);
            template.ConfigureLocalizedText(t => t.Description, "description", ChangeTemplate.DescriptionMaxLength);
            template.Property(t => t.ImplementationPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            template.Property(t => t.TestPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            template.Property(t => t.BackoutPlan).HasMaxLength(ChangeRecord.PlanMaxLength);
            template.Property(t => t.Tasks).HasColumnType("jsonb");
            template.Property(t => t.Version).IsRowVersion();
            template.HasIndex(t => t.TenantId);
            template.HasIndex(t => t.CategoryId);
            template.HasIndex(t => t.GroupId);
        });

        modelBuilder.Entity<ChangeBlackout>(blackout =>
        {
            blackout.ToTable("change_blackouts", table =>
            {
                table.HasCheckConstraint("ck_change_blackouts_name", "coalesce(length(btrim(name_en)), 0) + coalesce(length(btrim(name_ar)), 0) > 0");
                table.HasCheckConstraint("ck_change_blackouts_window", "ends_at > starts_at");
            });
            blackout.HasKey(b => b.Id);
            blackout.Property(b => b.Id).ValueGeneratedNever();
            blackout.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
            blackout.HasOne<ConfigurationItem>().WithMany().HasForeignKey(b => b.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            blackout.ConfigureLocalizedText(b => b.Name, "name", ChangeBlackout.NameMaxLength);
            blackout.Property(b => b.Version).IsRowVersion();
            blackout.HasIndex(b => new { b.TenantId, b.StartsAt });
            blackout.HasIndex(b => b.ConfigurationItemId);
        });

        modelBuilder.Entity<ChangeBoard>(board =>
        {
            board.ToTable("change_boards", table =>
            {
                table.HasCheckConstraint("ck_change_boards_kind", EnumCheck("kind", Enum.GetNames<ChangeBoardKind>()));
                table.HasCheckConstraint("ck_change_boards_hours", "decide_within_hours BETWEEN 1 AND 168");
            });
            board.HasKey(b => b.Id);
            board.Property(b => b.Id).ValueGeneratedNever();
            board.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
            board.HasOne<UserAccount>().WithMany().HasForeignKey(b => b.ChairId).OnDelete(DeleteBehavior.SetNull);
            board.HasOne<UserAccount>().WithMany().HasForeignKey(b => b.DeputyChairId).OnDelete(DeleteBehavior.SetNull);
            board.HasMany(b => b.Members).WithOne().HasForeignKey(m => m.BoardId).OnDelete(DeleteBehavior.Cascade);
            board.Property(b => b.Kind).HasConversion<string>().HasMaxLength(20);
            board.ConfigureLocalizedText(b => b.Name, "name", ChangeBoard.NameMaxLength);
            board.Property(b => b.Version).IsRowVersion();
            board.HasIndex(b => new { b.TenantId, b.Kind }).IsUnique();
            board.HasIndex(b => b.ChairId);
            board.HasIndex(b => b.DeputyChairId);
        });

        modelBuilder.Entity<ChangeBoardMember>(member =>
        {
            member.ToTable("change_board_members");
            member.HasKey(m => new { m.BoardId, m.UserId });
            member.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            member.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });

        modelBuilder.Entity<CabMeeting>(meeting =>
        {
            meeting.ToTable("cab_meetings", table =>
            {
                table.HasCheckConstraint("ck_cab_meetings_state", EnumCheck("state", Enum.GetNames<CabMeetingState>()));
                table.HasCheckConstraint("ck_cab_meetings_window", "ends_at > starts_at");
                table.HasCheckConstraint("ck_cab_meetings_title", "coalesce(length(btrim(title_en)), 0) + coalesce(length(btrim(title_ar)), 0) > 0");
            });
            meeting.HasKey(m => m.Id);
            meeting.Property(m => m.Id).ValueGeneratedNever();
            meeting.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            meeting.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.ChairId).OnDelete(DeleteBehavior.Restrict);
            meeting.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.CreatedById).OnDelete(DeleteBehavior.SetNull);
            meeting.ConfigureLocalizedText(m => m.Title, "title", CabMeeting.TitleMaxLength);
            meeting.Property(m => m.Location).HasMaxLength(CabMeeting.LocationMaxLength);
            meeting.Property(m => m.Minutes).HasMaxLength(CabMeeting.MinutesMaxLength);
            meeting.Property(m => m.State).HasConversion<string>().HasMaxLength(20);
            meeting.Property(m => m.Version).IsRowVersion();
            meeting.HasIndex(m => new { m.TenantId, m.StartsAt });
            meeting.HasIndex(m => m.ChairId);
            meeting.HasIndex(m => m.CreatedById);
        });

        modelBuilder.Entity<CabMeetingMember>(member =>
        {
            member.ToTable("cab_meeting_members");
            member.HasKey(m => new { m.MeetingId, m.UserId });
            member.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            member.HasOne<CabMeeting>().WithMany().HasForeignKey(m => m.MeetingId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });

        modelBuilder.Entity<CabAgendaItem>(item =>
        {
            item.ToTable("cab_agenda_items", table => table.HasCheckConstraint("ck_cab_agenda_items_decision", EnumCheck("decision", Enum.GetNames<CabDecision>())));
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).ValueGeneratedNever();
            item.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<CabMeeting>().WithMany().HasForeignKey(i => i.MeetingId).OnDelete(DeleteBehavior.Cascade);
            item.HasOne<Ticket>().WithMany().HasForeignKey(i => i.ChangeId).OnDelete(DeleteBehavior.Cascade);
            item.HasOne<UserAccount>().WithMany().HasForeignKey(i => i.DecidedById).OnDelete(DeleteBehavior.SetNull);
            item.Property(i => i.Decision).HasConversion<string>().HasMaxLength(20);
            item.Property(i => i.Comment).HasMaxLength(CabAgendaItem.TextMaxLength);
            item.Property(i => i.Minutes).HasMaxLength(CabAgendaItem.TextMaxLength);
            item.HasIndex(i => new { i.MeetingId, i.ChangeId }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.ChangeId });
            item.HasIndex(i => i.DecidedById);
        });

        modelBuilder.Entity<CabVote>(vote =>
        {
            vote.ToTable("cab_votes", table => table.HasCheckConstraint("ck_cab_votes_choice", EnumCheck("choice", Enum.GetNames<CabVoteChoice>())));
            vote.HasKey(v => new { v.ItemId, v.UserId });
            vote.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            vote.HasOne<CabAgendaItem>().WithMany().HasForeignKey(v => v.ItemId).OnDelete(DeleteBehavior.Cascade);
            vote.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
            vote.Property(v => v.Choice).HasConversion<string>().HasMaxLength(20);
            vote.Property(v => v.Comment).HasMaxLength(CabVote.CommentMaxLength);
            vote.HasIndex(v => new { v.TenantId, v.UserId });
        });

        modelBuilder.Entity<ChangeLink>(link =>
        {
            link.ToTable("change_links", table =>
            {
                table.HasCheckConstraint("ck_change_links_kind", EnumCheck("kind", Enum.GetNames<ChangeLinkKind>()));
                table.HasCheckConstraint("ck_change_links_not_self", "change_id <> ticket_id");
            });
            link.HasKey(l => new { l.ChangeId, l.TicketId });
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.ChangeId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.TicketId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.LinkedById).OnDelete(DeleteBehavior.SetNull);
            link.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            link.HasIndex(l => new { l.TenantId, l.TicketId });
        });
    }
}
