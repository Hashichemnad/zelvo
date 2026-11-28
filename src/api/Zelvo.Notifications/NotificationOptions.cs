using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Zelvo.Notifications;

/// <summary>How email leaves the installation (product owner decision 2026-11-24: one mail server per installation, set by the operator).</summary>
public enum SmtpSecurity
{
    /// <summary>TLS from the start when the port is 465, else STARTTLS, required (a server without it is refused, so the password never travels in clear; task F7, ASVS V12.1.1).</summary>
    Auto,

    /// <summary>No TLS (only for a relay on the same host or network, such as Mailpit in development).</summary>
    None,

    /// <summary>STARTTLS, required.</summary>
    StartTls,

    /// <summary>TLS from the start (usually port 465).</summary>
    SslOnConnect,
}

/// <summary>The installation's mail server.</summary>
public sealed class SmtpOptions
{
    /// <summary>Host name; empty: no email is sent (in-app notifications still work).</summary>
    public string? Host { get; set; }

    /// <summary>Port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>User name, when the server needs one.</summary>
    public string? User { get; set; }

    /// <summary>Password (from an environment variable or secret, never from a committed file).</summary>
    public string? Password { get; set; }

    /// <summary>TLS.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    /// <summary>Sender address; the sender name is the tenant's name in the email's language.</summary>
    public string From { get; set; } = "no-reply@zelvo.test";

    /// <summary>Timeout of one connection.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a mail server is set.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Installation settings of notifications (configuration section <c>Notifications</c>).</summary>
public sealed class NotificationOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Notifications";

    /// <summary>Address of the web app, for links in emails. Custom domains per tenant come with branding (task F1).</summary>
    public Uri WebUrl { get; set; } = new("http://localhost:3000/");

    /// <summary>The mail server.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>How often the worker looks for work.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Emails sent at the same time.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>Waits between email attempts; the email fails after the last one (about 24 hours in all).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    /// <summary>How long finished emails are kept in the delivery log.</summary>
    public TimeSpan DeliveryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long in-app notifications are kept.</summary>
    public TimeSpan InAppRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Web push (task J2).</summary>
    public PushOptions Push { get; set; } = new();

    /// <summary>Reads the section; values that are not valid keep the defaults.</summary>
    public static NotificationOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var options = new NotificationOptions();
        if (Uri.TryCreate(section["WebUrl"], UriKind.Absolute, out var web) && web.Scheme is "http" or "https")
        {
            options.WebUrl = web.AbsoluteUri.EndsWith('/') ? web : new Uri(web.AbsoluteUri + "/");
        }

        options.PollInterval = Time(section["PollInterval"]) is { } poll && poll > TimeSpan.Zero ? poll : options.PollInterval;
        options.Concurrency = Int(section["Concurrency"]) is { } concurrency and > 0 and <= 32 ? concurrency : options.Concurrency;
        options.DeliveryRetention = Time(section["DeliveryRetention"]) is { } delivery && delivery >= TimeSpan.FromDays(1) ? delivery : options.DeliveryRetention;
        options.InAppRetention = Time(section["InAppRetention"]) is { } inApp && inApp >= TimeSpan.FromDays(1) ? inApp : options.InAppRetention;
        if (section.GetSection("RetryDelays").GetChildren().Select(c => Time(c.Value)).ToList() is { Count: > 0 } delays)
        {
            if (delays.Any(d => d is null || d < TimeSpan.Zero))
            {
                throw new InvalidOperationException("Notifications:RetryDelays must be time spans such as 00:05:00.");
            }

            options.RetryDelays = [.. delays.Select(d => d!.Value)];
        }

        var smtp = section.GetSection("Smtp");
        var s = options.Smtp;
        s.Host = smtp["Host"] is { Length: > 0 } host ? host.Trim() : null;
        s.Port = Int(smtp["Port"]) is { } port and > 0 and < 65536 ? port : s.Port;
        s.User = smtp["User"] is { Length: > 0 } user ? user : null;
        s.Password = smtp["Password"] is { Length: > 0 } password ? password : null;
        s.Security = Enum.TryParse<SmtpSecurity>(smtp["Security"], ignoreCase: true, out var security) ? security : s.Security;
        s.From = smtp["From"] is { Length: > 0 } from ? from.Trim() : s.From;
        s.Timeout = Time(smtp["Timeout"]) is { } timeout && timeout > TimeSpan.Zero ? timeout : s.Timeout;

        var push = section.GetSection("Push");
        var p = options.Push;
        p.Enabled = bool.TryParse(push["Enabled"], out var pushEnabled) ? pushEnabled : p.Enabled;
        p.Subject = push["Subject"] is { Length: > 0 } subject && (subject.StartsWith("mailto:", StringComparison.Ordinal) || subject.StartsWith("https://", StringComparison.Ordinal))
            ? subject.Trim()
            : null;
        p.TimeToLive = Time(push["TimeToLive"]) is { } ttl && ttl >= TimeSpan.Zero && ttl <= TimeSpan.FromDays(28) ? ttl : p.TimeToLive;
        p.Timeout = Time(push["Timeout"]) is { } pushTimeout && pushTimeout > TimeSpan.Zero ? pushTimeout : p.Timeout;
        if (push.GetSection("AllowedHosts").GetChildren().Select(c => c.Value?.Trim().ToLowerInvariant()).Where(h => h is { Length: > 0 }).ToList() is { Count: > 0 } hosts)
        {
            p.AllowedHosts = [.. hosts!];
        }

        Zelvo.Integrations.OutboundNetworkOptions.Read(push, p.Network);
        return options;
    }

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : null;

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// Web push to the browsers' push services (module 19, task J2; product owner decision
/// 2026-11-26: it needs outgoing internet, so offline installations turn it off and get email,
/// in-app and Teams notifications only). Configuration section <c>Notifications:Push</c>.
/// </summary>
public sealed class PushOptions
{
    /// <summary>Whether web push is offered at all (off for installations without outgoing internet).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The VAPID contact (RFC 8292 <c>sub</c>): a <c>mailto:</c> or <c>https:</c> address the push
    /// services may contact. Empty: the web app's address when it is HTTPS, else the sender address.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>How long a push service keeps a message for a device that is offline (RFC 8030 <c>TTL</c>).</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Timeout of one request to a push service.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Push services a browser subscription may point at (the host or a domain it ends with), so a
    /// subscription can never make Zelvo send requests anywhere else: Google (Chrome, Edge on
    /// Android), Mozilla (Firefox), Apple (Safari) and Microsoft (Edge on Windows).
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; set; } =
        ["fcm.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    /// <summary>Addresses push requests may reach (after DNS): public addresses only unless the operator lists private networks.</summary>
    public Zelvo.Integrations.OutboundNetworkOptions Network { get; } = new();

    /// <summary>Whether <paramref name="endpoint"/> is an HTTPS address of an allowed push service.</summary>
    public bool IsAllowedEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (Network.AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        && string.IsNullOrEmpty(uri.UserInfo)
        && AllowedHosts.Any(h => string.Equals(uri.IdnHost, h, StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
