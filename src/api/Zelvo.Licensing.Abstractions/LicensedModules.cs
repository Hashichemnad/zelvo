namespace Zelvo.Licensing;

/// <summary>Module identifiers as numbered in <c>docs/requirements.md</c>.</summary>
public static class LicensedModules
{
    public const string Incidents = "m01-incidents";
    public const string ServiceCatalog = "m02-service-catalog";
    public const string CustomerVoice = "m03-customer-voice";
    public const string Portal = "m04-portal";
    public const string AgentWorkspace = "m05-agent-workspace";
    public const string Omnichannel = "m06-omnichannel";
    public const string KnowledgeBase = "m07-knowledge";
    public const string Sla = "m08-sla";
    public const string Ai = "m09-ai";
    public const string NotificationsReporting = "m10-notifications-reporting";
    public const string Branding = "m11-branding";
    public const string Licensing = "m12-licensing";
    public const string UpdatePackages = "m13-updates";
    public const string Integrations = "m14-integrations";
    public const string Payments = "m15-payments";

    /// <summary>All Phase 1 modules.</summary>
    public static IReadOnlyList<string> Phase1 { get; } =
    [
        Incidents, ServiceCatalog, CustomerVoice, Portal, AgentWorkspace, Omnichannel, KnowledgeBase,
        Sla, Ai, NotificationsReporting, Branding, Licensing, UpdatePackages, Integrations, Payments,
    ];
}
