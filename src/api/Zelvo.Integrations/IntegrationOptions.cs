using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Zelvo.Integrations;

/// <summary>
/// Installation settings for integrations (configuration section <c>Integrations</c>). These are
/// set by the operator of the installation, not by tenant administrators, because they decide
/// what Zelvo's servers may connect to.
/// </summary>
public sealed class IntegrationOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Integrations";

    /// <summary>How long integration log entries and finished webhook deliveries are kept.</summary>
    public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Webhook delivery settings.</summary>
    public WebhookOptions Webhooks { get; set; } = new();

    /// <summary>Reads the section; invalid values keep the defaults, invalid networks throw.</summary>
    public static IntegrationOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var options = new IntegrationOptions();
        options.LogRetention = Time(section["LogRetention"]) is { } retention && retention >= TimeSpan.FromDays(1) ? retention : options.LogRetention;

        var webhooks = section.GetSection("Webhooks");
        var w = options.Webhooks;
        w.Timeout = Time(webhooks["Timeout"]) is { } timeout && timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(60) ? timeout : w.Timeout;
        w.PollInterval = Time(webhooks["PollInterval"]) is { } poll && poll > TimeSpan.Zero ? poll : w.PollInterval;
        w.DisableAfterFailures = Int(webhooks["DisableAfterFailures"]) is { } failures and > 0 ? failures : w.DisableAfterFailures;
        w.Concurrency = Int(webhooks["Concurrency"]) is { } concurrency and > 0 and <= 64 ? concurrency : w.Concurrency;
        if (webhooks.GetSection("RetryDelays").GetChildren().Select(c => Time(c.Value)).ToList() is { Count: > 0 } delays)
        {
            if (delays.Any(d => d is null || d < TimeSpan.Zero))
            {
                throw new InvalidOperationException("Integrations:Webhooks:RetryDelays must be time spans such as 00:05:00.");
            }

            w.RetryDelays = [.. delays.Select(d => d!.Value)];
        }

        OutboundNetworkOptions.Read(webhooks, w);

        return options;
    }

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : null;

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// What outgoing requests of one kind (webhooks, AI providers) may reach; read by
/// <see cref="OutboundAddressPolicy"/>. Set by the installation operator.
/// </summary>
public class OutboundNetworkOptions
{
    /// <summary>
    /// Allow <c>http://</c> addresses. Off: HTTPS only. Development Compose turns it on for its
    /// local test services; installations should leave it off.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Private networks (for example <c>10.20.0.0/16</c>) that may be reached, for services on
    /// the customer's internal network. Loopback, link-local and cloud metadata addresses are never
    /// allowed, whatever is listed here.
    /// </summary>
    public List<IPNetwork> AllowedPrivateNetworks { get; } = [];

    /// <summary>
    /// Reads <c>AllowInsecureHttp</c> and <c>AllowedPrivateNetworks</c> of <paramref name="section"/>
    /// into <paramref name="options"/>; entries that are not addresses or networks throw.
    /// </summary>
    public static void Read(IConfigurationSection section, OutboundNetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);
        options.AllowInsecureHttp = bool.TryParse(section["AllowInsecureHttp"], out var insecure) ? insecure : options.AllowInsecureHttp;
        foreach (var entry in section.GetSection("AllowedPrivateNetworks").GetChildren().Select(c => c.Value?.Trim()))
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            if (IPNetwork.TryParse(entry, out var network))
            {
                options.AllowedPrivateNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.AllowedPrivateNetworks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"{section.Path}:AllowedPrivateNetworks has an entry that is not an address or network: '{entry}'.");
            }
        }
    }
}

/// <summary>Webhook delivery settings (<c>Integrations:Webhooks</c>).</summary>
public sealed class WebhookOptions : OutboundNetworkOptions
{

    /// <summary>Time a receiver has to answer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits before each retry; the number of entries is the number of retries. Default: 1 and 5
    /// minutes, then 30 minutes, 2, 5, 10 and 10 hours (8 attempts over about 28 hours).
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
        TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10),
    ];

    /// <summary>A subscription is disabled after this many deliveries in a row failed for good.</summary>
    public int DisableAfterFailures { get; set; } = 5;

    /// <summary>How often the worker looks for due deliveries.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Deliveries sent at the same time by one worker.</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>How long a previous signing secret keeps being used after a rotation.</summary>
    public TimeSpan SecretRotationOverlap { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Attempts in total: the first one plus one per retry delay.</summary>
    public int MaxAttempts => RetryDelays.Count + 1;
}
