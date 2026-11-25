using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Catalog;

/// <summary>Internal services are handled in Zelvo; external ones link to the provider's site instead of a form.</summary>
public enum ServiceType
{
    /// <summary>Requested in Zelvo with the service's form.</summary>
    Internal,

    /// <summary>Requested on the provider's site; the card shows a support link.</summary>
    External,
}

/// <summary>Where a service can be requested (module 2 service card: delivery channels).</summary>
public enum DeliveryChannel
{
    /// <summary>The self-service portal.</summary>
    Portal,

    /// <summary>Mobile app.</summary>
    Mobile,

    /// <summary>WhatsApp.</summary>
    WhatsApp,

    /// <summary>Live chat.</summary>
    Chat,

    /// <summary>Email.</summary>
    Email,

    /// <summary>Call centre.</summary>
    CallCentre,

    /// <summary>Service centre (in person).</summary>
    ServiceCentre,
}

/// <summary>Who a service is for (module 2 service card: target audience).</summary>
public enum TargetAudience
{
    /// <summary>Individuals.</summary>
    Individuals,

    /// <summary>Businesses.</summary>
    Businesses,

    /// <summary>Government entities.</summary>
    GovernmentEntities,

    /// <summary>Employees.</summary>
    Employees,

    /// <summary>Visitors.</summary>
    Visitors,
}

/// <summary>Who may see and request a service.</summary>
public enum ServiceAccessMode
{
    /// <summary>Everyone signed in.</summary>
    Everyone,

    /// <summary>Everyone, also visitors who are not signed in (they sign in to request).</summary>
    Public,

    /// <summary>Only the groups, roles, departments, locations and users named.</summary>
    Restricted,
}

/// <summary>What kind of partner helps deliver a service.</summary>
public enum PartnerKind
{
    /// <summary>Another department of the organisation (C1 departments).</summary>
    Department,

    /// <summary>Another entity, for example a government authority.</summary>
    Entity,

    /// <summary>A vendor.</summary>
    Vendor,
}

/// <summary>The internal stage a service step maps to, so tracking shows the current step (C4 shows the journey).</summary>
public enum ServiceStepStage
{
    /// <summary>Received; nobody assigned yet.</summary>
    Submitted,

    /// <summary>Waiting for an approval before assignment (task C4).</summary>
    Approval,

    /// <summary>Assigned to someone.</summary>
    Assigned,

    /// <summary>Being worked on.</summary>
    InProgress,

    /// <summary>Waiting, for example for the requester.</summary>
    OnHold,

    /// <summary>Done.</summary>
    Resolved,

    /// <summary>Closed.</summary>
    Closed,
}

/// <summary>A document the requester uploads with a request.</summary>
public sealed record ServiceDocument
{
    /// <summary>Stable key in the card (camelCase).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Name in English and Arabic.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>What it must show.</summary>
    public LocalizedText Description { get; init; } = new(null, null);

    /// <summary>Mandatory or optional.</summary>
    public bool Required { get; init; }

    /// <summary>Allowed file types (extensions, a subset of the tenant's allowlist); empty: the tenant's allowlist.</summary>
    public IReadOnlyList<string> FileTypes { get; init; } = [];

    /// <summary>Largest file in MB; null: the tenant's limit.</summary>
    public int? MaxSizeMb { get; init; }

    /// <summary>A downloadable template (a file of the service).</summary>
    public Guid? TemplateFileId { get; init; }
}

/// <summary>A step of the requester's journey.</summary>
public sealed record ServiceStep
{
    /// <summary>Name in English and Arabic.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>What happens in it.</summary>
    public LocalizedText Description { get; init; } = new(null, null);

    /// <summary>The internal stage it maps to.</summary>
    public ServiceStepStage Stage { get; init; } = ServiceStepStage.InProgress;

    /// <summary>A fulfilment task (task C4) whose start reaches this step, instead of the stage.</summary>
    public string? TaskKey { get; init; }
}

/// <summary>
/// How a service is fulfilled (task C4, module 2): the group that receives new requests first and
/// overrides by form answer, the service's own ownership and completion SLAs with escalations, and
/// the task template.
/// </summary>
public sealed record ServiceFulfilment
{
    /// <summary>The group that receives new requests first; none: the assignment rules decide.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The group's queue or round-robin among its available members.</summary>
    public Data.Directory.AssignmentMethod Method { get; init; } = Data.Directory.AssignmentMethod.GroupQueue;

    /// <summary>Another group when the answers say so (for example Location = Abu Dhabi); the first match wins.</summary>
    public IReadOnlyList<GroupOverride> Overrides { get; init; } = [];

    /// <summary>Time for an agent to take ownership (start work), in business minutes; none: the SLA policy's response target.</summary>
    public int? OwnershipMinutes { get; init; }

    /// <summary>Time to complete, in business minutes; none: to the end of the service's Nth working day (its days), else the policy's.</summary>
    public int? CompletionMinutes { get; init; }

    /// <summary>Escalation thresholds of the service's SLA; none: the SLA policy's.</summary>
    public IReadOnlyList<Data.Catalog.ServiceEscalation> Escalations { get; init; } = [];

    /// <summary>The task template, in order.</summary>
    public IReadOnlyList<FulfilmentTask> Tasks { get; init; } = [];
}

/// <summary>A group chosen by form answers.</summary>
public sealed record GroupOverride
{
    /// <summary>When all hold.</summary>
    public IReadOnlyList<Zelvo.Workflows.Rules.WorkflowCondition> Conditions { get; init; } = [];

