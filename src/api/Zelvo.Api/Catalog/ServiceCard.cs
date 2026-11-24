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
        Steps = [.. (card.Steps ?? []).Select(s => s with { Name = Clean(s.Name), Description = Clean(s.Description) })],
        Fee = (card.Fee ?? new ServiceFee()) with { Notes = Clean(card.Fee?.Notes), AmountAed = card.Fee?.Paid == true ? card.Fee.AmountAed : null },
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

    private static LocalizedText Clean(LocalizedText? text) =>
        text is null ? new(null, null) : new(string.IsNullOrWhiteSpace(text.En) ? null : text.En.Trim(), string.IsNullOrWhiteSpace(text.Ar) ? null : text.Ar.Trim());

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,28}[A-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentKeyPattern();
}
