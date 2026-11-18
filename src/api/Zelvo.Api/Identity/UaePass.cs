using Zelvo.Api.Settings;

namespace Zelvo.Api.Identity;

/// <summary>Which UAE PASS system a tenant signs in with (GOV-1).</summary>
public enum UaePassEnvironment
{
    /// <summary>UAE PASS staging (<c>stg-id.uaepass.ae</c>), for testing with staging accounts.</summary>
    Staging,

    /// <summary>UAE PASS production (<c>id.uaepass.ae</c>).</summary>
    Production,

    /// <summary>The development mock in Docker Compose. Offered only when the installation configures it.</summary>
    Mock,
}

/// <summary>
/// UAE PASS account levels (docs.uaepass.ae, User Account Types), lowest first: SOP1 basic
/// (identity not verified), SOP2 advanced and SOP3 qualified (identity verified).
/// </summary>
public enum UaePassLevel
{
    /// <summary>Basic account: email and mobile verified, identity not verified.</summary>
    SOP1 = 1,

    /// <summary>Advanced account: identity verified.</summary>
    SOP2 = 2,

    /// <summary>Qualified account: identity verified with biometrics.</summary>
    SOP3 = 3,
}

/// <summary>Installation settings for UAE PASS (section <c>Identity:UaePass</c>).</summary>
internal sealed class UaePassOptions
{
    public const string Section = "Identity:UaePass";

    /// <summary>Development only: the UAE PASS mock. Empty in installs, so "Mock" cannot be chosen.</summary>
    public MockOptions Mock { get; set; } = new();

    internal sealed class MockOptions
    {
        /// <summary>The mock as browsers see it (sign-in and sign-out pages).</summary>
        public string? PublicUrl { get; set; }

        /// <summary>The mock as Keycloak's server sees it (token and user information).</summary>
        public string? InternalUrl { get; set; }
    }

    public bool MockAvailable => !string.IsNullOrWhiteSpace(Mock.PublicUrl);

    /// <summary>The environments an administrator may choose on this installation.</summary>
    public IReadOnlyList<UaePassEnvironment> Environments =>
        MockAvailable ? [UaePassEnvironment.Staging, UaePassEnvironment.Production, UaePassEnvironment.Mock]
            : [UaePassEnvironment.Staging, UaePassEnvironment.Production];

    /// <summary>
    /// UAE PASS addresses (docs.uaepass.ae, Web Integration → Endpoints). They are fixed by the
    /// UAE PASS programme, so they are code, not settings; only the environment is chosen.
    /// </summary>
    public UaePassEndpoints EndpointsFor(UaePassEnvironment environment) => environment switch
    {
        UaePassEnvironment.Staging => UaePassEndpoints.At("https://stg-id.uaepass.ae", "https://stg-id.uaepass.ae"),
        UaePassEnvironment.Production => UaePassEndpoints.At("https://id.uaepass.ae", "https://id.uaepass.ae"),
        UaePassEnvironment.Mock when MockAvailable => UaePassEndpoints.At(
            Mock.PublicUrl!, string.IsNullOrWhiteSpace(Mock.InternalUrl) ? Mock.PublicUrl! : Mock.InternalUrl),
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Not available on this installation."),
    };
}

/// <summary>Where UAE PASS signs people in, issues tokens, gives user information and signs out.</summary>
internal sealed record UaePassEndpoints(string Authorization, string Token, string UserInfo, string Logout)
{
    /// <summary>
    /// Web sign-in asks for the "low" level, the value UAE PASS documents for web integration; the
    /// level the person reached is the profile's <c>userType</c> (SOP1, SOP2 or SOP3).
    /// </summary>
    public const string AcrValues = "urn:safelayer:tws:policies:authentication:level:low";

    /// <summary>Scope of the citizen and resident profile.</summary>
    public const string Scope = "urn:uae:digitalid:profile:general";

    public static UaePassEndpoints At(string browserBase, string serverBase)
    {
        var browser = browserBase.TrimEnd('/');
        var server = serverBase.TrimEnd('/');
        return new($"{browser}/idshub/authorize", $"{server}/idshub/token", $"{server}/idshub/userinfo", $"{browser}/idshub/logout");
    }
}

/// <summary>
/// The tenant's UAE PASS settings (Settings → UAE PASS). The client secret is not among them: it
/// goes straight to the tenant's Keycloak realm, which uses it, and Zelvo keeps only when it last
/// changed, so that the change is audited without the value.
/// </summary>
internal static class UaePassSettings
{
    /// <summary>Longest client ID accepted.</summary>
    public const int MaxClientIdLength = 200;

    /// <summary>Longest client secret accepted.</summary>
    public const int MaxClientSecretLength = 500;

    public static SettingDefinition<bool> Enabled { get; } = new("identity.uaePass.enabled", false, Permissions.IdentityManage);

