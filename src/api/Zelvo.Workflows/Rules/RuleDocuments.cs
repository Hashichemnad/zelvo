using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets.Forms;

namespace Zelvo.Workflows.Rules;

/// <summary>
/// One condition of a rule or approval level: a field of the ticket, an operator and a value.
/// Fields: <c>service</c>, <c>category</c> (or below), <c>location</c> (or below), <c>group</c>,
/// <c>channel</c>, <c>state</c>, <c>requesterDepartment</c>, <c>requesterLanguage</c> (lists of
/// values with <c>in</c> / <c>notIn</c>); <c>priority</c> and <c>fee</c> (numbers with
/// <c>eq</c>, <c>neq</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>); and <c>answer.&lt;key&gt;</c>,
/// a form answer, which also takes <c>contains</c>, <c>empty</c> and <c>notEmpty</c>.
/// </summary>
public sealed record WorkflowCondition
{
    /// <summary>The field.</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>The operator.</summary>
    public string Op { get; init; } = RuleOperators.In;

    /// <summary>The value: a list for <c>in</c> / <c>notIn</c>, a number or text otherwise, none for <c>empty</c> / <c>notEmpty</c>.</summary>
    public JsonNode? Value { get; init; }
}

/// <summary>Operators of conditions.</summary>
public static class RuleOperators
{
    /// <summary>One of the values (for trees: the node or below).</summary>
    public const string In = "in";

    /// <summary>None of the values.</summary>
    public const string NotIn = "notIn";

    /// <summary>Equal.</summary>
    public const string Eq = "eq";

    /// <summary>Not equal.</summary>
    public const string Neq = "neq";

    /// <summary>Greater than.</summary>
    public const string Gt = "gt";

    /// <summary>Greater than or equal.</summary>
    public const string Gte = "gte";

    /// <summary>Less than.</summary>
    public const string Lt = "lt";

    /// <summary>Less than or equal.</summary>
    public const string Lte = "lte";

    /// <summary>Text contains (ignoring case), or a list answer holds the value.</summary>
    public const string Contains = "contains";

    /// <summary>No answer.</summary>
    public const string Empty = "empty";

    /// <summary>Any answer.</summary>
    public const string NotEmpty = "notEmpty";

    /// <summary>List operators.</summary>
    public static IReadOnlyList<string> List { get; } = [In, NotIn];

    /// <summary>Number operators.</summary>
    public static IReadOnlyList<string> Number { get; } = [Eq, Neq, Gt, Gte, Lt, Lte];

    /// <summary>Operators on form answers.</summary>
    public static IReadOnlyList<string> Answer { get; } = [Eq, Neq, Gt, Gte, Lt, Lte, Contains, Empty, NotEmpty];
}

/// <summary>Fields conditions look at.</summary>
public static class RuleFields
{
    /// <summary>The requested service (service requests).</summary>
    public const string Service = "service";

    /// <summary>The category, or a category above it.</summary>
    public const string Category = "category";

    /// <summary>The location, or a location above it.</summary>
    public const string Location = "location";

    /// <summary>The group working on it.</summary>
    public const string Group = "group";

    /// <summary>How it came in.</summary>
    public const string Channel = "channel";

    /// <summary>Its state (for state changes: the new state).</summary>
    public const string State = "state";

    /// <summary>The requester's department.</summary>
    public const string RequesterDepartment = "requesterDepartment";

    /// <summary>The requester's language (en, ar).</summary>
    public const string RequesterLanguage = "requesterLanguage";

    /// <summary>
    /// Task K1: a customer group (B3) the requester is in, added by hand or by email domain, for
    /// example the VIP group (product owner decision 2026-11-26: no separate VIP flag).
    /// </summary>
    public const string RequesterCustomerGroup = "requesterCustomerGroup";

    /// <summary>Incident priority, 1 (highest) to 5.</summary>
    public const string Priority = "priority";

    /// <summary>The service's fee in AED (0 for free services).</summary>
    public const string Fee = "fee";

    /// <summary>Prefix of form answers: <c>answer.monitorSize</c>.</summary>
    public const string AnswerPrefix = "answer.";

