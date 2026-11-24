using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Notifications;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Channels;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Secrets;

namespace Zelvo.Api.Channels;

/// <summary>
/// Settings → Mailboxes (module 6, task E2): mailboxes that turn emails into tickets, read over
/// IMAP, Microsoft Graph or the Gmail API. Credentials are encrypted and never returned; a change
/// of server, provider or app needs the credential again, so nobody can send it elsewhere. Every
/// change is audited; the mailbox log shows what became of each email, with addresses masked.
/// </summary>
internal static partial class MailboxEndpoints
{
    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(15);

    public static RouteGroupBuilder MapMailboxEndpoints(this RouteGroupBuilder group)
    {
        var mailboxes = group.MapGroup("/mailboxes").WithTags("Channels").RequireTenant();
        mailboxes.MapGet("/", ListAsync)
            .WithName("ListMailboxes")
            .WithSummary("The tenant's mailboxes with their status. Credentials are never returned. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<IReadOnlyList<MailboxResponse>>();
        mailboxes.MapGet("/{id:guid}", GetAsync)
            .WithName("GetMailbox")
            .WithSummary("One mailbox. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxResponse>();
        mailboxes.MapPost("/", CreateAsync)
            .WithName("CreateMailbox")
            .WithSummary("Adds a mailbox: IMAP and SMTP with a password, Microsoft 365 (app with a client secret, or an administrator's sign-in) or Gmail (service account with domain-wide delegation, or an administrator's sign-in); who may raise tickets by email (anyone, allowed domains, known users); category or group of new tickets. The credential is stored encrypted. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        mailboxes.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateMailbox")
            .WithSummary("Changes a mailbox; values left out stay. A new address, server, provider, sign-in method or app needs the credential again. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxResponse>()
            .ProducesValidationProblem();
        mailboxes.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteMailbox")
            .WithSummary("Removes a mailbox and its log. Its tickets stay; replies to them then go out as notifications. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces(StatusCodes.Status204NoContent);
        mailboxes.MapPost("/{id:guid}/test", TestAsync)
            .WithName("TestMailbox")
            .WithSummary("Connects to the mailbox and reads its inbox, without processing anything. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxTestResponse>();
        mailboxes.MapPost("/{id:guid}/check", CheckNowAsync)
            .WithName("CheckMailboxNow")
            .WithSummary("Asks the worker to read the mailbox now. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxResponse>();
        mailboxes.MapGet("/{id:guid}/log", LogAsync)
            .WithName("ListMailboxLog")
            .WithSummary("Emails read from the mailbox, newest first, with what became of each (ticket created, added, follow-up, rejected, ignored, failed) and why; sender addresses masked. Kept 90 days. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<PageResult<InboundEmailResponse>>();
        mailboxes.MapGet("/{id:guid}/outgoing", OutgoingAsync)
            .WithName("ListMailboxOutgoing")
            .WithSummary("Emails sent from the mailbox to requesters (replies and notifications), newest first, addresses masked. Kept 30 days. Needs channels.manage.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<PageResult<OutboundEmailResponse>>();
        mailboxes.MapPost("/{id:guid}/outgoing/{emailId:long}/resend", ResendAsync)
            .WithName("ResendMailboxEmail")
            .WithSummary("Sends an email that failed again. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<OutboundEmailResponse>();
        mailboxes.MapPost("/{id:guid}/sign-in", StartSignInAsync)
            .WithName("StartMailboxSignIn")
            .WithSummary("Microsoft 365 and Gmail with an administrator's sign-in: returns the provider's sign-in page (authorization code with PKCE, 15 minutes). Register the redirect address shown in the mailbox with the app. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxSignInResponse>();
        mailboxes.MapPost("/sign-in/complete", CompleteSignInAsync)
            .WithName("CompleteMailboxSignIn")
            .WithSummary("Finishes the sign-in with the code and state the provider sent back. The account must be the mailbox's own address. Only the refresh token is kept, encrypted. Needs channels.manage; audited.")
            .RequirePermission(Permissions.ChannelsManage)
            .Produces<MailboxResponse>()
            .ProducesValidationProblem();
        return group;
    }

    /// <summary>Where providers send administrators back after signing in (registered with the app).</summary>
    internal static Uri RedirectUri(ChannelOptions options) => new(options.WebUrl, "auth/mailbox-callback");

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, ChannelOptions options, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.Mailboxes.AsNoTracking().OrderBy(m => m.Name).ToListAsync(cancellationToken)).Select(m => MailboxResponse.From(m, options)).ToList());

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, ChannelOptions options, CancellationToken cancellationToken) =>
        await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken) is { } mailbox
            ? TypedResults.Ok(MailboxResponse.From(mailbox, options))
            : NotFound();

    internal static async Task<IResult> CreateAsync(
        MailboxRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ChannelHttp http, CancellationToken cancellationToken)
    {
        var mailbox = new Mailbox { Name = string.Empty, Address = string.Empty };
        if (await ApplyAsync(mailbox, request, db, http, creating: true, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (request.Credential is { Length: > 0 } credential)
        {
            await SetCredentialAsync(mailbox, new MailboxCredential(credential), secrets, audit, cancellationToken);
        }

        db.Mailboxes.Add(mailbox);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return UserEndpoints.Validation("address", "Another mailbox already has this address.");
        }

        return TypedResults.Created($"/api/v1/mailboxes/{mailbox.Id}", MailboxResponse.From(mailbox, http.Options));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, MailboxRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, ChannelHttp http, OAuthTokens tokens,
        CancellationToken cancellationToken)
    {
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailbox is null)
        {
            return NotFound();
        }

        var before = Destination(mailbox);
        if (await ApplyAsync(mailbox, request, db, http, creating: false, cancellationToken) is { } problem)
        {
            return problem;
        }

        // Otherwise anyone allowed to change the server could have the stored credential sent there.
        if (Destination(mailbox) != before)
        {
            if (string.IsNullOrEmpty(request.Credential) && mailbox.CredentialCiphertext is not null)
            {
                return UserEndpoints.Validation("credential", "A new address, server, provider, sign-in method or app needs the credential again.");
            }

            mailbox.SignedInAs = null;
        }

        if (request.Credential is not null)
        {
            if (request.Credential.Length == 0)
            {
                mailbox.CredentialCiphertext = null;
                mailbox.CredentialSetAt = null;
                mailbox.SignedInAs = null;
                audit.Add(AuditCategory.Admin, "channels.mailbox.credential-removed", "mailbox", mailbox.Id.ToString());
            }
            else
            {
                await SetCredentialAsync(mailbox, new MailboxCredential(request.Credential), secrets, audit, cancellationToken);
            }

            tokens.Forget(mailbox.Id);
        }

        mailbox.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return UserEndpoints.Validation("address", "Another mailbox already has this address.");
        }

        return TypedResults.Ok(MailboxResponse.From(mailbox, http.Options));
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, OAuthTokens tokens, CancellationToken cancellationToken)
    {
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailbox is null)
        {
            return NotFound();
        }

        db.Mailboxes.Remove(mailbox);
        await db.SaveChangesAsync(cancellationToken);
        tokens.Forget(id);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> TestAsync(Guid id, ZelvoDbContext db, IMailboxClients clients, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailbox is null)
        {
            return NotFound();
        }

        try
        {
            var credential = await MailboxCredential.ReadAsync(mailbox, secrets, cancellationToken);
            await using var connection = await clients.OpenAsync(mailbox, credential, cancellationToken);
            var unread = await connection.ListUnreadAsync(25, cancellationToken);
            return TypedResults.Ok(new MailboxTestResponse(true, unread.Count, null, null));
        }
        catch (MailboxException ex)
        {
            return TypedResults.Ok(new MailboxTestResponse(false, null, ex.Code, ex.Message));
        }
    }

    internal static async Task<IResult> CheckNowAsync(Guid id, ZelvoDbContext db, ChannelOptions options, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (await db.Mailboxes.Where(m => m.Id == id).ExecuteUpdateAsync(set => set.SetProperty(m => m.NextCheckAt, now), cancellationToken) == 0)
        {
            return NotFound();
        }

        return await GetAsync(id, db, options, cancellationToken);
    }

    internal static async Task<IResult> LogAsync(
        Guid id, [AsParameters] MailboxLogQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Mailboxes.AnyAsync(m => m.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var entries = db.InboundEmails.AsNoTracking().Where(e => e.MailboxId == id);
        if (query.Outcome is { } outcome)
        {
            entries = entries.Where(e => e.Outcome == outcome);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var page = await entries.OrderByDescending(e => e.Id).Skip(query.Skip).Take(query.Size).ToListAsync(cancellationToken);
        var ticketIds = page.Where(e => e.TicketId is not null).Select(e => e.TicketId!.Value).Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        return TypedResults.Ok(new PageResult<InboundEmailResponse>(
            [.. page.Select(e => new InboundEmailResponse(e.Id, e.FromAddress is { } from ? NotificationEndpoints.Mask(from) : null, e.Subject, e.Outcome, e.Reason,
                e.TicketId, e.TicketId is { } t ? numbers.GetValueOrDefault(t) : null, e.Attempts, e.ReceivedAt, e.ProcessedAt))],
            query.Number, query.Size, total));
    }

    internal static async Task<IResult> OutgoingAsync(
        Guid id, [AsParameters] OutgoingQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Mailboxes.AnyAsync(m => m.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var emails = db.OutboundEmails.AsNoTracking().Where(e => e.MailboxId == id);
        if (query.Status is { } status)
        {
            emails = emails.Where(e => e.Status == status);
        }

        var total = await emails.LongCountAsync(cancellationToken);
        var page = await emails.OrderByDescending(e => e.Id).Skip(query.Skip).Take(query.Size).ToListAsync(cancellationToken);
        var ticketIds = page.Select(e => e.TicketId).Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        return TypedResults.Ok(new PageResult<OutboundEmailResponse>([.. page.Select(e => OutboundEmailResponse.From(e, numbers.GetValueOrDefault(e.TicketId)))],
            query.Number, query.Size, total));
    }

    internal static async Task<IResult> ResendAsync(Guid id, long emailId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var email = await db.OutboundEmails.SingleOrDefaultAsync(e => e.Id == emailId && e.MailboxId == id, cancellationToken);
        if (email is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "There is no such email.");
        }

        if (email.Status == OutboundEmailStatus.Failed)
        {
            email.Status = OutboundEmailStatus.Pending;
            email.Attempts = 0;
            email.NextAttemptAt = DateTimeOffset.UtcNow;
            email.LastError = null;
            audit.Add(AuditCategory.Admin, "channels.mailbox.email-resent", "mailbox", id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["emailId"] = emailId });
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(OutboundEmailResponse.From(email, await db.Tickets.AsNoTracking().Where(t => t.Id == email.TicketId).Select(t => t.Number).FirstOrDefaultAsync(cancellationToken)));
    }

    internal static async Task<IResult> StartSignInAsync(
        Guid id, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, OAuthTokens tokens, ChannelOptions options, CancellationToken cancellationToken)
    {
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailbox is null)
        {
            return NotFound();
        }

        if (mailbox.Auth != MailboxAuth.OAuthSignIn || mailbox.Provider == MailboxProvider.Imap)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "sign-in-not-used", "This mailbox does not use an administrator's sign-in.");
        }

        if (mailbox.ClientId is null || mailbox.CredentialCiphertext is null)
        {
            return UserEndpoints.Validation("credential", "Enter the app's client id and client secret first.");
        }

        var (verifier, challenge) = OAuthTokens.Pkce();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var state = $"{mailbox.Id:N}.{secret}";
        var pending = JsonSerializer.Serialize(new PendingSignIn(Hash(state), verifier, DateTimeOffset.UtcNow + SignInWindow));
        mailbox.OAuthPending = await secrets.ProtectAsync(PendingPurpose(mailbox.Id), pending, cancellationToken);
        audit.Add(AuditCategory.Admin, "channels.mailbox.sign-in-started", "mailbox", mailbox.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new MailboxSignInResponse(tokens.AuthorizeUrl(mailbox, RedirectUri(options), state, challenge).AbsoluteUri));
    }

    internal static async Task<IResult> CompleteSignInAsync(
        CompleteMailboxSignInRequest request, ZelvoDbContext db, ISecretProtector secrets, IAuditLog audit, OAuthTokens tokens, ChannelOptions options,
        CancellationToken cancellationToken)
    {
        if (request.State!.Split('.') is not [var idText, _] || !Guid.TryParseExact(idText, "N", out var id))
        {
            return SignInProblem("The sign-in could not be matched to a mailbox. Start it again.");
        }

        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailbox?.OAuthPending is null)
        {
            return SignInProblem("The sign-in could not be matched to a mailbox. Start it again.");
        }

        PendingSignIn? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingSignIn>(await secrets.UnprotectAsync(PendingPurpose(mailbox.Id), mailbox.OAuthPending, cancellationToken));
        }
        catch (Exception ex) when (ex is SecretDecryptionException or JsonException)
        {
            pending = null;
        }

        // One use only: the pending sign-in is gone whatever happens next.
        mailbox.OAuthPending = null;
        if (pending is null || pending.ExpiresAt < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.StateHash), Encoding.ASCII.GetBytes(Hash(request.State))))
        {
            await db.SaveChangesAsync(cancellationToken);
            return SignInProblem("The sign-in expired or does not match. Start it again.");
        }

