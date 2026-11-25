using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Channels;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>WhatsApp, SMS and Microsoft Teams (module 6, task E3).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>WhatsApp numbers, SMS gateways and Teams apps.</summary>
    public DbSet<MessagingChannel> MessagingChannels => Set<MessagingChannel>();

    /// <summary>People on those channels.</summary>
    public DbSet<MessagingContact> MessagingContacts => Set<MessagingContact>();

    /// <summary>Channel log and outgoing queue.</summary>
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();

    /// <summary>Voice notes and their transcripts.</summary>
    public DbSet<VoiceNote> VoiceNotes => Set<VoiceNote>();

    private static void ConfigureMessaging(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessagingChannel>(channel =>
        {
            channel.ToTable("messaging_channels", table =>
            {
                table.HasCheckConstraint("ck_messaging_channels_kind", EnumCheck("kind", Enum.GetNames<MessagingKind>()));
                table.HasCheckConstraint("ck_messaging_channels_provider", EnumCheck("provider", Enum.GetNames<MessagingProvider>()));
                table.HasCheckConstraint("ck_messaging_channels_new_senders", EnumCheck("new_senders", Enum.GetNames<MessagingSenders>()));
                // WhatsApp through Meta, SMS through Twilio or a gateway, Teams through its bot.
                table.HasCheckConstraint("ck_messaging_channels_kind_provider",
                    "(kind = 'WhatsApp' AND provider = 'WhatsAppCloud') OR (kind = 'Sms' AND provider IN ('Twilio', 'GenericHttp')) OR (kind = 'Teams' AND provider = 'TeamsBot')");
            });
            channel.HasKey(c => c.Id);
            channel.Property(c => c.Id).ValueGeneratedNever();
            channel.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            channel.HasOne<Category>().WithMany().HasForeignKey(c => c.CategoryId).OnDelete(DeleteBehavior.Restrict);
            channel.HasOne<SupportGroup>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Restrict);
            channel.Property(c => c.Name).HasMaxLength(MessagingChannel.NameMaxLength);
            channel.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            channel.Property(c => c.Provider).HasConversion<string>().HasMaxLength(20);
            channel.Property(c => c.NewSenders).HasConversion<string>().HasMaxLength(20);
            channel.Property(c => c.AccountId).HasMaxLength(MessagingChannel.IdentifierMaxLength);
            channel.Property(c => c.Sender).HasMaxLength(MessagingChannel.IdentifierMaxLength);
            channel.Property(c => c.DirectoryId).HasMaxLength(MessagingChannel.IdentifierMaxLength);
            channel.Property(c => c.Settings).HasColumnType("jsonb");
            channel.Property(c => c.LastError).HasMaxLength(MessagingChannel.ErrorMaxLength);
            channel.Property(c => c.Version).IsRowVersion();
            channel.HasIndex(c => new { c.TenantId, c.Kind });
        });

        modelBuilder.Entity<MessagingContact>(contact =>
        {
            contact.ToTable("messaging_contacts");
            contact.HasKey(c => c.Id);
            contact.Property(c => c.Id).ValueGeneratedNever();
            contact.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            contact.HasOne<MessagingChannel>().WithMany().HasForeignKey(c => c.ChannelId).OnDelete(DeleteBehavior.Cascade);
            contact.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.SetNull);
            // The current ticket is a plain id (tickets point back at their contact).
            contact.Property(c => c.Address).HasMaxLength(MessagingContact.AddressMaxLength);
            contact.Property(c => c.TeamsServiceUrl).HasMaxLength(MessagingContact.TeamsValueMaxLength);
            contact.Property(c => c.TeamsConversationId).HasMaxLength(MessagingContact.TeamsValueMaxLength);
            contact.Property(c => c.TeamsUserId).HasMaxLength(MessagingContact.TeamsValueMaxLength);
            // One contact per person and channel.
            contact.HasIndex(c => new { c.ChannelId, c.Address }).IsUnique();
            contact.HasIndex(c => new { c.TenantId, c.UserId });
        });

        modelBuilder.Entity<ChannelMessage>(message =>
        {
            message.ToTable("channel_messages", table =>
            {
                table.HasCheckConstraint("ck_channel_messages_direction", EnumCheck("direction", Enum.GetNames<MessageDirection>()));
                table.HasCheckConstraint("ck_channel_messages_kind", EnumCheck("kind", Enum.GetNames<ChannelMessageKind>()));
                table.HasCheckConstraint("ck_channel_messages_status", EnumCheck("status", Enum.GetNames<ChannelMessageStatus>()));
                table.HasCheckConstraint("ck_channel_messages_language", "language IS NULL OR language IN ('en', 'ar')");
            });
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).UseIdentityAlwaysColumn();
            message.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            message.HasOne<MessagingChannel>().WithMany().HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Cascade);
            message.HasOne<MessagingContact>().WithMany().HasForeignKey(m => m.ContactId).OnDelete(DeleteBehavior.SetNull);
            message.HasOne<Ticket>().WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.SetNull);
            message.HasOne<TicketComment>().WithMany().HasForeignKey(m => m.CommentId).OnDelete(DeleteBehavior.NoAction);
            message.HasOne<Notification>().WithMany().HasForeignKey(m => m.NotificationId).OnDelete(DeleteBehavior.SetNull);
            message.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10);
            message.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
            message.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            message.Property(m => m.ProviderMessageId).HasMaxLength(ChannelMessage.ProviderIdMaxLength);
            message.Property(m => m.Address).HasMaxLength(MessagingContact.AddressMaxLength);
            message.Property(m => m.Text).HasMaxLength(ChannelMessage.TextMaxLength);
            message.Property(m => m.Payload).HasColumnType("jsonb");
            message.Property(m => m.Language).HasMaxLength(2);
            message.Property(m => m.Reason).HasMaxLength(ChannelMessage.ReasonMaxLength);
            // Each incoming message is taken once (providers send webhooks again).
            message.HasIndex(m => new { m.ChannelId, m.ProviderMessageId }).IsUnique().HasFilter("direction = 'In' AND provider_message_id IS NOT NULL");
            // Delivery receipts find the outgoing message.
            message.HasIndex(m => new { m.ChannelId, m.ProviderMessageId }).HasFilter("direction = 'Out' AND provider_message_id IS NOT NULL")
                .HasDatabaseName("ix_channel_messages_channel_id_provider_message_id_out");
            message.HasIndex(m => new { m.TenantId, m.Status, m.NextAttemptAt });
            message.HasIndex(m => new { m.TenantId, m.ChannelId, m.CreatedAt });
            message.HasIndex(m => new { m.NotificationId, m.ChannelId }).IsUnique().HasFilter("notification_id IS NOT NULL");
        });

        modelBuilder.Entity<VoiceNote>(note =>
        {
            note.ToTable("voice_notes", table =>
                table.HasCheckConstraint("ck_voice_notes_status", EnumCheck("status", Enum.GetNames<TranscriptStatus>())));
            note.HasKey(v => v.CommentId);
            note.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            note.HasOne<TicketComment>().WithMany().HasForeignKey(v => v.CommentId).OnDelete(DeleteBehavior.Cascade);
            note.HasOne<Ticket>().WithMany().HasForeignKey(v => v.TicketId).OnDelete(DeleteBehavior.Cascade);
            note.HasOne<TicketAttachment>().WithMany().HasForeignKey(v => v.AttachmentId).OnDelete(DeleteBehavior.Cascade);
            note.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            note.Property(v => v.Transcript).HasMaxLength(VoiceNote.TranscriptMaxLength);
            note.Property(v => v.Language).HasMaxLength(2);
            note.HasIndex(v => new { v.TenantId, v.TicketId });
        });

        modelBuilder.Entity<Ticket>().HasOne<MessagingContact>().WithMany().HasForeignKey(t => t.MessagingContactId).OnDelete(DeleteBehavior.SetNull);
    }
}