    /// <summary>The group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Queue or round-robin.</summary>
    public Data.Directory.AssignmentMethod Method { get; init; } = Data.Directory.AssignmentMethod.GroupQueue;
}

/// <summary>A task of a service's template.</summary>
public sealed record FulfilmentTask
{
    /// <summary>Key, camelCase, unique in the card; steps refer to it.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Name in English and Arabic.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>What to do.</summary>
    public LocalizedText Description { get; init; } = new(null, null);

    /// <summary>The group that does it; none: the request's group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Starts together with the task before it (parallel) instead of after it (sequence).</summary>
    public bool ParallelWithPrevious { get; init; }

    /// <summary>Target time in business minutes from when it starts.</summary>
    public int? TargetMinutes { get; init; }
}

/// <summary>A single service or a bundle of services (task C5).</summary>
public enum ServiceKind
{
    /// <summary>One service, fulfilled by its own group and tasks.</summary>
    Service,

    /// <summary>
    /// Several services requested together with one combined form; each becomes a child request
    /// with its own team, approvals and SLA.
    /// </summary>
    Bundle,
}

/// <summary>The services of a bundle (task C5, module 2), in order.</summary>
public sealed record ServiceBundle
{
    /// <summary>The services, in order.</summary>
    public IReadOnlyList<BundleItem> Items { get; init; } = [];
}

/// <summary>A service in a bundle.</summary>
public sealed record BundleItem
{
    /// <summary>The service (a single, internal service).</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>Mandatory (always requested) or optional (the requester chooses).</summary>
    public bool Required { get; init; } = true;

    /// <summary>Starts together with the service before it instead of after it is complete.</summary>
    public bool ParallelWithPrevious { get; init; }
}

/// <summary>How often a schedule repeats.</summary>
public enum ScheduleUnit
{
    /// <summary>Days.</summary>
    Day,

    /// <summary>Weeks.</summary>
    Week,

    /// <summary>Months (the same day of the month, or the month's last day).</summary>
    Month,

    /// <summary>Years.</summary>
    Year,
}

/// <summary>
/// A proactive service (task C5, module 2): Zelvo offers or starts it without the person asking,
/// when a date comes near, on a schedule, or when another system sends an event through the API.
/// </summary>
public sealed record ServiceProactive
{
    /// <summary>On or off.</summary>
    public bool Enabled { get; init; }

    /// <summary>Notify and ask to confirm (an offer), or start the request automatically.</summary>
    public Data.Catalog.ProactiveMode Mode { get; init; } = Data.Catalog.ProactiveMode.Offer;

    /// <summary>Days an offer stays open.</summary>
    public int OfferDays { get; init; } = 14;

    /// <summary>What starts it.</summary>
    public IReadOnlyList<ProactiveTrigger> Triggers { get; init; } = [];
}

/// <summary>One trigger of a proactive service.</summary>
public sealed record ProactiveTrigger
{
    /// <summary>Key, camelCase, unique in the card; other systems name it in their events.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Date, schedule or event.</summary>
    public Data.Catalog.ProactiveTriggerKind Kind { get; init; } = Data.Catalog.ProactiveTriggerKind.Event;

    /// <summary>Date: the service whose completed requests hold the date (for example a trade licence); none: dates come through the API only.</summary>
    public Guid? SourceServiceId { get; init; }

    /// <summary>Date: the date field of that service's form (for example <c>expiryDate</c>).</summary>
    public string? SourceFieldKey { get; init; }

    /// <summary>Date: days after the date (negative: before it, for example -30).</summary>
    public int OffsetDays { get; init; }

    /// <summary>Schedule: the first occurrence.</summary>
    public DateOnly? StartOn { get; init; }

    /// <summary>Schedule: repeats every this many units.</summary>
    public int Every { get; init; } = 1;

    /// <summary>Schedule: the unit.</summary>
    public ScheduleUnit Unit { get; init; } = ScheduleUnit.Year;

    /// <summary>Schedule: who it is for.</summary>
    public ProactivePeople People { get; init; } = new();
}

/// <summary>The people a scheduled proactive service is for: members of groups, holders of roles, people of departments, named users.</summary>
public sealed record ProactivePeople
{
    /// <summary>Support groups whose members.</summary>
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];

    /// <summary>Roles whose holders.</summary>
    public IReadOnlyList<string> RoleKeys { get; init; } = [];

    /// <summary>Departments whose people.</summary>
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];

    /// <summary>Named users.</summary>
    public IReadOnlyList<Guid> UserIds { get; init; } = [];

    /// <summary>How many entries name people.</summary>
    public int Count => GroupIds.Count + RoleKeys.Count + DepartmentIds.Count + UserIds.Count;
}

/// <summary>Fee of a service in AED (payments come with module 15).</summary>
public sealed record ServiceFee
{
    /// <summary>Free or paid.</summary>
    public bool Paid { get; init; }

    /// <summary>Amount in AED when paid.</summary>
    public decimal? AmountAed { get; init; }

    /// <summary>Notes, for example "urgent delivery costs extra".</summary>
    public LocalizedText Notes { get; init; } = new(null, null);

    /// <summary>When it is paid online (task E8): on submission, after approval or on completion.</summary>
    public Data.Payments.PaymentTiming Timing { get; init; } = Data.Payments.PaymentTiming.OnSubmission;

    /// <summary>The gateway (task E8); none: the tenant's default gateway.</summary>
    public Guid? GatewayId { get; init; }

