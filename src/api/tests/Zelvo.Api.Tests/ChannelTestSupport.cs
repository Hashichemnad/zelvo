using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Zelvo.Channels;
using Zelvo.Data.Channels;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Mailboxes in memory: emails waiting per mailbox address, what was marked read and what was sent.</summary>
internal sealed class FakeMailboxClients : IMailboxClients
{
    private readonly ConcurrentDictionary<string, List<(string Id, MimeMessage Message, bool Read)>> _inboxes = new(StringComparer.OrdinalIgnoreCase);
    private int _next;

    public ConcurrentQueue<(string From, MimeMessage Message)> Sent { get; } = new();

    /// <summary>When set, connections fail with this error.</summary>
    public MailboxException? Failure { get; set; }

    /// <summary>When set, sending fails with this error.</summary>
    public MailboxException? SendFailure { get; set; }

    public string Deliver(string mailbox, MimeMessage message)
    {
        var id = $"1:{Interlocked.Increment(ref _next)}";
        _inboxes.GetOrAdd(mailbox, _ => []).Add((id, message, false));
        return id;
    }

    public bool IsRead(string mailbox, string id) => _inboxes.TryGetValue(mailbox, out var list) && list.Any(e => e.Id == id && e.Read);

    public MimeMessage[] SentTo(string address) => [.. Sent.Where(s => s.Message.To.Mailboxes.Any(m => m.Address == address)).Select(s => s.Message)];

    public Task<IMailboxConnection> OpenAsync(Mailbox mailbox, MailboxCredential credential, CancellationToken cancellationToken) =>
        Failure is { } failure ? throw failure : Task.FromResult<IMailboxConnection>(new Connection(this, mailbox.Address));

    public Task<string?> SendAsync(Mailbox mailbox, MailboxCredential credential, MimeMessage message, CancellationToken cancellationToken)
    {
        if (SendFailure is { } failure)
        {
            throw failure;
        }

        // The sender disposes its message: keep a copy.
        using var stream = new MemoryStream();
        message.WriteTo(stream, cancellationToken);
        stream.Position = 0;
        Sent.Enqueue((mailbox.Address, MimeMessage.Load(stream, cancellationToken)));
        return Task.FromResult<string?>(null);
    }

    private sealed class Connection(FakeMailboxClients owner, string address) : IMailboxConnection
    {
        public string? RotatedRefreshToken => null;

        private List<(string Id, MimeMessage Message, bool Read)> Inbox => owner._inboxes.GetOrAdd(address, _ => []);

        public Task<IReadOnlyList<string>> ListUnreadAsync(int max, CancellationToken cancellationToken)
        {
            lock (Inbox)
            {
                return Task.FromResult<IReadOnlyList<string>>([.. Inbox.Where(e => !e.Read).Take(max).Select(e => e.Id)]);
            }
        }

        public Task<MimeMessage?> GetAsync(string id, long maxBytes, CancellationToken cancellationToken)
        {
            var message = Inbox.Single(e => e.Id == id).Message;
            using var stream = new MemoryStream();
            message.WriteTo(stream, cancellationToken);
            return Task.FromResult(stream.Length > maxBytes ? null : message);
        }

        public Task MarkReadAsync(string id, CancellationToken cancellationToken)
        {
            lock (Inbox)
            {
                var index = Inbox.FindIndex(e => e.Id == id);
                Inbox[index] = Inbox[index] with { Read = true };
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Email and chat helpers.</summary>
internal static class ChannelApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ApiFactory WithMailboxes(this ApiFactory factory, FakeMailboxClients clients) =>
        factory.WithServices(s =>
        {
            s.RemoveAll<IMailboxClients>();
            s.AddSingleton<IMailboxClients>(clients);
        });

    /// <summary>Runs the worker's mail job once for <paramref name="tenant"/> (mailboxes that are due, then queued emails).</summary>
    public static Task<int> RunMailAsync(this ApiFactory factory, Tenant tenant) =>
        new MailJob(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<ChannelOptions>(), NullLogger<MailJob>.Instance)
            .RunTenantAsync(tenant.Id, tenant.Slug, Ct);

    /// <summary>"Check now" for every mailbox of the tenant, then the worker's mail job once.</summary>
    public static async Task<int> ReadMailboxesAsync(this ApiFactory factory, Tenant tenant)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
            await scope.ServiceProvider.GetRequiredService<Zelvo.Data.ZelvoDbContext>().Mailboxes
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        }

        return await factory.RunMailAsync(tenant);
    }

    /// <summary>A plain email.</summary>
    public static MimeMessage Email(string from, string to, string subject, string text, string? inReplyTo = null, string? fromName = null, string? messageId = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName ?? string.Empty, from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.MessageId = messageId ?? $"{Guid.NewGuid():N}@mail.example.org";
        if (inReplyTo is not null)
        {
            message.InReplyTo = inReplyTo;
            message.References.Add(inReplyTo);
        }

        message.Body = new TextPart("plain") { Text = text };
        return message;
    }

    /// <summary>A password made at run time (tests never hold secret-looking literals).</summary>
    public static string Credential() => $"pw-{Guid.NewGuid():N}";

    public static async Task<Guid> ImapMailboxAsync(this HttpClient admin, string address, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Support",
            ["address"] = address,
            ["provider"] = "Imap",
            ["imapHost"] = "imap.mail.example.org",
            ["imapPort"] = 993,
            ["smtpHost"] = "smtp.mail.example.org",
            ["smtpPort"] = 587,
            ["username"] = address,
            ["credential"] = Credential(),
        };
        foreach (var property in extra?.GetType().GetProperties() ?? [])
        {
            body[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(extra);
        }

        return (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/mailboxes", body, System.Net.HttpStatusCode.Created)).Id();
    }
}