    public static SettingDefinition<UaePassEnvironment> Environment { get; } =
        new("identity.uaePass.environment", UaePassEnvironment.Staging, Permissions.IdentityManage, Enum.IsDefined);

    public static SettingDefinition<string> ClientId { get; } =
        new("identity.uaePass.clientId", string.Empty, Permissions.IdentityManage, IsClientId);

    /// <summary>When the client secret was last given; <see cref="DateTimeOffset.MinValue"/> if never.</summary>
    public static SettingDefinition<DateTimeOffset> ClientSecretChangedAt { get; } =
        new("identity.uaePass.clientSecretChangedAt", DateTimeOffset.MinValue, Permissions.IdentityManage);

    /// <summary>GOV-1 setting 1: a verified (SOP2+) UAE PASS sign-in counts as the second factor for staff. Off by default.</summary>
    public static SettingDefinition<bool> AcceptAsSecondFactor { get; } =
        new("identity.uaePass.acceptAsSecondFactor", false, Permissions.IdentityManage);

    /// <summary>GOV-1 setting 2: the lowest UAE PASS level requesters may sign in with. SOP1 by default.</summary>
    public static SettingDefinition<UaePassLevel> RequesterMinimumLevel { get; } =
        new("identity.uaePass.requesterMinimumLevel", UaePassLevel.SOP1, Permissions.IdentityManage, Enum.IsDefined);

    /// <summary>GOV-1 setting 3: a first UAE PASS sign-in without an account creates a requester account. On by default.</summary>
    public static SettingDefinition<bool> CreateRequesters { get; } =
        new("identity.uaePass.createRequesters", true, Permissions.IdentityManage);

    /// <summary>Staff always need a verified UAE PASS account; not configurable (product owner decision 2026-11-18).</summary>
    public const UaePassLevel StaffMinimumLevel = UaePassLevel.SOP2;

    public static IReadOnlyList<SettingDefinition> All { get; } =
        [Enabled, Environment, ClientId, ClientSecretChangedAt, AcceptAsSecondFactor, RequesterMinimumLevel, CreateRequesters];

    /// <summary>A level name from a token claim; missing or unknown counts as the lowest, SOP1.</summary>
    public static UaePassLevel ParseLevel(string? value) =>
        Enum.TryParse<UaePassLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : UaePassLevel.SOP1;

    /// <summary>Printable characters without spaces; empty means "not set yet".</summary>
    public static bool IsClientId(string? value) =>
        value is not null && value.Length <= MaxClientIdLength && value.All(c => c > ' ' && c < '\u007f');

    public static bool IsClientSecret(string value) =>
        value.Length is > 0 and <= MaxClientSecretLength && value.All(c => c >= ' ' && c < '\u007f');
}

/// <summary>
/// What the API sets on the tenant realm's <c>uaepass</c> identity provider.
/// </summary>
/// <param name="Enabled">Whether the sign-in page offers UAE PASS.</param>
/// <param name="Endpoints">UAE PASS addresses of the chosen environment.</param>
/// <param name="ClientId">Client ID issued by the UAE PASS programme.</param>
/// <param name="ClientSecret">New client secret, or <see langword="null"/> to keep the one Keycloak has.</param>
/// <param name="CreateRequesters">Whether a first sign-in without an account creates a requester account.</param>
/// <param name="RequesterMinimumLevel">Lowest level a new requester account may have.</param>
internal sealed record UaePassProvider(
    bool Enabled, UaePassEndpoints Endpoints, string ClientId, string? ClientSecret,
    bool CreateRequesters = true, UaePassLevel RequesterMinimumLevel = UaePassLevel.SOP1)
{
    /// <summary>Alias of the identity provider in every tenant realm; part of the redirect URI registered with UAE PASS.</summary>
    public const string Alias = "uaepass";

    /// <summary>
    /// First sign-in with UAE PASS: linked by Emirates ID, linked to the account with the same email
    /// after proving it is yours, or a new requester account (Zelvo extension step <see cref="FirstLoginStep"/>).
    /// </summary>
    public const string FirstLoginFlow = "zelvo uaepass first sign-in";

    /// <summary>The Zelvo Keycloak extension's first-login step; Zelvo sets its configuration.</summary>
    public const string FirstLoginStep = "zelvo-uaepass-first-login";

    /// <summary>The Zelvo Keycloak extension's mapper that keeps the UAE PASS level of each sign-in.</summary>
    public const string ProfileMapper = "zelvo-uaepass-profile";

    /// <summary>User session note with the UAE PASS level, set by <see cref="ProfileMapper"/>.</summary>
    public const string LevelSessionNote = "zelvo.uaepass.level";

    /// <summary>Keycloak user attribute with the Emirates ID an administrator recorded.</summary>
    public const string EmiratesIdAttribute = "zelvo_emirates_id";

    /// <summary>After every UAE PASS sign-in: counts as the first factor; asks for the second when the client asks for <c>acr=mfa</c>.</summary>
    public const string AfterLoginFlow = "zelvo uaepass after login";
}