    /// <summary>Amounts added when the form answers say so (task E8), for example urgent delivery.</summary>
    public IReadOnlyList<FeeRule> Rules { get; init; } = [];
}

/// <summary>An amount added to a service's fee when the answers match (task E8).</summary>
public sealed record FeeRule
{
    /// <summary>What it is, for the receipt.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>When all hold (C3 conditions, usually on answers).</summary>
    public IReadOnlyList<Zelvo.Workflows.Rules.WorkflowCondition> Conditions { get; init; } = [];

    /// <summary>Amount in AED added.</summary>
    public decimal AmountAed { get; init; }
}

/// <summary>Another department, entity or vendor involved in delivering the service.</summary>
public sealed record ServicePartner
{
    /// <summary>Kind of partner.</summary>
    public PartnerKind Kind { get; init; } = PartnerKind.Entity;

    /// <summary>The department, for department partners.</summary>
    public Guid? DepartmentId { get; init; }

    /// <summary>The name of entities and vendors.</summary>
    public LocalizedText Name { get; init; } = new(null, null);
}

/// <summary>A question and answer of the service FAQ.</summary>
public sealed record ServiceFaq
{
    /// <summary>Question in English and Arabic.</summary>
    public LocalizedText Question { get; init; } = new(null, null);

    /// <summary>Answer in English and Arabic (plain text with line breaks).</summary>
    public LocalizedText Answer { get; init; } = new(null, null);
}

/// <summary>Who may see and request a service. Hidden services are left out of search, the catalog, the API and the virtual agent.</summary>
public sealed record ServiceAccess
{
    /// <summary>Everyone signed in, public or restricted.</summary>
    public ServiceAccessMode Mode { get; init; } = ServiceAccessMode.Everyone;

    /// <summary>Support groups whose members may.</summary>
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];

    /// <summary>Roles whose holders may.</summary>
    public IReadOnlyList<string> RoleKeys { get; init; } = [];

    /// <summary>Departments whose people may.</summary>
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];

    /// <summary>Locations (and the ones below them) whose people may.</summary>
    public IReadOnlyList<Guid> LocationIds { get; init; } = [];

    /// <summary>Named users who may.</summary>
    public IReadOnlyList<Guid> UserIds { get; init; } = [];
}

/// <summary>
/// The service card of module 2: every field, each in English and Arabic where it holds text.
/// Stored as JSON in the service's versions.
/// </summary>
public sealed record ServiceCard
{
    /// <summary>Display name.</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>One line for lists.</summary>
    public LocalizedText Summary { get; init; } = new(null, null);

    /// <summary>What the service is, who it is for and the outcome (Markdown, never HTML).</summary>
    public LocalizedText Description { get; init; } = new(null, null);

    /// <summary>The main service above this one.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Catalogs the service is in.</summary>
    public IReadOnlyList<Guid> CatalogIds { get; init; } = [];

    /// <summary>Categories for browsing and search (the shared B2 tree).</summary>
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];

    /// <summary>Owning department.</summary>
    public Guid? DepartmentId { get; init; }

    /// <summary>The named service owner.</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>Internal or external.</summary>
    public ServiceType Type { get; init; } = ServiceType.Internal;

    /// <summary>The provider's site, for external services (HTTPS).</summary>
    public string? ExternalUrl { get; init; }

    /// <summary>The form requesters fill (a C1 form); none: title and description only.</summary>
    public Guid? FormId { get; init; }

    /// <summary>Documents to upload.</summary>
    public IReadOnlyList<ServiceDocument> Documents { get; init; } = [];

    /// <summary>The requester's journey, in order.</summary>
    public IReadOnlyList<ServiceStep> Steps { get; init; } = [];

    /// <summary>Fee.</summary>
    public ServiceFee Fee { get; init; } = new();

    /// <summary>Delivery time in working days; the default completion SLA (C4).</summary>
    public int? Days { get; init; }

    /// <summary>Delivery channels.</summary>
    public IReadOnlyList<DeliveryChannel> Channels { get; init; } = [DeliveryChannel.Portal];

    /// <summary>Target audience.</summary>
    public IReadOnlyList<TargetAudience> Audiences { get; init; } = [];

    /// <summary>Service partners.</summary>
    public IReadOnlyList<ServicePartner> Partners { get; init; } = [];

    /// <summary>Shown as "You may also need".</summary>
    public IReadOnlyList<Guid> RelatedServiceIds { get; init; } = [];

    /// <summary>Services the requester must have completed first.</summary>
    public IReadOnlyList<Guid> PrerequisiteServiceIds { get; init; } = [];

    /// <summary>Search keywords (any language).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>An icon from Zelvo's set.</summary>
    public string? Icon { get; init; }

    /// <summary>An image (a file of the service), instead of the icon.</summary>
    public Guid? ImageFileId { get; init; }

    /// <summary>The service FAQ.</summary>
    public IReadOnlyList<ServiceFaq> Faq { get; init; } = [];

    /// <summary>Who may see and request it.</summary>
    public ServiceAccess Access { get; init; } = new();

    /// <summary>Roles whose holders may request it on behalf of someone else; empty: nobody.</summary>
    public IReadOnlyList<string> OnBehalfRoleKeys { get; init; } = [];

    /// <summary>Approvals before assignment and after fulfilment (task C3).</summary>
    public Zelvo.Workflows.Rules.ServiceApprovals Approvals { get; init; } = new();

    /// <summary>Initial group, SLAs and tasks (task C4).</summary>
    public ServiceFulfilment Fulfilment { get; init; } = new();

    /// <summary>A single service or a bundle (task C5).</summary>
    public ServiceKind Kind { get; init; } = ServiceKind.Service;

    /// <summary>The services of a bundle (task C5).</summary>
    public ServiceBundle Bundle { get; init; } = new();

    /// <summary>When Zelvo offers or starts the service by itself (task C5).</summary>
    public ServiceProactive Proactive { get; init; } = new();
}

