using System.Globalization;
using Microsoft.Extensions.Configuration;
using Zelvo.Integrations;

namespace Zelvo.Ai;

/// <summary>
/// Installation settings for AI (configuration section <c>Ai</c>), set by the operator of the
/// installation, not by tenant administrators: what Zelvo's servers may connect to, time limits
/// and how long the AI log keeps text.
/// </summary>
public sealed class AiOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Ai";

    /// <summary>
    /// Which provider addresses may be reached (<c>Ai:AllowInsecureHttp</c>,
    /// <c>Ai:AllowedPrivateNetworks</c>). A model the customer hosts on its own network needs its
    /// network listed here; loopback, link-local and cloud metadata addresses are never allowed.
    /// </summary>
    public OutboundNetworkOptions Network { get; } = new();

    /// <summary>Time a provider has to answer one call.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the masked prompt and answer of each call are kept in the AI log.</summary>
    public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the worker clears text past <see cref="LogRetention"/>.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Largest provider answer read, in bytes.</summary>
    public int MaxResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>Reads the section; invalid values keep the defaults, invalid networks throw.</summary>
    public static AiOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var options = new AiOptions();
        OutboundNetworkOptions.Read(section, options.Network);
        options.Timeout = Time(section["Timeout"]) is { } timeout && timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(5) ? timeout : options.Timeout;
        options.LogRetention = Time(section["LogRetention"]) is { } retention && retention >= TimeSpan.FromDays(1) ? retention : options.LogRetention;
        options.PurgeInterval = Time(section["PurgeInterval"]) is { } interval && interval > TimeSpan.Zero ? interval : options.PurgeInterval;
        return options;
    }

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : null;
}