    /// <summary>Fields taking lists of ids.</summary>
    public static IReadOnlyList<string> IdLists { get; } = [Service, Category, Location, Group, RequesterDepartment, RequesterCustomerGroup];

    /// <summary>Fields taking lists of names.</summary>
    public static IReadOnlyList<string> NameLists { get; } = [Channel, State, RequesterLanguage];

    /// <summary>Fields taking numbers.</summary>
    public static IReadOnlyList<string> Numbers { get; } = [Priority, Fee];

    /// <summary>The form field key of an answer field, or <see langword="null"/>.</summary>
    public static string? AnswerKeyOf(string field) =>
        field.StartsWith(AnswerPrefix, StringComparison.Ordinal) && field.Length > AnswerPrefix.Length ? field[AnswerPrefix.Length..] : null;
}

/// <summary>What conditions are evaluated against.</summary>
/// <param name="Type">Record type.</param>
/// <param name="State">Current (new) state.</param>
/// <param name="Channel">Channel.</param>
/// <param name="ServiceId">Requested service.</param>
/// <param name="Fee">The service's fee in AED (0 when free).</param>
/// <param name="CategoryAncestry">The category and those above it.</param>
/// <param name="LocationAncestry">The location and those above it.</param>
/// <param name="Priority">Incident priority.</param>
/// <param name="GroupId">Group.</param>
/// <param name="RequesterDepartmentId">The requester's department.</param>
/// <param name="RequesterLanguage">The requester's language.</param>
/// <param name="Answers">Form answers by field key.</param>
/// <param name="RequesterCustomerGroups">Task K1: the active customer groups the requester is in.</param>
public sealed record RuleSubject(
    TicketType Type, TicketState State, TicketChannel Channel, Guid? ServiceId, decimal? Fee,
    IReadOnlyCollection<Guid> CategoryAncestry, IReadOnlyCollection<Guid> LocationAncestry, int? Priority, Guid? GroupId,
    Guid? RequesterDepartmentId, string? RequesterLanguage, JsonObject Answers, IReadOnlyCollection<Guid>? RequesterCustomerGroups = null);

/// <summary>An action of a rule, by <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssignAction), "assign")]
[JsonDerivedType(typeof(NotifyAction), "notify")]
[JsonDerivedType(typeof(ApprovalAction), "approval")]
[JsonDerivedType(typeof(ConnectorAction), "connector")]
public abstract record WorkflowAction;

/// <summary>
/// Task E7: call an operation of a REST connector (the no-code connector builder) for the ticket,
/// for example "update the case in our CRM". Queued with the change and sent by the worker with
/// retries, never inside the save; the answer's values become an internal note.
/// </summary>
public sealed record ConnectorAction : WorkflowAction
{
    /// <summary>The REST connector.</summary>
    public Guid? ConnectorId { get; init; }

    /// <summary>The operation's key.</summary>
    public string? Operation { get; init; }
}

/// <summary>Assign the ticket: to a group's queue, by round-robin among its members, or to a member.</summary>
public sealed record AssignAction : WorkflowAction
{
    /// <summary>The group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Group queue or round-robin (when no assignee is named).</summary>
    public AssignmentMethod Method { get; init; } = AssignmentMethod.GroupQueue;

    /// <summary>A member of the group to assign.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>Tell people, in the app and optionally by email, with wording in English and Arabic.</summary>
public sealed record NotifyAction : WorkflowAction
{
    /// <summary>
    /// Who: <c>requester</c>, <c>requesterManager</c>, <c>assignee</c>, <c>group</c> (the ticket's
    /// group members), <c>groupLeads</c>, <c>user:&lt;id&gt;</c>, <c>group:&lt;id&gt;</c>,
    /// <c>groupLeads:&lt;id&gt;</c> (a named group's team leads, task K1), <c>role:&lt;key&gt;</c>.
    /// </summary>
    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>Subject (and the in-app title).</summary>
    public LocalizedText Subject { get; init; } = new(null, null);

    /// <summary>Body; placeholders as in <see cref="RuleDocuments.NotifyPlaceholders"/>.</summary>
    public LocalizedText Body { get; init; } = new(null, null);

    /// <summary>Whether it also goes by email.</summary>
    public bool Email { get; init; } = true;