/// <summary>Limits, JSON and the self-contained checks of service cards.</summary>
public static partial class ServiceCards
{
    /// <summary>Most documents, steps, FAQ entries, partners and links per card.</summary>
    public const int MaxItems = 50;

    /// <summary>Most keywords.</summary>
    public const int MaxKeywords = 30;

    /// <summary>Longest keyword.</summary>
    public const int MaxKeywordLength = 60;

    /// <summary>Longest description or FAQ answer, per language.</summary>
    public const int MaxLongText = 20_000;

    /// <summary>Longest short text (names, questions, notes), per language.</summary>
    public const int MaxShortText = 500;

    /// <summary>Longest summary, per language.</summary>
    public const int MaxSummary = 300;

    /// <summary>Most working days.</summary>
    public const int MaxDays = 3650;

    /// <summary>Largest fee in AED.</summary>
    public const decimal MaxFee = 10_000_000m;

    /// <summary>Icons of Zelvo's set (the web app draws them).</summary>
    public static IReadOnlyList<string> Icons { get; } =
    [
        "service", "laptop", "phone", "software", "access", "key", "email", "network", "printer", "document", "certificate",
        "id-card", "money", "payment", "calendar", "building", "car", "truck", "people", "person", "health", "education",
        "permit", "license", "home", "tools", "support", "question", "shield", "globe",
    ];

    /// <summary>JSON of cards: camelCase, enums as names.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Write(ServiceCard card) => JsonSerializer.Serialize(card, Json);

    public static ServiceCard Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new ServiceCard() : JsonSerializer.Deserialize<ServiceCard>(json, Json) ?? new ServiceCard();

    /// <summary>A service code: 2–30 capital letters, digits and hyphens, starting and ending with a letter or digit.</summary>
    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>A document key: camelCase, like form field keys.</summary>
    public static bool IsDocumentKey(string? key) => key is not null && DocumentKeyPattern().IsMatch(key);

    /// <summary>The form upload field of a required document (uploads go through <c>/form-uploads</c>).</summary>
    public static string UploadFieldOf(string documentKey) => "document" + char.ToUpperInvariant(documentKey[0]) + documentKey[1..];

    /// <summary>The document key of a form upload field, if it is one.</summary>
    public static string? DocumentKeyOf(string? uploadField) =>
        uploadField is { Length: > 8 } f && f.StartsWith("document", StringComparison.Ordinal) && char.IsAsciiLetterUpper(f[8])
            ? char.ToLowerInvariant(f[8]) + f[9..]
            : null;

