using System.Globalization;
using Microsoft.Extensions.Configuration;
using Zelvo.Integrations;

namespace Zelvo.Channels;

/// <summary>Installation settings of email and chat (configuration section <c>Channels</c>), set by the operator.</summary>
public sealed class ChannelOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Channels";

    /// <summary>How often each mailbox is read (no push: many installations are offline).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the worker looks for mailboxes and replies that are due.</summary>
    public TimeSpan WorkInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Most emails read from one mailbox per read.</summary>
    public int MaxEmailsPerRead { get; set; } = 25;

    /// <summary>Largest email Zelvo reads (bigger ones are logged as rejected and marked read).</summary>
    public long MaxEmailBytes { get; set; } = 40L * 1024 * 1024;

    /// <summary>Waits between attempts to send a reply; then it is given up.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    /// <summary>How long the mailbox log keeps an email.</summary>
    public TimeSpan InboundLogRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long sent or given-up replies are kept.</summary>
    public TimeSpan OutboundRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Timeout of one connection to a mail server or provider.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A chat whose visitor has not written or looked for this long ends on its own.</summary>
    public TimeSpan ChatIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Address of the web app (OAuth sign-in redirect, links in replies).</summary>
    public Uri WebUrl { get; set; } = new("http://localhost:3000/");

    /// <summary>Microsoft identity platform (sign-in and tokens).</summary>
    public Uri MicrosoftLoginUrl { get; set; } = new("https://login.microsoftonline.com/");

    /// <summary>Microsoft Graph.</summary>
    public Uri MicrosoftGraphUrl { get; set; } = new("https://graph.microsoft.com/");

    /// <summary>Google sign-in.</summary>
    public Uri GoogleAuthUrl { get; set; } = new("https://accounts.google.com/o/oauth2/v2/auth");

    /// <summary>Google tokens (also the audience of service-account assertions).</summary>
    public Uri GoogleTokenUrl { get; set; } = new("https://oauth2.googleapis.com/token");

    /// <summary>Gmail API.</summary>
    public Uri GmailUrl { get; set; } = new("https://gmail.googleapis.com/");

    /// <summary>
    /// What mail connections may reach. IMAP and SMTP servers are often on the customer's own
    /// network: list those networks in <c>Channels:Network:AllowedPrivateNetworks</c>.
    /// </summary>
    public OutboundNetworkOptions Network { get; } = new();

    /// <summary>
    /// Task E3: the installation's public address. Providers (Meta, Twilio, Microsoft) send
    /// incoming messages to <c>{PublicUrl}hooks/{tenant}/…</c>, which the reverse proxy passes to the
    /// API; Twilio's signatures are checked against it. Defaults to <see cref="WebUrl"/>.
    /// </summary>
    public Uri PublicUrl { get; set; } = new("http://localhost:3000/");

    /// <summary>Meta's Graph API for WhatsApp, with its version.</summary>
    public Uri WhatsAppGraphUrl { get; set; } = new("https://graph.facebook.com/v23.0/");

    /// <summary>Twilio's REST API.</summary>
    public Uri TwilioUrl { get; set; } = new("https://api.twilio.com/");

    /// <summary>Microsoft's OpenID metadata for the tokens Bot Framework signs (Teams).</summary>
    public Uri BotFrameworkOpenIdUrl { get; set; } = new("https://login.botframework.com/v1/.well-known/openidconfiguration");

    /// <summary>Issuer of Bot Framework's tokens.</summary>
    public string BotFrameworkIssuer { get; set; } = "https://api.botframework.com";

    /// <summary>Hosts Teams conversations may be answered on (the activity's service address must end with one).</summary>
    public IReadOnlyList<string> TeamsServiceHosts { get; set; } = ["smba.trafficmanager.net", "botframework.com", "botframework.azure.us", "teams.microsoft.com"];

    /// <summary>Largest voice note or file taken from a message (WhatsApp allows 16 MB for audio).</summary>
    public long MaxMediaBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>How long the channel log is kept.</summary>
    public TimeSpan MessageRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Reads the section; unset values keep their defaults.</summary>
    public static ChannelOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var options = new ChannelOptions();
        options.PollInterval = Time(section["PollInterval"]) ?? options.PollInterval;
        options.WorkInterval = Time(section["WorkInterval"]) ?? options.WorkInterval;
        options.MaxEmailsPerRead = Int(section["MaxEmailsPerRead"]) ?? options.MaxEmailsPerRead;
        options.InboundLogRetention = Time(section["InboundLogRetention"]) ?? options.InboundLogRetention;
        options.OutboundRetention = Time(section["OutboundRetention"]) ?? options.OutboundRetention;
        options.Timeout = Time(section["Timeout"]) ?? options.Timeout;
        options.ChatIdleTimeout = Time(section["ChatIdleTimeout"]) ?? options.ChatIdleTimeout;
        options.WebUrl = BaseUrl(section["WebUrl"] ?? configuration["Notifications:WebUrl"]) ?? options.WebUrl;
        options.MicrosoftLoginUrl = BaseUrl(section["MicrosoftLoginUrl"]) ?? options.MicrosoftLoginUrl;
        options.MicrosoftGraphUrl = BaseUrl(section["MicrosoftGraphUrl"]) ?? options.MicrosoftGraphUrl;
        options.GoogleAuthUrl = Url(section["GoogleAuthUrl"]) ?? options.GoogleAuthUrl;
        options.GoogleTokenUrl = Url(section["GoogleTokenUrl"]) ?? options.GoogleTokenUrl;
        options.GmailUrl = BaseUrl(section["GmailUrl"]) ?? options.GmailUrl;
        OutboundNetworkOptions.Read(section.GetSection("Network"), options.Network);
        options.PublicUrl = BaseUrl(section["PublicUrl"]) ?? options.WebUrl;
        options.WhatsAppGraphUrl = BaseUrl(section["WhatsAppGraphUrl"]) ?? options.WhatsAppGraphUrl;
        options.TwilioUrl = BaseUrl(section["TwilioUrl"]) ?? options.TwilioUrl;
        options.BotFrameworkOpenIdUrl = Url(section["BotFrameworkOpenIdUrl"]) ?? options.BotFrameworkOpenIdUrl;
        options.BotFrameworkIssuer = string.IsNullOrWhiteSpace(section["BotFrameworkIssuer"]) ? options.BotFrameworkIssuer : section["BotFrameworkIssuer"]!.Trim();
        var hosts = section.GetSection("TeamsServiceHosts").GetChildren().Select(c => c.Value?.Trim().ToLowerInvariant()).Where(h => !string.IsNullOrEmpty(h)).ToList();
        if (hosts.Count > 0)
        {
            options.TeamsServiceHosts = hosts!;
        }

        options.MessageRetention = Time(section["MessageRetention"]) ?? options.MessageRetention;
        return options;
    }

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) && time > TimeSpan.Zero ? time : null;

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private static Uri? Url(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) ? url : null;

    /// <summary>A base address, ending with a slash so relative paths add to it.</summary>
    private static Uri? BaseUrl(string? value) =>
        Url(value) is { } url ? (url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/")) : null;
}