    /// <summary>
    /// Task K1: also by WhatsApp, SMS or Microsoft Teams (E3), to people who can be reached there:
    /// WhatsApp and SMS to the phone of those who turned them on under Account → Notifications,
    /// Teams to those linked to the tenant's Teams app. <see langword="null"/> (rules made before
    /// K1): as each person chose for their notifications.
    /// </summary>
    public IReadOnlyList<Data.Channels.MessagingKind>? Channels { get; init; }
}

/// <summary>Ask for approval (service requests; before assignment on submission, or after fulfilment).</summary>
public sealed record ApprovalAction : WorkflowAction
{
    /// <summary>When.</summary>
    public ApprovalStage Stage { get; init; } = ApprovalStage.BeforeAssignment;

    /// <summary>The levels, in order.</summary>
    public IReadOnlyList<ApprovalLevelDefinition> Levels { get; init; } = [];
}

/// <summary>Whether one approver of a level is enough, or all must approve (parallel).</summary>
public enum ApprovalMode
{
    /// <summary>The first approval decides the level.</summary>
    Any,

    /// <summary>Everyone must approve; one rejection rejects.</summary>
    All,
}

/// <summary>Who approves.</summary>
public enum ApproverKind
{
    /// <summary>The requester's manager (user profile).</summary>
    RequesterManager,

    /// <summary>A named person.</summary>
    User,

    /// <summary>Every member of a group.</summary>
    Group,

    /// <summary>The team leads of a group.</summary>
    GroupLeads,

    /// <summary>Everyone holding a role.</summary>
    Role,

    /// <summary>The person chosen in a user-picker answer of the form.</summary>
    AnswerUser,

    /// <summary>Everyone whose roles grant a permission (<see cref="ApproverRef.Key"/>), for example the change managers (task H3).</summary>
    Permission,
}

/// <summary>An approver of a level.</summary>
public sealed record ApproverRef
{
    /// <summary>Kind.</summary>
    public ApproverKind Kind { get; init; }

    /// <summary>The user or group.</summary>
    public Guid? Id { get; init; }

    /// <summary>The role key, the form field key, or the permission key.</summary>
    public string? Key { get; init; }
}

/// <summary>One level of approval.</summary>
public sealed record ApprovalLevelDefinition
{
    /// <summary>Name shown to requesters and approvers, for example "Manager approval".</summary>
    public LocalizedText Name { get; init; } = new(null, null);

    /// <summary>Any or all approvers.</summary>
    public ApprovalMode Mode { get; init; } = ApprovalMode.Any;

    /// <summary>Who.</summary>
    public IReadOnlyList<ApproverRef> Approvers { get; init; } = [];

    /// <summary>The level applies only when all hold (approval by amount or answer); none: always.</summary>
    public IReadOnlyList<WorkflowCondition> Conditions { get; init; } = [];
}

/// <summary>A service's approvals (service card, task C3).</summary>
public sealed record ServiceApprovals
{
    /// <summary>Levels before the request reaches agents.</summary>
    public IReadOnlyList<ApprovalLevelDefinition> BeforeAssignment { get; init; } = [];

    /// <summary>Levels of the sign-off before the request is resolved.</summary>
    public IReadOnlyList<ApprovalLevelDefinition> AfterFulfilment { get; init; } = [];
}

/// <summary>Reading, checking and evaluating rule documents.</summary>
public static class RuleDocuments
{
    /// <summary>Most conditions per rule or level, actions per rule, levels per approval, approvers per level and recipients.</summary>
    public const int MaxItems = 20;

    /// <summary>Longest subject per language.</summary>
    public const int MaxSubject = 300;

    /// <summary>Longest body per language.</summary>
    public const int MaxBody = 5000;

    /// <summary>Placeholders rule notifications may use.</summary>
    public static IReadOnlyList<string> NotifyPlaceholders { get; } =
        ["recipient.name", "tenant.name", "ticket.number", "ticket.title", "ticket.state", "ticket.group", "requester.name", "service.name"];

    /// <summary>Longest plain-language description of a rule (task K1).</summary>
    public const int MaxSourceText = 2000;