    /// <summary>Trims texts and drops blanks and duplicates, so what is stored is tidy.</summary>
    public static ServiceCard Normalise(ServiceCard card) => card with
    {
        Name = Clean(card.Name),
        Summary = Clean(card.Summary),
        Description = Clean(card.Description),
        ExternalUrl = string.IsNullOrWhiteSpace(card.ExternalUrl) ? null : card.ExternalUrl.Trim(),
        CatalogIds = [.. (card.CatalogIds ?? []).Distinct()],
        CategoryIds = [.. (card.CategoryIds ?? []).Distinct()],
        Documents = [.. (card.Documents ?? []).Select(d => d with
        {
            Key = d.Key?.Trim() ?? string.Empty,
            Name = Clean(d.Name),
            Description = Clean(d.Description),
            FileTypes = [.. (d.FileTypes ?? []).Select(t => t.Trim().TrimStart('.').ToLowerInvariant()).Where(t => t.Length > 0).Distinct()],
        })],
        Steps = [.. (card.Steps ?? []).Select(s => s with { Name = Clean(s.Name), Description = Clean(s.Description), TaskKey = string.IsNullOrWhiteSpace(s.TaskKey) ? null : s.TaskKey.Trim() })],
        Fee = (card.Fee ?? new ServiceFee()) with
        {
            Notes = Clean(card.Fee?.Notes),
            AmountAed = card.Fee?.Paid == true ? card.Fee.AmountAed : null,
            Rules = card.Fee?.Paid == true ? [.. (card.Fee.Rules ?? []).Select(r => r with { Name = Clean(r.Name), Conditions = r.Conditions ?? [] })] : [],
        },
        Channels = [.. (card.Channels ?? []).Distinct()],
        Audiences = [.. (card.Audiences ?? []).Distinct()],
        Partners = [.. (card.Partners ?? []).Select(p => p with { Name = Clean(p.Name) })],
        RelatedServiceIds = [.. (card.RelatedServiceIds ?? []).Distinct()],
        PrerequisiteServiceIds = [.. (card.PrerequisiteServiceIds ?? []).Distinct()],
        Keywords = [.. (card.Keywords ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        Icon = string.IsNullOrWhiteSpace(card.Icon) ? null : card.Icon.Trim(),
        Faq = [.. (card.Faq ?? []).Select(f => f with { Question = Clean(f.Question), Answer = Clean(f.Answer) })],
        Access = (card.Access ?? new ServiceAccess()) with
        {
            GroupIds = [.. (card.Access?.GroupIds ?? []).Distinct()],
            RoleKeys = [.. (card.Access?.RoleKeys ?? []).Distinct(StringComparer.Ordinal)],
            DepartmentIds = [.. (card.Access?.DepartmentIds ?? []).Distinct()],
            LocationIds = [.. (card.Access?.LocationIds ?? []).Distinct()],
            UserIds = [.. (card.Access?.UserIds ?? []).Distinct()],
        },
        OnBehalfRoleKeys = [.. (card.OnBehalfRoleKeys ?? []).Distinct(StringComparer.Ordinal)],
        Approvals = new Zelvo.Workflows.Rules.ServiceApprovals
        {
            BeforeAssignment = [.. (card.Approvals?.BeforeAssignment ?? []).Select(CleanLevel)],
            AfterFulfilment = [.. (card.Approvals?.AfterFulfilment ?? []).Select(CleanLevel)],
        },
        Fulfilment = CleanFulfilment(card.Fulfilment),
        Bundle = new ServiceBundle { Items = card.Bundle?.Items ?? [] },
        Proactive = (card.Proactive ?? new ServiceProactive()) with
        {
            Triggers = [.. (card.Proactive?.Triggers ?? []).Select(t => t with
            {
                Key = t.Key?.Trim() ?? string.Empty,
                SourceFieldKey = string.IsNullOrWhiteSpace(t.SourceFieldKey) ? null : t.SourceFieldKey.Trim(),
                People = (t.People ?? new ProactivePeople()) with
                {
                    GroupIds = [.. (t.People?.GroupIds ?? []).Distinct()],
                    RoleKeys = [.. (t.People?.RoleKeys ?? []).Distinct(StringComparer.Ordinal)],
                    DepartmentIds = [.. (t.People?.DepartmentIds ?? []).Distinct()],
                    UserIds = [.. (t.People?.UserIds ?? []).Distinct()],
                },
            })],
        },
    };

    private static ServiceFulfilment CleanFulfilment(ServiceFulfilment? f) => f is null ? new() : f with
    {
        Overrides = [.. (f.Overrides ?? []).Select(o => o with { Conditions = o.Conditions ?? [] })],
        Escalations = f.Escalations ?? [],
        Tasks = [.. (f.Tasks ?? []).Select(t => t with { Key = t.Key?.Trim() ?? string.Empty, Name = Clean(t.Name), Description = Clean(t.Description) })],
    };

    private static Zelvo.Workflows.Rules.ApprovalLevelDefinition CleanLevel(Zelvo.Workflows.Rules.ApprovalLevelDefinition level) => level with
    {
        Name = Clean(level.Name),
        Approvers = [.. (level.Approvers ?? []).Distinct()],
        Conditions = level.Conditions ?? [],
    };

    /// <summary>
    /// Checks that need nothing but the card: names, lengths, URLs, fees, keys and counts. What
    /// the card refers to is checked against the database by <see cref="ServiceCardCheck"/>.
    /// </summary>
    public static List<FormProblem> CheckShape(ServiceCard card, IReadOnlyList<string> allowedFileTypes)
    {
        var problems = new List<FormProblem>();
        void Add(string field, string code, string message) => problems.Add(new FormProblem(field, code, message));

        if (!card.Name.HasAnyValue)
        {
            Add("name", "name-required", "Give the service a name in English or Arabic.");
        }

        Length(card.Name, "name", Data.Catalog.CatalogService.NameMaxLength);
        Length(card.Summary, "summary", MaxSummary);
        Length(card.Description, "description", MaxLongText);
        if (card.Type == ServiceType.External)
        {
            if (card.ExternalUrl is null || !Uri.TryCreate(card.ExternalUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || card.ExternalUrl.Length > 2000)
            {
                Add("externalUrl", "external-url-invalid", "An external service links to an HTTPS address of the provider.");
            }

            if (card.FormId is not null || card.Documents.Count > 0)
            {
                Add("type", "external-has-form", "External services are requested on the provider's site; remove the form and documents.");
            }
        }

        if (card.Fee.Paid && (card.Fee.AmountAed is not { } amount || amount <= 0 || amount > MaxFee || decimal.Round(amount, 2) != amount))
        {
            Add("fee.amountAed", "fee-invalid", "A paid service has an amount in AED above zero, with at most two decimals.");
        }

        Length(card.Fee.Notes, "fee.notes", MaxShortText);
        // Task E8: amounts added by answers.
        if (card.Fee.Rules.Count > MaxFulfilmentItems)
        {
            Add("fee.rules", "too-many", $"At most {MaxFulfilmentItems} fee rules.");
        }

        for (var i = 0; i < card.Fee.Rules.Count; i++)
        {
            var rule = card.Fee.Rules[i];
            if (rule.AmountAed <= 0 || rule.AmountAed > MaxFee || decimal.Round(rule.AmountAed, 2) != rule.AmountAed)
            {
                Add($"fee.rules.{i}.amountAed", "fee-invalid", "Add an amount in AED above zero, with at most two decimals.");
            }

            if (!rule.Name.HasAnyValue)
            {
                Add($"fee.rules.{i}.name", "fee-rule-name-required", "Name the addition in English or Arabic, for the receipt.");
            }

            if (rule.Conditions.Count == 0)
            {
                Add($"fee.rules.{i}.conditions", "conditions-required", "Say which answers add this amount.");
            }

            Length(rule.Name, $"fee.rules.{i}.name", MaxShortText);
            Zelvo.Workflows.Rules.RuleDocuments.CheckConditions(rule.Conditions, $"fee.rules.{i}.conditions", problems);
        }
        if (card.Days is { } days && (days < 1 || days > MaxDays))
        {
            Add("days", "days-invalid", $"Use 1 to {MaxDays} working days.");
        }

        Count(card.Documents.Count, "documents");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < card.Documents.Count; i++)
        {
            var document = card.Documents[i];
            var at = $"documents.{i}";
            if (!IsDocumentKey(document.Key))
            {
                Add($"{at}.key", "document-key-invalid", "Document keys are camelCase letters and digits, starting with a lower-case letter.");
            }
            else if (!keys.Add(document.Key))
            {
                Add($"{at}.key", "document-key-taken", "Each document has its own key.");
            }

            if (!document.Name.HasAnyValue)
            {
                Add($"{at}.name", "document-name-required", "Name the document in English or Arabic.");
            }

            Length(document.Name, $"{at}.name", MaxShortText);
            Length(document.Description, $"{at}.description", MaxShortText);
            foreach (var type in document.FileTypes.Where(t => !allowedFileTypes.Contains(t, StringComparer.Ordinal)))
            {
                Add($"{at}.fileTypes", "file-type-not-allowed", $"'{type}' is not in the tenant's allowed file types.");
            }

            if (document.MaxSizeMb is { } size && (size < 1 || size > 1024))
            {
                Add($"{at}.maxSizeMb", "max-size-invalid", "Use 1 to 1024 MB.");
            }
        }

        Count(card.Steps.Count, "steps");
        for (var i = 0; i < card.Steps.Count; i++)
        {
            if (!card.Steps[i].Name.HasAnyValue)
            {
                Add($"steps.{i}.name", "step-name-required", "Name the step in English or Arabic.");
            }

            Length(card.Steps[i].Name, $"steps.{i}.name", MaxShortText);
            Length(card.Steps[i].Description, $"steps.{i}.description", MaxShortText);
        }

        Count(card.Faq.Count, "faq");
        for (var i = 0; i < card.Faq.Count; i++)
        {
            if (!card.Faq[i].Question.HasAnyValue || !card.Faq[i].Answer.HasAnyValue)
            {
                Add($"faq.{i}", "faq-incomplete", "Each FAQ entry has a question and an answer.");
            }

            Length(card.Faq[i].Question, $"faq.{i}.question", MaxShortText);
            Length(card.Faq[i].Answer, $"faq.{i}.answer", MaxLongText);
        }

        Count(card.Partners.Count, "partners");
        for (var i = 0; i < card.Partners.Count; i++)
        {
            var partner = card.Partners[i];
            if (partner.Kind == PartnerKind.Department ? partner.DepartmentId is null : !partner.Name.HasAnyValue)
            {
                Add($"partners.{i}", "partner-incomplete", "Choose the department, or name the entity or vendor.");
            }

            Length(partner.Name, $"partners.{i}.name", MaxShortText);
        }

        Count(card.CatalogIds.Count, "catalogIds");
        Count(card.CategoryIds.Count, "categoryIds");
        Count(card.RelatedServiceIds.Count, "relatedServiceIds");
        Count(card.PrerequisiteServiceIds.Count, "prerequisiteServiceIds");
        if (card.Keywords.Count > MaxKeywords || card.Keywords.Any(k => k.Length > MaxKeywordLength))
        {
            Add("keywords", "keywords-invalid", $"Up to {MaxKeywords} keywords of up to {MaxKeywordLength} characters.");
        }

        if (card.Icon is { } icon && !Icons.Contains(icon, StringComparer.Ordinal))
        {
            Add("icon", "icon-unknown", "Choose one of Zelvo's icons.");
        }

        if (card.Channels.Count == 0)
        {
            Add("channels", "channels-required", "Choose at least one delivery channel.");
        }

        var access = card.Access;
        if (access.Mode == ServiceAccessMode.Restricted
            && access.GroupIds.Count + access.RoleKeys.Count + access.DepartmentIds.Count + access.LocationIds.Count + access.UserIds.Count == 0)
        {
            Add("access", "access-empty", "Name at least one group, role, department, location or user who may see the service.");
        }

        if (access.GroupIds.Count + access.RoleKeys.Count + access.DepartmentIds.Count + access.LocationIds.Count + access.UserIds.Count > 90)
        {
            Add("access", "access-too-long", "Name at most 90 groups, roles, departments, locations and users.");
        }

        Zelvo.Workflows.Rules.RuleDocuments.CheckLevels(card.Approvals.BeforeAssignment, "approvals.beforeAssignment", problems);
        Zelvo.Workflows.Rules.RuleDocuments.CheckLevels(card.Approvals.AfterFulfilment, "approvals.afterFulfilment", problems);
        CheckFulfilment(card, problems);
        CheckBundle(card, problems);
        CheckProactive(card, problems);
        if (card.Type == ServiceType.External && card.Approvals.BeforeAssignment.Count + card.Approvals.AfterFulfilment.Count > 0)
        {
            Add("approvals", "external-has-approvals", "External services are requested on the provider's site; remove the approvals.");
        }

        return problems;

        void Length(LocalizedText? text, string field, int max)
        {
            if (text is not null && ((text.En?.Length ?? 0) > max || (text.Ar?.Length ?? 0) > max))
            {
                Add(field, "text-too-long", $"Use at most {max} characters in each language.");
            }
        }

        void Count(int count, string field)
        {
            if (count > MaxItems)
            {
                Add(field, "too-many", $"At most {MaxItems}.");
            }
        }
    }

    /// <summary>Most overrides, tasks and escalations.</summary>
    public const int MaxFulfilmentItems = 20;

    /// <summary>Checks the fulfilment section (task C4): overrides, SLA targets, escalations and tasks; steps refer to tasks that exist.</summary>
    private static void CheckFulfilment(ServiceCard card, List<FormProblem> problems)
    {
        var f = card.Fulfilment;
        void Add(string field, string code, string message) => problems.Add(new FormProblem(field, code, message));
        if (card.Type == ServiceType.External && (f.GroupId is not null || f.Tasks.Count > 0 || f.Overrides.Count > 0))
        {
            Add("fulfilment", "external-has-fulfilment", "External services are fulfilled by the provider; remove the group and tasks.");
        }

        if (f.Overrides.Count > MaxFulfilmentItems || f.Tasks.Count > MaxFulfilmentItems || f.Escalations.Count > MaxFulfilmentItems)
        {
            Add("fulfilment", "too-many", $"At most {MaxFulfilmentItems} overrides, tasks and escalations.");
        }

        foreach (var (minutes, field) in new[] { (f.OwnershipMinutes, "fulfilment.ownershipMinutes"), (f.CompletionMinutes, "fulfilment.completionMinutes") })
        {
            if (minutes is { } m && (m < 1 || m > Data.Sla.SlaPolicyTarget.MaxMinutes))
            {
                Add(field, "target-invalid", $"Use 1 to {Data.Sla.SlaPolicyTarget.MaxMinutes} minutes.");
            }
        }

        for (var i = 0; i < f.Overrides.Count; i++)
        {
            var o = f.Overrides[i];
            if (o.GroupId is null)
            {
                Add($"fulfilment.overrides.{i}.groupId", "group-required", "Choose the group for these answers.");
            }

            if (o.Conditions.Count == 0)
            {
                Add($"fulfilment.overrides.{i}.conditions", "conditions-required", "Say which answers send requests to this group.");
            }

            Zelvo.Workflows.Rules.RuleDocuments.CheckConditions(o.Conditions, $"fulfilment.overrides.{i}.conditions", problems);
        }

        for (var i = 0; i < f.Escalations.Count; i++)
        {
            var e = f.Escalations[i];
            if (e.Percent is < 1 or > Data.Sla.SlaEscalation.MaxPercent)
            {
                Add($"fulfilment.escalations.{i}.percent", "percent-invalid", $"Use 1 to {Data.Sla.SlaEscalation.MaxPercent} %.");
            }

            if (!e.NotifyAssignee && !e.NotifyGroupLeads && e.NotifyGroupId is null)
            {
                Add($"fulfilment.escalations.{i}", "escalation-nobody", "Say who to alert.");
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < f.Tasks.Count; i++)
        {
            var task = f.Tasks[i];
            var at = $"fulfilment.tasks.{i}";
            if (!IsDocumentKey(task.Key))
            {
                Add($"{at}.key", "task-key-invalid", "Task keys are camelCase letters and digits, starting with a lower-case letter.");
            }
            else if (!keys.Add(task.Key))
            {
                Add($"{at}.key", "task-key-taken", "Each task has its own key.");
            }

            if (!task.Name.HasAnyValue)
            {
                Add($"{at}.name", "task-name-required", "Name the task in English or Arabic.");
            }

            if ((task.Name.En?.Length ?? 0) > 150 || (task.Name.Ar?.Length ?? 0) > 150 || (task.Description.En?.Length ?? 0) > 1000 || (task.Description.Ar?.Length ?? 0) > 1000)
            {
                Add($"{at}.name", "text-too-long", "Use at most 150 characters for names and 1000 for descriptions.");
            }

            if (task.TargetMinutes is { } target && (target < 1 || target > Data.Sla.SlaPolicyTarget.MaxMinutes))
            {
                Add($"{at}.targetMinutes", "target-invalid", $"Use 1 to {Data.Sla.SlaPolicyTarget.MaxMinutes} minutes.");
            }
        }

        if (f.Tasks.Count > 0 && f.Tasks[0].ParallelWithPrevious)
        {
            Add("fulfilment.tasks.0.parallelWithPrevious", "first-task-parallel", "The first task starts the work; later tasks can run alongside it.");
        }

        for (var i = 0; i < card.Steps.Count; i++)
        {
            if (card.Steps[i].TaskKey is { } key && !keys.Contains(key))
            {
                Add($"steps.{i}.taskKey", "task-unknown", "The step refers to a task the service does not have.");
            }
        }
    }

    /// <summary>Most services in a bundle.</summary>
    public const int MaxBundleItems = 20;

    /// <summary>Most triggers of a proactive service.</summary>
    public const int MaxTriggers = 10;

    /// <summary>Longest offer, in days.</summary>
    public const int MaxOfferDays = 90;

    /// <summary>Largest date offset, in days.</summary>
    public const int MaxOffsetDays = 3650;

    /// <summary>Checks a bundle (task C5): its services, and that it has no fulfilment, approvals, documents or fee of its own.</summary>
    private static void CheckBundle(ServiceCard card, List<FormProblem> problems)
    {
        void Add(string field, string code, string message) => problems.Add(new FormProblem(field, code, message));
        var items = card.Bundle.Items;
        if (card.Kind != ServiceKind.Bundle)
        {
            if (items.Count > 0)
            {
                Add("bundle", "bundle-items-on-service", "Only bundles list services; make it a bundle or remove the services.");
            }

            return;
        }

        if (card.Type == ServiceType.External)
        {
            Add("type", "external-bundle", "A bundle is requested in Zelvo; make it internal.");
        }

        if (items.Count == 0)
        {
            Add("bundle.items", "bundle-empty", "Add the services of the bundle.");
        }

        if (items.Count > MaxBundleItems)
        {
            Add("bundle.items", "too-many", $"At most {MaxBundleItems} services.");
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ServiceId is not { } id)
            {
                Add($"bundle.items.{i}.serviceId", "service-required", "Choose the service.");
            }
            else if (!seen.Add(id))
            {
                Add($"bundle.items.{i}.serviceId", "service-duplicate", "Each service is in the bundle once.");
            }
        }

        if (items.Count > 0 && items[0].ParallelWithPrevious)
        {
            Add("bundle.items.0.parallelWithPrevious", "first-item-parallel", "The first service starts the bundle; later ones can run alongside it.");
        }

        var f = card.Fulfilment;
        if (f.GroupId is not null || f.Tasks.Count + f.Overrides.Count + f.Escalations.Count > 0 || f.OwnershipMinutes is not null || f.CompletionMinutes is not null)
        {
            Add("fulfilment", "bundle-has-fulfilment", "Each service of the bundle is fulfilled by its own team; remove the bundle's group, targets and tasks.");
        }

        if (card.Approvals.BeforeAssignment.Count + card.Approvals.AfterFulfilment.Count > 0)
        {
            Add("approvals", "bundle-has-approvals", "Each service of the bundle has its own approvals; remove the bundle's.");
        }

        if (card.Documents.Count > 0)
        {
            Add("documents", "bundle-has-documents", "The services of the bundle ask for their own documents; remove the bundle's.");
        }

        if (card.Fee.Paid)
        {
            Add("fee", "bundle-has-fee", "Fees belong to the services of the bundle; make the bundle free.");
        }

        if (card.Steps.Any(s => s.TaskKey is not null))
        {
            Add("steps", "bundle-step-task", "A bundle has no tasks of its own; its steps follow its stage.");
        }
    }

    /// <summary>Checks the proactive section (task C5): mode, offer days and triggers.</summary>
    private static void CheckProactive(ServiceCard card, List<FormProblem> problems)
    {
        void Add(string field, string code, string message) => problems.Add(new FormProblem(field, code, message));
        var p = card.Proactive;
        if (!p.Enabled && p.Triggers.Count == 0)
        {
            return;
        }

        if (card.Type == ServiceType.External && p.Enabled)
        {
            Add("proactive", "external-proactive", "External services are requested on the provider's site; they cannot be proactive.");
        }

        if (p.Enabled && p.Triggers.Count == 0)
        {
            Add("proactive.triggers", "triggers-required", "Say what starts the service: a date, a schedule or an event.");
        }

        if (p.Triggers.Count > MaxTriggers)
        {
            Add("proactive.triggers", "too-many", $"At most {MaxTriggers} triggers.");
        }

        if (p.OfferDays < 1 || p.OfferDays > MaxOfferDays)
        {
            Add("proactive.offerDays", "offer-days-invalid", $"Offers stay open 1 to {MaxOfferDays} days.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < p.Triggers.Count; i++)
        {
            var t = p.Triggers[i];
            var at = $"proactive.triggers.{i}";
            if (!IsDocumentKey(t.Key) || t.Key.Length > Data.Catalog.ProactiveRun.KeyMaxLength)
            {
                Add($"{at}.key", "trigger-key-invalid", "Trigger keys are camelCase letters and digits, starting with a lower-case letter.");
            }
            else if (!keys.Add(t.Key))
            {
                Add($"{at}.key", "trigger-key-taken", "Each trigger has its own key.");
            }

            switch (t.Kind)
            {
                case Data.Catalog.ProactiveTriggerKind.Date:
                    if (t.OffsetDays is < -MaxOffsetDays or > MaxOffsetDays)
                    {
                        Add($"{at}.offsetDays", "offset-invalid", $"Use {-MaxOffsetDays} to {MaxOffsetDays} days.");
                    }

                    if ((t.SourceServiceId is null) != (t.SourceFieldKey is null))
                    {
                        Add($"{at}.sourceFieldKey", "source-incomplete", "Choose both the service and its date field, or neither (dates then come through the API).");
                    }

                    break;
                case Data.Catalog.ProactiveTriggerKind.Schedule:
                    if (t.StartOn is null)
                    {
                        Add($"{at}.startOn", "start-required", "Choose the first date of the schedule.");
                    }

                    if (t.Every is < 1 or > 365)
                    {
                        Add($"{at}.every", "every-invalid", "Repeat every 1 to 365 days, weeks, months or years.");
                    }

                    if (t.People.Count == 0)
                    {
                        Add($"{at}.people", "people-required", "Name the groups, roles, departments or people the schedule is for.");
                    }

                    if (t.People.Count > 90)
                    {
                        Add($"{at}.people", "people-too-long", "Name at most 90 groups, roles, departments and people.");
                    }

                    break;
            }
        }
    }

    private static LocalizedText Clean(LocalizedText? text) =>
        text is null ? new(null, null) : new(string.IsNullOrWhiteSpace(text.En) ? null : text.En.Trim(), string.IsNullOrWhiteSpace(text.Ar) ? null : text.Ar.Trim());

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,28}[A-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentKeyPattern();
}
