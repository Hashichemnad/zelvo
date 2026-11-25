using System.Globalization;
using Microsoft.Extensions.Configuration;
using Zelvo.Integrations;

namespace Zelvo.Connectors;

/// <summary>Installation settings of connectors (section <c>Connectors</c>), set by the operator.</summary>
public sealed class ConnectorOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Connectors";

    /// <summary>How long one call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the worker looks for due syncs and calls.</summary>
    public TimeSpan WorkInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits between attempts of a failed call.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];

    /// <summary>How long the call log is kept.</summary>
    public TimeSpan CallRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Most records read in one sync.</summary>
    public int MaxRecords { get; set; } = 100_000;

    /// <summary>Microsoft's sign-in (Entra ID and Dynamics 365 tokens).</summary>
    public Uri MicrosoftLoginUrl { get; set; } = new("https://login.microsoftonline.com/");

    /// <summary>Microsoft Graph.</summary>
    public Uri GraphUrl { get; set; } = new("https://graph.microsoft.com/");

    /// <summary>HubSpot's API.</summary>
    public Uri HubSpotUrl { get; set; } = new("https://api.hubapi.com/");

    /// <summary>Salesforce REST API version.</summary>
    public string SalesforceApiVersion { get; set; } = "v62.0";

    /// <summary>The web app's address (links to tickets in CRM cases).</summary>
    public Uri WebUrl { get; set; } = new("http://localhost:3000/");

    /// <summary>Networks connected systems may be on (directories are mostly internal), and plain HTTP for tests.</summary>
    public OutboundNetworkOptions Network { get; } = new();

    /// <summary>Reads the section.</summary>
    public static ConnectorOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        var options = new ConnectorOptions();
        options.Timeout = Time(section["Timeout"]) ?? options.Timeout;
        options.WorkInterval = Time(section["WorkInterval"]) ?? options.WorkInterval;
        options.CallRetention = Time(section["CallRetention"]) ?? options.CallRetention;
        options.MaxRecords = int.TryParse(section["MaxRecords"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : options.MaxRecords;
        options.MicrosoftLoginUrl = BaseUrl(section["MicrosoftLoginUrl"]) ?? options.MicrosoftLoginUrl;
        options.GraphUrl = BaseUrl(section["GraphUrl"]) ?? options.GraphUrl;
        options.HubSpotUrl = BaseUrl(section["HubSpotUrl"]) ?? options.HubSpotUrl;
        options.SalesforceApiVersion = section["SalesforceApiVersion"] is { Length: > 0 } version ? version : options.SalesforceApiVersion;
        options.WebUrl = BaseUrl(section["WebUrl"] ?? configuration["Notifications:WebUrl"]) ?? options.WebUrl;
        var delays = section.GetSection("RetryDelays").GetChildren().Select(c => Time(c.Value)).Where(t => t is not null).Select(t => t!.Value).ToList();
        if (delays.Count > 0)
        {
            options.RetryDelays = delays;
        }

        OutboundNetworkOptions.Read(section.GetSection("Network"), options.Network);
        return options;
    }

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero ? time : null;

    private static Uri? BaseUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url)
            ? (url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"))
            : null;
}
