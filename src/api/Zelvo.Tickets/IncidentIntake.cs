using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets.Sla;

namespace Zelvo.Tickets;

/// <summary>An incident raised by a channel (email, chat) rather than by a person in the web app.</summary>
/// <param name="Title">Short summary (cut to the longest title).</param>
/// <param name="Description">What happened.</param>
/// <param name="Channel">Email or Chat.</param>
/// <param name="RequesterId">The person it is for.</param>
/// <param name="CreatedById">The person who raised it, when one did (an agent turning a chat into a ticket).</param>
/// <param name="CategoryId">Category (the mailbox's or the widget's).</param>
/// <param name="GroupId">Group; when set the assignment rules are not used.</param>
/// <param name="AssigneeId">Assignee (a member of <paramref name="GroupId"/>).</param>
/// <param name="MailboxId">The mailbox it came in through.</param>
/// <param name="FollowUpOfId">The closed incident it follows up.</param>
public sealed record IncidentIntakeRequest(
    string Title,
    string? Description,
    TicketChannel Channel,
    Guid RequesterId,
    Guid? CreatedById = null,
    Guid? CategoryId = null,
    Guid? GroupId = null,
    Guid? AssigneeId = null,
    Guid? MailboxId = null,
    Guid? FollowUpOfId = null);

/// <summary>
/// Creates incidents for channels (task E2) exactly as the API does for people: the tenant's number
/// series and priority matrix (medium impact and urgency, as for requesters), the assignment rules
/// (task B2), the search index and the SLA clocks (task B3). Works in the API and in the worker; call
/// inside a transaction (the number and round-robin rows stay locked until it commits). Saving is
/// the caller's.
/// </summary>
public sealed class IncidentIntake(ZelvoDbContext db, ISearchIndexer indexer, SlaTracker sla)
{
    /// <summary>Impact and urgency of incidents raised by channels: the middle level.</summary>
    public const int DefaultLevel = 2;

    /// <summary>Stages a new incident; returns it (not saved yet).</summary>
    public async Task<Ticket> CreateAsync(Guid tenantId, IncidentIntakeRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken)
            .ConfigureAwait(false);
        var prefixes = await TicketSettingKeys.ReadAsync<IReadOnlyDictionary<TicketType, string>>(db, TicketSettingKeys.NumberPrefixes,
            TicketNumbers.DefaultPrefixes, TicketNumbers.AreValid, cancellationToken).ConfigureAwait(false);
        var sequence = await TicketNumbers.NextAsync(db, tenantId, TicketType.Incident, cancellationToken).ConfigureAwait(false);
        var title = Clean(request.Title, Ticket.TitleMaxLength);
        var ticket = new Ticket
        {
            TenantId = tenantId,
            Type = TicketType.Incident,
            Number = TicketNumbers.Format(prefixes[TicketType.Incident], sequence),
            Sequence = sequence,
            Title = title.Length >= 3 ? title : $"{title}...",
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : Clean(request.Description, Ticket.DescriptionMaxLength),
            Channel = request.Channel,
            RequesterId = request.RequesterId,
            CreatedById = request.CreatedById,
            CategoryId = request.CategoryId,
            GroupId = request.GroupId,
            MailboxId = request.MailboxId,
            FollowUpOfId = request.FollowUpOfId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ticket.Incident = new Incident { TicketId = ticket.Id, Impact = DefaultLevel, Urgency = DefaultLevel, Priority = matrix.PriorityOf(DefaultLevel, DefaultLevel) };
        if (request.GroupId is null)
        {
            await RouteAsync(ticket, now, cancellationToken).ConfigureAwait(false);
        }
        else if (request.AssigneeId is { } assignee)
        {
            TicketWorkflow.Assign(ticket, assignee, now);
        }

        db.Tickets.Add(ticket);
        await IndexAsync([ticket], cancellationToken).ConfigureAwait(false);
        await sla.UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
        return ticket;
    }

    /// <summary>Writes the tickets' search documents (with the change; the outbox relays them).</summary>
    public async Task IndexAsync(IEnumerable<Ticket> tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        var staff = await IncidentSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in tickets)
        {
            indexer.Upsert(IncidentSearch.Document(ticket, staff));
        }
    }

    private async Task RouteAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var requesterLanguage = await db.Users.AsNoTracking().Where(u => u.Id == ticket.RequesterId).Select(u => u.PreferredLanguage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var language = requesterLanguage is LocalizedText.English or LocalizedText.Arabic
            ? requesterLanguage
            : await TicketSettingKeys.ReadAsync(db, "localization.defaultLanguage", LocalizedText.English,
                v => v is LocalizedText.English or LocalizedText.Arabic, cancellationToken).ConfigureAwait(false);
        var fallback = await TicketSettingKeys.ReadAsync(db, AssignmentSettingKeys.Default, AssignmentDefault.None, d => Enum.IsDefined(d.Method), cancellationToken)
            .ConfigureAwait(false);
        var decision = await TicketRouter.RouteAsync(db, new RoutingRequest(ticket.CategoryId, ticket.LocationId, language), fallback, now, cancellationToken)
            .ConfigureAwait(false);
        ticket.GroupId = decision.GroupId;
        ticket.RoutingRuleId = decision.Rule?.Id;
        TicketWorkflow.Assign(ticket, decision.AssigneeId, now);
    }

    /// <summary>Text without control characters other than line breaks and tabs, trimmed and cut to <paramref name="max"/>.</summary>
    public static string Clean(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (!char.IsControl(c) || c is '\n' or '\t')
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length <= max ? cleaned : string.Concat(cleaned.AsSpan(0, max - 1), "…");
    }
}

/// <summary>Requester accounts for people who reach Zelvo by email or chat before they ever sign in.</summary>
public static class ContactAccounts
{
    /// <summary>
    /// Prefix of the <see cref="UserAccount.Subject"/> of accounts made for email and chat senders.
    /// They have no Keycloak account yet; the first sign-in with a verified email of the same address
    /// takes the account over (task E2).
    /// </summary>
    public const string SubjectPrefix = "contact:";

    /// <summary>Whether <paramref name="user"/> was made for an email or chat sender and has never signed in.</summary>
    public static bool IsContact(UserAccount user) => user?.Subject.StartsWith(SubjectPrefix, StringComparison.Ordinal) == true;

    /// <summary>The active or disabled account with <paramref name="email"/> (compared without case), if any.</summary>
    public static Task<UserAccount?> FindByEmailAsync(ZelvoDbContext db, string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Case-insensitive, with LIKE's wildcards escaped.
        var pattern = email.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return db.Users.Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .Where(u => u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\"))
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Stages a requester account for <paramref name="email"/> (saved by the caller).</summary>
    public static async Task<UserAccount> CreateAsync(ZelvoDbContext db, string email, string? name, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var requester = await db.Roles.SingleAsync(r => r.Key == BuiltInRoles.Requester, cancellationToken).ConfigureAwait(false);
        var account = new UserAccount
        {
            Subject = $"{SubjectPrefix}{Guid.NewGuid():N}",
            Email = email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(name) ? null : IncidentIntake.Clean(name, UserAccount.DisplayNameMaxLength),
            PreferredLanguage = language is LocalizedText.English or LocalizedText.Arabic ? language : null,
        };
        account.Roles.Add(new UserRole { UserId = account.Id, RoleId = requester.Id });
        db.Users.Add(account);
        return account;
    }
}