    /// <summary>JSON of rule documents: camelCase, enums as names.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>The conditions stored in a rule.</summary>
    public static IReadOnlyList<WorkflowCondition> ReadConditions(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<WorkflowCondition>>(json, Json) ?? [];

    /// <summary>The actions stored in a rule.</summary>
    public static IReadOnlyList<WorkflowAction> ReadActions(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<WorkflowAction>>(json, Json) ?? [];

    /// <summary>JSON to store.</summary>
    public static string Write<T>(IReadOnlyList<T> items) => JsonSerializer.Serialize(items, Json);

    /// <summary>Whether all (or any) of <paramref name="conditions"/> hold; no conditions always hold.</summary>
    public static bool Holds(IReadOnlyList<WorkflowCondition> conditions, ConditionMatch match, RuleSubject subject)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Count == 0)
        {
            return true;
        }

        return match == ConditionMatch.All ? conditions.All(c => Holds(c, subject)) : conditions.Any(c => Holds(c, subject));
    }

    /// <summary>Whether one condition holds.</summary>
    public static bool Holds(WorkflowCondition condition, RuleSubject subject)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(subject);
        switch (condition.Field)
        {
            case RuleFields.Service:
                return InList(condition, subject.ServiceId is { } s ? [s.ToString()] : []);
            case RuleFields.Category:
                return InList(condition, [.. subject.CategoryAncestry.Select(c => c.ToString())]);
            case RuleFields.Location:
                return InList(condition, [.. subject.LocationAncestry.Select(c => c.ToString())]);
            case RuleFields.Group:
                return InList(condition, subject.GroupId is { } g ? [g.ToString()] : []);
            case RuleFields.RequesterDepartment:
                return InList(condition, subject.RequesterDepartmentId is { } d ? [d.ToString()] : []);
            case RuleFields.RequesterCustomerGroup:
                return InList(condition, [.. (subject.RequesterCustomerGroups ?? []).Select(g => g.ToString())]);
            case RuleFields.Channel:
                return InList(condition, [subject.Channel.ToString()]);
            case RuleFields.State:
                return InList(condition, [subject.State.ToString()]);
            case RuleFields.RequesterLanguage:
                return InList(condition, subject.RequesterLanguage is { } l ? [l] : []);
            case RuleFields.Priority:
                return subject.Priority is { } p && Compare(condition.Op, p, Number(condition.Value));
            case RuleFields.Fee:
                return subject.Type == TicketType.ServiceRequest && Compare(condition.Op, subject.Fee ?? 0m, Number(condition.Value));
        }

        if (RuleFields.AnswerKeyOf(condition.Field) is not { } key)
        {
            return false;
        }

        var answer = subject.Answers[key];
        var empty = answer is null || (answer is JsonValue v && v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
            || (answer is JsonArray a && a.Count == 0);
        switch (condition.Op)
        {
            case RuleOperators.Empty:
                return empty;
            case RuleOperators.NotEmpty:
                return !empty;
            case RuleOperators.Contains:
                var wanted = Text(condition.Value);
                return !empty && wanted.Length > 0 && (answer is JsonArray list
                    ? list.Any(item => string.Equals(Text(item), wanted, StringComparison.OrdinalIgnoreCase))
                    : Text(answer).Contains(wanted, StringComparison.OrdinalIgnoreCase));
            case RuleOperators.Eq or RuleOperators.Neq:
                var same = !empty && (TryNumber(answer, out var an) && TryNumber(condition.Value, out var cn)
                    ? an == cn
                    : string.Equals(Text(answer), Text(condition.Value), StringComparison.OrdinalIgnoreCase));
                return condition.Op == RuleOperators.Eq ? same : !same;
            default:
                return !empty && TryNumber(answer, out var number) && Compare(condition.Op, number, Number(condition.Value));
        }
    }