        try
        {
            var credential = await MailboxCredential.ReadAsync(mailbox, secrets, cancellationToken);
            var (refresh, email) = await tokens.RedeemAsync(mailbox, credential.Secret, request.Code!, pending.Verifier, RedirectUri(options), cancellationToken);
            if (!string.Equals(email, mailbox.Address, StringComparison.OrdinalIgnoreCase))
            {
                await db.SaveChangesAsync(cancellationToken);
                return SignInProblem($"Sign in as {mailbox.Address}; the sign-in was for {email ?? "another account"}.");
            }

            await SetCredentialAsync(mailbox, credential with { RefreshToken = refresh }, secrets, audit, cancellationToken);
            mailbox.SignedInAs = email;
            mailbox.NextCheckAt = DateTimeOffset.UtcNow;
            mailbox.UpdatedAt = DateTimeOffset.UtcNow;
            tokens.Forget(mailbox.Id);
            audit.Add(AuditCategory.Admin, "channels.mailbox.signed-in", "mailbox", mailbox.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(MailboxResponse.From(mailbox, options));
        }
        catch (MailboxException ex)
        {
            await db.SaveChangesAsync(cancellationToken);
            return SignInProblem($"{ex.Code}: {ex.Message}");
        }
    }

    /// <summary>Applies the request to <paramref name="mailbox"/>; returns a problem, or null.</summary>
    private static async Task<IResult?> ApplyAsync(Mailbox mailbox, MailboxRequest request, ZelvoDbContext db, ChannelHttp http, bool creating, CancellationToken cancellationToken)
    {
        if (request.Name is { } name)
        {
            mailbox.Name = name.Trim();
        }
        else if (creating)
        {
            return UserEndpoints.Validation("name", "Give the mailbox a name.");
        }

        if (request.Address is { } address)
        {
            if (!IsAddress(address))
            {
                return UserEndpoints.Validation("address", "Enter the mailbox's email address.");
            }

            mailbox.Address = address.Trim().ToLowerInvariant();
        }
        else if (creating)
        {
            return UserEndpoints.Validation("address", "Enter the mailbox's email address.");
        }

        mailbox.Provider = request.Provider ?? mailbox.Provider;
        mailbox.Auth = request.Auth ?? (creating ? DefaultAuth(mailbox.Provider) : mailbox.Auth);
        if (!Fits(mailbox.Provider, mailbox.Auth))
        {
            return UserEndpoints.Validation("auth", "IMAP uses a password; Microsoft 365 an app's client secret or a sign-in; Gmail a service account or a sign-in.");
        }

        if (request.ImapHost is not null) mailbox.ImapHost = Blank(request.ImapHost);
        if (request.ImapPort is not null) mailbox.ImapPort = request.ImapPort;
        if (request.ImapSecurity is { } imapSecurity) mailbox.ImapSecurity = imapSecurity;
        if (request.SmtpHost is not null) mailbox.SmtpHost = Blank(request.SmtpHost);
        if (request.SmtpPort is not null) mailbox.SmtpPort = request.SmtpPort;
        if (request.SmtpSecurity is { } smtpSecurity) mailbox.SmtpSecurity = smtpSecurity;
        if (request.Username is not null) mailbox.Username = Blank(request.Username);
        if (request.ClientId is not null) mailbox.ClientId = Blank(request.ClientId);
        if (request.DirectoryId is not null) mailbox.DirectoryId = Blank(request.DirectoryId);
        if (request.NewSenders is { } newSenders) mailbox.NewSenders = newSenders;
        if (request.AllowedDomains is { } domains)
        {
            var cleaned = domains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (cleaned.Any(d => !DomainPattern().IsMatch(d)))
            {
                return UserEndpoints.Validation("allowedDomains", "Enter domains such as entity.gov.ae.");
            }

            mailbox.AllowedDomains = cleaned;
        }

        if (request.Status is { } status) mailbox.Status = status;
        if (request.ClearCategory == true) mailbox.CategoryId = null;
        if (request.CategoryId is { } category)
        {
            if (category != mailbox.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
            {
                return UserEndpoints.Validation("categoryId", "Choose an active category.");
            }

            mailbox.CategoryId = category;
        }

        if (request.ClearGroup == true) mailbox.GroupId = null;
        if (request.GroupId is { } groupId)
        {
            if (groupId != mailbox.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
            {
                return UserEndpoints.Validation("groupId", "Choose an active group.");
            }

            mailbox.GroupId = groupId;
        }

        if (mailbox.NewSenders == NewSenderPolicy.AllowedDomains && mailbox.AllowedDomains.Length == 0)
        {
            return UserEndpoints.Validation("allowedDomains", "List at least one domain whose senders may raise tickets.");
        }

        if (mailbox.Provider == MailboxProvider.Imap)
        {
            if ((HostProblem(http, "imapHost", mailbox.ImapHost) ?? HostProblem(http, "smtpHost", mailbox.SmtpHost)) is { } hostProblem)
            {
                return hostProblem;
            }

            if (mailbox.ImapPort is not (>= 1 and <= 65535) || mailbox.SmtpPort is not (>= 1 and <= 65535))
            {
                return UserEndpoints.Validation(mailbox.ImapPort is >= 1 and <= 65535 ? "smtpPort" : "imapPort", "Enter a port from 1 to 65535.");
            }

            if (mailbox.Username is null)
            {
                return UserEndpoints.Validation("username", "Enter the user name for IMAP and SMTP.");
            }
        }
        else if (mailbox.ClientId is null && mailbox.Auth != MailboxAuth.ServiceAccount)
        {
            return UserEndpoints.Validation("clientId", "Enter the app's client id.");
        }
        else if (mailbox.Provider == MailboxProvider.Microsoft365 && mailbox.Auth == MailboxAuth.ClientSecret && mailbox.DirectoryId is null)
        {
            return UserEndpoints.Validation("directoryId", "Enter the directory (tenant) id of the customer's Entra ID.");
        }

        if (mailbox.Auth == MailboxAuth.ServiceAccount && request.Credential is { Length: > 0 } key)
        {
            try
            {
                ServiceAccountKey.Parse(key);
            }
            catch (MailboxException ex)
            {
                return UserEndpoints.Validation("credential", ex.Message);
            }
        }

        return null;
    }

    private static async Task SetCredentialAsync(Mailbox mailbox, MailboxCredential credential, ISecretProtector secrets, IAuditLog audit, CancellationToken cancellationToken)
    {
        mailbox.CredentialCiphertext = await MailboxCredential.ProtectAsync(mailbox, credential, secrets, cancellationToken);
        mailbox.CredentialSetAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "channels.mailbox.credential-changed", "mailbox", mailbox.Id.ToString());
    }

    /// <summary>What the credential is sent to; a change needs the credential again.</summary>
    private static string Destination(Mailbox m) =>
        string.Join('|', m.Provider, m.Auth, m.Address, m.ImapHost, m.ImapPort, m.SmtpHost, m.SmtpPort, m.Username, m.ClientId, m.DirectoryId);

    private static MailboxAuth DefaultAuth(MailboxProvider provider) => provider switch
    {
        MailboxProvider.Microsoft365 => MailboxAuth.ClientSecret,
        MailboxProvider.Gmail => MailboxAuth.ServiceAccount,
        _ => MailboxAuth.Password,
    };

    private static bool Fits(MailboxProvider provider, MailboxAuth auth) => (provider, auth) switch
    {
        (MailboxProvider.Imap, MailboxAuth.Password) => true,
        (MailboxProvider.Microsoft365, MailboxAuth.ClientSecret or MailboxAuth.OAuthSignIn) => true,
        (MailboxProvider.Gmail, MailboxAuth.ServiceAccount or MailboxAuth.OAuthSignIn) => true,
        _ => false,
    };

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? HostProblem(ChannelHttp http, string field, string? host)
    {
        if (host is null || host.Length > Mailbox.HostMaxLength || Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            return UserEndpoints.Validation(field, "Enter the server's host name, for example mail.entity.gov.ae.");
        }

        return IPAddress.TryParse(host, out var literal) && !http.Policy.IsAllowed(literal)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            ? UserEndpoints.Validation(field, "Zelvo may not connect to this address. The installation's operator can allow internal networks (Channels:Network:AllowedPrivateNetworks).")
            : null;
    }

    internal static bool IsAddress(string? value)
    {
        if (value is null || value.Length > Mailbox.AddressMaxLength)
        {
            return false;
        }

        try
        {
            var parsed = new MailAddress(value.Trim());
            return parsed.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PendingPurpose(Guid mailboxId) => $"channels.mailbox:{mailboxId:N}.sign-in";

    private static IResult SignInProblem(string message) =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, "mailbox-sign-in-failed", message);

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "mailbox-not-found", "There is no such mailbox.");

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    private sealed record PendingSignIn(string StateHash, string Verifier, DateTimeOffset ExpiresAt);
}

/// <summary>A new mailbox, or changes to one (values left out stay as they are).</summary>
public sealed class MailboxRequest
{
    /// <summary>Name shown to administrators.</summary>
    [StringLength(Mailbox.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>The mailbox's address; replies come from it.</summary>
    [StringLength(Mailbox.AddressMaxLength)]
    public string? Address { get; init; }

    /// <summary>Imap, Microsoft365 or Gmail.</summary>
    public MailboxProvider? Provider { get; init; }

    /// <summary>Password (IMAP), ClientSecret (Microsoft 365), ServiceAccount (Gmail) or OAuthSignIn (Microsoft 365, Gmail).</summary>
    public MailboxAuth? Auth { get; init; }

    /// <summary>IMAP host.</summary>
    [StringLength(Mailbox.HostMaxLength)]
    public string? ImapHost { get; init; }

    /// <summary>IMAP port (993 for TLS).</summary>
    public int? ImapPort { get; init; }

    /// <summary>IMAP TLS (Auto: TLS on 993, else STARTTLS required).</summary>
    public MailSecurity? ImapSecurity { get; init; }

    /// <summary>SMTP host.</summary>
    [StringLength(Mailbox.HostMaxLength)]
    public string? SmtpHost { get; init; }

    /// <summary>SMTP port (587 or 465).</summary>
    public int? SmtpPort { get; init; }

    /// <summary>SMTP TLS (Auto: TLS on 465, else STARTTLS required).</summary>
    public MailSecurity? SmtpSecurity { get; init; }

    /// <summary>IMAP and SMTP user name.</summary>
    [StringLength(Mailbox.IdentifierMaxLength)]
    public string? Username { get; init; }

    /// <summary>The app's client id (Microsoft 365, sign-ins).</summary>
    [StringLength(Mailbox.IdentifierMaxLength)]
    public string? ClientId { get; init; }

    /// <summary>Microsoft 365: the Entra directory (tenant) id or domain.</summary>
    [StringLength(Mailbox.IdentifierMaxLength)]
    public string? DirectoryId { get; init; }

    /// <summary>Password, client secret or service-account key (JSON); empty removes it. Stored encrypted, never returned.</summary>
    [StringLength(16_000)]
    public string? Credential { get; init; }

    /// <summary>Who may raise tickets by email.</summary>
    public NewSenderPolicy? NewSenders { get; init; }

    /// <summary>With AllowedDomains: domains such as entity.gov.ae.</summary>
    [MaxLength(100)]
    public IReadOnlyList<string>? AllowedDomains { get; init; }

    /// <summary>Category of new tickets.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>True removes the category.</summary>
    public bool? ClearCategory { get; init; }

    /// <summary>Group of new tickets (the assignment rules are then not used).</summary>
    public Guid? GroupId { get; init; }

    /// <summary>True removes the group.</summary>
    public bool? ClearGroup { get; init; }

    /// <summary>Active or Disabled.</summary>
    public MailboxStatus? Status { get; init; }
}

/// <summary>The code and state a provider sent back after an administrator signed in.</summary>
public sealed class CompleteMailboxSignInRequest
{
    /// <summary>The state Zelvo gave the provider.</summary>
    [Required]
    [StringLength(200)]
    public string? State { get; init; }

    /// <summary>The authorization code.</summary>
    [Required]
    [StringLength(4000)]
    public string? Code { get; init; }
}

/// <summary>Filters of the mailbox log.</summary>
public sealed class MailboxLogQuery
{
    /// <summary>Only this outcome.</summary>
    public InboundEmailOutcome? Outcome { get; init; }

    /// <summary>Page number, starting at 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Items per page, 1 to 100.</summary>
    [Range(1, PageQuery.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int Number => Page ?? 1;

    internal int Size => PageSize ?? PageQuery.DefaultPageSize;

    internal int Skip => (Number - 1) * Size;
}

/// <summary>Filters of a mailbox's outgoing emails.</summary>
public sealed class OutgoingQuery
{
    /// <summary>Only this status.</summary>
    public OutboundEmailStatus? Status { get; init; }

    /// <summary>Page number, starting at 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Items per page, 1 to 100.</summary>
    [Range(1, PageQuery.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int Number => Page ?? 1;

    internal int Size => PageSize ?? PageQuery.DefaultPageSize;

    internal int Skip => (Number - 1) * Size;
}

/// <summary>A mailbox as administrators see it (never its credential).</summary>
internal sealed record MailboxResponse(
    Guid Id, string Name, string Address, MailboxProvider Provider, MailboxAuth Auth,
    string? ImapHost, int? ImapPort, MailSecurity ImapSecurity, string? SmtpHost, int? SmtpPort, MailSecurity SmtpSecurity, string? Username,
    string? ClientId, string? DirectoryId, bool CredentialSet, DateTimeOffset? CredentialSetAt, string? SignedInAs, string RedirectUri,
    NewSenderPolicy NewSenders, IReadOnlyList<string> AllowedDomains, Guid? CategoryId, Guid? GroupId, MailboxStatus Status,
    DateTimeOffset? LastCheckedAt, bool? LastCheckSucceeded, string? LastError, DateTimeOffset NextCheckAt, DateTimeOffset UpdatedAt)
{
    public static MailboxResponse From(Mailbox m, ChannelOptions options) => new(
        m.Id, m.Name, m.Address, m.Provider, m.Auth, m.ImapHost, m.ImapPort, m.ImapSecurity, m.SmtpHost, m.SmtpPort, m.SmtpSecurity, m.Username,
        m.ClientId, m.DirectoryId, m.CredentialCiphertext is not null, m.CredentialSetAt, m.SignedInAs, MailboxEndpoints.RedirectUri(options).AbsoluteUri,
        m.NewSenders, m.AllowedDomains, m.CategoryId, m.GroupId, m.Status, m.LastCheckedAt, m.LastCheckSucceeded, m.LastError, m.NextCheckAt, m.UpdatedAt);
}

/// <summary>What a connection test found.</summary>
internal sealed record MailboxTestResponse(bool Succeeded, int? Unread, string? Code, string? Message);

/// <summary>The provider's sign-in page.</summary>
internal sealed record MailboxSignInResponse(string Url);

/// <summary>An email of the mailbox log (sender masked).</summary>
internal sealed record InboundEmailResponse(
    long Id, string? From, string? Subject, InboundEmailOutcome Outcome, string? Reason, Guid? TicketId, string? TicketNumber, int Attempts,
    DateTimeOffset ReceivedAt, DateTimeOffset ProcessedAt);

/// <summary>An email sent from a mailbox (address masked).</summary>
internal sealed record OutboundEmailResponse(
    long Id, string To, string Subject, Guid TicketId, string? TicketNumber, bool Reply, OutboundEmailStatus Status, int Attempts, string? LastError,
    DateTimeOffset? NextAttemptAt, DateTimeOffset CreatedAt, DateTimeOffset? SentAt)
{
    public static OutboundEmailResponse From(OutboundEmail e, string? number) => new(
        e.Id, NotificationEndpoints.Mask(e.ToAddress), e.Subject, e.TicketId, number, e.CommentId is not null, e.Status, e.Attempts, e.LastError,
        e.NextAttemptAt, e.CreatedAt, e.SentAt);
}