    /// <summary>Checks the shape of conditions: known fields, operators and values. References are checked by the caller.</summary>
    public static void CheckConditions(IReadOnlyList<WorkflowCondition> conditions, string at, List<FormProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(problems);
        if (conditions.Count > MaxItems)
        {
            problems.Add(new($"{at}", "too-many", $"At most {MaxItems} conditions."));
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            var field = $"{at}.{i}";
            if (RuleFields.IdLists.Contains(c.Field) || RuleFields.NameLists.Contains(c.Field))
            {
                if (!RuleOperators.List.Contains(c.Op))
                {
                    problems.Add(new($"{field}.op", "operator-invalid", "Use 'in' or 'notIn' for this field."));
                }

                var values = Values(c.Value);
                if (values is null || values.Count == 0 || values.Count > 100)
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Choose 1 to 100 values."));
                }
                else if (RuleFields.IdLists.Contains(c.Field) && values.Any(v => !Guid.TryParse(v, out _)))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Choose existing items."));
                }
                else if (c.Field == RuleFields.Channel && values.Any(v => !Enum.TryParse<TicketChannel>(v, out _)))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Choose known channels."));
                }
                else if (c.Field == RuleFields.State && values.Any(v => !Enum.TryParse<TicketState>(v, out _)))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Choose known states."));
                }
                else if (c.Field == RuleFields.RequesterLanguage && values.Any(v => !LocalizedText.Languages.Contains(v)))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Choose English (en) or Arabic (ar)."));
                }
            }
            else if (RuleFields.Numbers.Contains(c.Field))
            {
                if (!RuleOperators.Number.Contains(c.Op))
                {
                    problems.Add(new($"{field}.op", "operator-invalid", "Use a comparison for numbers."));
                }

                if (!TryNumber(c.Value, out _))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Enter a number."));
                }
            }
            else if (RuleFields.AnswerKeyOf(c.Field) is { } key)
            {
                if (key.Length > 60 || !key.All(char.IsAsciiLetterOrDigit))
                {
                    problems.Add(new($"{field}.field", "field-invalid", "Answers are named by the form field's key."));
                }

                if (!RuleOperators.Answer.Contains(c.Op))
                {
                    problems.Add(new($"{field}.op", "operator-invalid", "Choose a known operator."));
                }
                else if (c.Op is not (RuleOperators.Empty or RuleOperators.NotEmpty) && Text(c.Value).Length is 0 or > 500)
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Enter a value of up to 500 characters."));
                }
                else if (c.Op is RuleOperators.Gt or RuleOperators.Gte or RuleOperators.Lt or RuleOperators.Lte && !TryNumber(c.Value, out _))
                {
                    problems.Add(new($"{field}.value", "value-invalid", "Enter a number to compare with."));
                }
            }
            else
            {
                problems.Add(new($"{field}.field", "field-unknown", "Choose a known field."));
            }
        }
    }

    /// <summary>Checks the shape of approval levels.</summary>
    public static void CheckLevels(IReadOnlyList<ApprovalLevelDefinition> levels, string at, List<FormProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(problems);
        if (levels.Count > MaxItems)
        {
            problems.Add(new(at, "too-many", $"At most {MaxItems} levels."));
        }

        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var field = $"{at}.{i}";
            if (!level.Name.HasAnyValue)
            {
                problems.Add(new($"{field}.name", "name-required", "Name the level in English or Arabic."));
            }

            if ((level.Name.En?.Length ?? 0) > 150 || (level.Name.Ar?.Length ?? 0) > 150)
            {
                problems.Add(new($"{field}.name", "text-too-long", "Use at most 150 characters in each language."));
            }

            if (level.Approvers.Count == 0 || level.Approvers.Count > MaxItems)
            {
                problems.Add(new($"{field}.approvers", "approvers-required", $"Name 1 to {MaxItems} approvers."));
            }

            for (var j = 0; j < level.Approvers.Count; j++)
            {
                var approver = level.Approvers[j];
                var ok = approver.Kind switch
                {
                    ApproverKind.RequesterManager => true,
                    ApproverKind.User or ApproverKind.Group or ApproverKind.GroupLeads => approver.Id is not null,
                    ApproverKind.Role => !string.IsNullOrWhiteSpace(approver.Key) && approver.Key.Length <= 60,
                    ApproverKind.AnswerUser => approver.Key is { Length: > 0 and <= 60 } k && k.All(char.IsAsciiLetterOrDigit),
                    ApproverKind.Permission => approver.Key is { Length: > 0 and <= 60 } p && p.All(c => char.IsAsciiLetterLower(c) || c == '.'),
                    _ => false,
                };
                if (!ok)
                {
                    problems.Add(new($"{field}.approvers.{j}", "approver-invalid", "Choose who approves."));
                }
            }

            CheckConditions(level.Conditions, $"{field}.conditions", problems);
        }
    }

    /// <summary>Checks a notify action's recipients and wording.</summary>
    public static void CheckNotify(NotifyAction action, string at, List<FormProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(problems);
        if (action.Recipients.Count == 0 || action.Recipients.Count > MaxItems)
        {
            problems.Add(new($"{at}.recipients", "recipients-required", $"Choose 1 to {MaxItems} recipients."));
        }

        for (var i = 0; i < action.Recipients.Count; i++)
        {
            if (!IsRecipient(action.Recipients[i]))
            {
                problems.Add(new($"{at}.recipients.{i}", "recipient-invalid", "Choose who to tell."));
            }
        }

        if (action.Channels is { } channels && (channels.Count != channels.Distinct().Count() || channels.Any(c => !Enum.IsDefined(c))))
        {
            problems.Add(new($"{at}.channels", "channels-invalid", "Choose WhatsApp, SMS or Teams, each at most once."));
        }

        if (!action.Subject.HasAnyValue || !action.Body.HasAnyValue)
        {
            problems.Add(new($"{at}.subject", "wording-required", "Write the subject and the message in English or Arabic."));
        }

        foreach (var (text, field, max) in new[] { (action.Subject, "subject", MaxSubject), (action.Body, "body", MaxBody) })
        {
            foreach (var value in new[] { text.En, text.Ar })
            {
                if (value is null)
                {
                    continue;
                }

                if (value.Length > max)
                {
                    problems.Add(new($"{at}.{field}", "text-too-long", $"Use at most {max} characters in each language."));
                }

                foreach (var problem in Zelvo.Notifications.TemplateSyntax.Problems(value, NotifyPlaceholders))
                {
                    problems.Add(new($"{at}.{field}", "placeholder-unknown", problem));
                }
            }
        }
    }

    /// <summary>Whether <paramref name="recipient"/> is a known recipient expression.</summary>
    public static bool IsRecipient(string? recipient) => recipient switch
    {
        "requester" or "requesterManager" or "assignee" or "group" or "groupLeads" => true,
        { } r when r.StartsWith("user:", StringComparison.Ordinal) || r.StartsWith("group:", StringComparison.Ordinal)
            || r.StartsWith("groupLeads:", StringComparison.Ordinal) => Guid.TryParse(r[(r.IndexOf(':') + 1)..], out _),
        { } r when r.StartsWith("role:", StringComparison.Ordinal) => r.Length is > 5 and <= 65,
        _ => false,
    };

    /// <summary>Values of a list condition as text.</summary>
    public static List<string>? Values(JsonNode? value) => value switch
    {
        JsonArray array => [.. array.Select(Text).Where(t => t.Length > 0)],
        JsonValue single when Text(single) is { Length: > 0 } t => [t],
        _ => null,
    };

    private static bool InList(WorkflowCondition condition, IReadOnlyList<string> actual)
    {
        var wanted = Values(condition.Value) ?? [];
        var any = actual.Any(a => wanted.Contains(a, StringComparer.OrdinalIgnoreCase));
        return condition.Op == RuleOperators.NotIn ? !any : any;
    }

    private static bool Compare(string op, decimal actual, decimal? wanted) => wanted is { } w && op switch
    {
        RuleOperators.Eq => actual == w,
        RuleOperators.Neq => actual != w,
        RuleOperators.Gt => actual > w,
        RuleOperators.Gte => actual >= w,
        RuleOperators.Lt => actual < w,
        RuleOperators.Lte => actual <= w,
        _ => false,
    };

    private static decimal? Number(JsonNode? value) => TryNumber(value, out var n) ? n : null;

    private static bool TryNumber(JsonNode? value, out decimal number)
    {
        number = 0;
        if (value is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue<decimal>(out number))
        {
            return true;
        }

        if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            number = (decimal)d;
            return true;
        }

        return v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => v.ToJsonString(),
        _ => value.ToJsonString(),
    };
}
