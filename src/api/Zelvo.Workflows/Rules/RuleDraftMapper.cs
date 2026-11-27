using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Data.Channels;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets.Forms;

namespace Zelvo.Workflows.Rules;

/// <summary>
/// The tenant's own lists offered to the AI by reference (task K1): <c>s1</c> for services,
/// <c>c1</c> categories, <c>l1</c> locations, <c>g1</c> support groups, <c>d1</c> departments,
/// <c>v1</c> customer groups and <c>r1</c> roles. Only these references are accepted back, so the
/// AI can never name an id, a person or anything the tenant does not have.
/// </summary>
public sealed class RuleDraftReferences
{
    /// <summary>Services, categories, locations, groups, departments and customer groups by reference.</summary>
    public Dictionary<string, Guid> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Role keys by reference.</summary>
    public Dictionary<string, string> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The reference prefix of each id-list field.</summary>
    public static IReadOnlyDictionary<string, char> Prefixes { get; } = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        [RuleFields.Service] = 's',
        [RuleFields.Category] = 'c',
        [RuleFields.Location] = 'l',
        [RuleFields.Group] = 'g',
        [RuleFields.RequesterDepartment] = 'd',
        [RuleFields.RequesterCustomerGroup] = 'v',
    };

    /// <summary>Adds an item and returns its reference, for example <c>g3</c>.</summary>
    public string Add(char prefix, Guid id)
    {
        var reference = $"{prefix}{Ids.Keys.Count(k => k[0] == prefix) + 1}";
        Ids[reference] = id;
        return reference;
    }

    /// <summary>Adds a role and returns its reference.</summary>
    public string AddRole(string key)
    {
        var reference = $"r{Roles.Count + 1}";
        Roles[reference] = key;
        return reference;
    }

    /// <summary>The id of <paramref name="reference"/> when it has <paramref name="prefix"/>.</summary>
    public Guid? Resolve(char prefix, string? reference) =>
        reference is { Length: > 1 } r && char.ToLowerInvariant(r[0]) == prefix && Ids.TryGetValue(r.Trim(), out var id) ? id : null;
}

/// <summary>A rule draft read from the AI's answer: only what fits the builder, and what did not.</summary>
/// <param name="Name">Name in English and Arabic.</param>
/// <param name="Trigger">When.</param>
/// <param name="RecordType">Record type, or every type.</param>
/// <param name="Match">All or any conditions.</param>
/// <param name="Conditions">Conditions that passed the checks.</param>
/// <param name="Actions">Actions that passed the checks.</param>
/// <param name="StopProcessing">Stop later rules.</param>
/// <param name="Unmapped">What the AI could not express, and what Zelvo left out, as short phrases.</param>
public sealed record RuleDraft(
    LocalizedText Name, WorkflowTrigger Trigger, TicketType? RecordType, ConditionMatch Match,
    IReadOnlyList<WorkflowCondition> Conditions, IReadOnlyList<WorkflowAction> Actions, bool StopProcessing, IReadOnlyList<string> Unmapped);

/// <summary>
/// Turns the AI's answer for a plain-language rule (task K1, module 20) into a rule draft. The
/// answer is untrusted: every condition and action is read field by field, references are
/// resolved only from <see cref="RuleDraftReferences"/>, and each item is checked with the same
/// checks as the builder (<see cref="RuleDocuments"/>). Anything unknown is left out and listed,
/// never accepted.
/// </summary>
public static class RuleDraftMapper
{
    /// <summary>Most phrases kept in <see cref="RuleDraft.Unmapped"/>.</summary>
    public const int MaxUnmapped = 20;

    /// <summary>Longest phrase in <see cref="RuleDraft.Unmapped"/>.</summary>
    public const int MaxPhrase = 200;

    private static readonly string[] FixedRecipients = ["requester", "requesterManager", "assignee", "group", "groupLeads"];

    /// <summary>
    /// Reads <paramref name="answer"/>. Returns <see langword="null"/> when nothing usable is left
    /// (no action passed the checks); <paramref name="unmapped"/> then says why.
    /// </summary>
    public static RuleDraft? Map(JsonElement answer, RuleDraftReferences references, string sourceText, out IReadOnlyList<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(references);
        var left = new List<string>();
        if (answer.ValueKind != JsonValueKind.Object)
        {
            left.Add("The AI's answer was not a rule.");
            unmapped = left;
            return null;
        }

        if (answer.TryGetProperty("unmapped", out var reported) && reported.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reported.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } phrase)
                {
                    left.Add(phrase);
                }
            }
        }

        var trigger = WorkflowTrigger.TicketCreated;
        if (Text(answer, "trigger") is { } t && Enum.TryParse<WorkflowTrigger>(t, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
        {
            trigger = parsed;
        }
        else
        {
            left.Add("When the rule runs was not clear: \"a ticket is created\" was chosen.");
        }

        TicketType? recordType = null;
        if (Text(answer, "recordType") is { } rt)
        {
            if (rt is nameof(TicketType.Incident) or nameof(TicketType.ServiceRequest))
            {
                recordType = Enum.Parse<TicketType>(rt);
            }
            else
            {
                left.Add($"Record type \"{rt}\": the rule applies to incidents and service requests.");
            }
        }

        var match = Text(answer, "match") == nameof(ConditionMatch.Any) ? ConditionMatch.Any : ConditionMatch.All;

        var conditions = new List<WorkflowCondition>();
        if (answer.TryGetProperty("conditions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(RuleDocuments.MaxItems))
            {
                if (Condition(item, references, left) is { } condition)
                {
                    conditions.Add(condition);
                }
            }
        }

        var actions = new List<WorkflowAction>();
        if (answer.TryGetProperty("actions", out var actionList) && actionList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in actionList.EnumerateArray().Take(RuleDocuments.MaxItems))
            {
                if (Action(item, references, left) is { } action)
                {
                    actions.Add(action);
                }
            }
        }

        var name = Bilingual(answer, "name", WorkflowRule.NameMaxLength);
        if (!name.HasAnyValue)
        {
            var fallback = sourceText.Trim().ReplaceLineEndings(" ");
            fallback = fallback.Length > 80 ? fallback[..80] + "…" : fallback;
            name = AiLanguage(fallback) == LocalizedText.Arabic ? new LocalizedText(null, fallback) : new LocalizedText(fallback, null);
        }

        unmapped = [.. left.Select(Phrase).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxUnmapped)];
        if (actions.Count == 0)
        {
            unmapped = [.. unmapped.Append("No action could be built from the description.").Distinct(StringComparer.Ordinal).Take(MaxUnmapped)];
            return null;
        }

        var stop = answer.TryGetProperty("stopProcessing", out var s) && s.ValueKind == JsonValueKind.True;
        return new RuleDraft(name, trigger, recordType, match, conditions, actions, stop, unmapped);
    }

    private static WorkflowCondition? Condition(JsonElement item, RuleDraftReferences references, List<string> left)
    {
        var field = item.ValueKind == JsonValueKind.Object ? Text(item, "field") : null;
        var op = item.ValueKind == JsonValueKind.Object ? Text(item, "op") : null;
        if (field is null || op is null)
        {
            left.Add("A condition without a field or operator was left out.");
            return null;
        }

        if (RuleFields.AnswerKeyOf(field) is not null)
        {
            left.Add($"Condition on the form answer \"{field[RuleFields.AnswerPrefix.Length..]}\": add it in the builder.");
            return null;
        }

        JsonNode? value = null;
        if (RuleDraftReferences.Prefixes.TryGetValue(field, out var prefix))
        {
            var ids = new JsonArray();
            foreach (var reference in Values(item))
            {
                if (references.Resolve(prefix, reference) is { } id)
                {
                    ids.Add(id.ToString());
                }
                else
                {
                    left.Add($"Condition on {field}: \"{reference}\" is not one of the tenant's items.");
                }
            }

            if (ids.Count == 0)
            {
                return null;
            }

            value = ids;
        }
        else if (RuleFields.NameLists.Contains(field))
        {
            value = new JsonArray([.. Values(item).Select(v => (JsonNode?)JsonValue.Create(v))]);
        }
        else if (RuleFields.Numbers.Contains(field) && item.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDecimal(out var n))
        {
            value = JsonValue.Create(n);
        }

        var condition = new WorkflowCondition { Field = field, Op = op, Value = value };
        var problems = new List<FormProblem>();
        RuleDocuments.CheckConditions([condition], "condition", problems);
        if (problems.Count > 0)
        {
            left.Add($"Condition on {field} ({op}) does not fit the builder and was left out.");
            return null;
        }

        return condition;
    }

    private static WorkflowAction? Action(JsonElement item, RuleDraftReferences references, List<string> left)
    {
        var type = item.ValueKind == JsonValueKind.Object ? Text(item, "type") : null;
        switch (type)
        {
            case "assign":
                if (references.Resolve('g', Text(item, "group")) is not { } group)
                {
                    left.Add("Assign: the group is not one of the tenant's groups.");
                    return null;
                }

                return new AssignAction
                {
                    GroupId = group,
                    Method = Text(item, "method") == nameof(AssignmentMethod.RoundRobin) ? AssignmentMethod.RoundRobin : AssignmentMethod.GroupQueue,
                };
            case "notify":
                return Notify(item, references, left);
            case null:
                left.Add("An action without a type was left out.");
                return null;
            default:
                left.Add($"Action \"{Phrase(type)}\": build it in the builder.");
                return null;
        }
    }

    private static NotifyAction? Notify(JsonElement item, RuleDraftReferences references, List<string> left)
    {
        var recipients = new List<string>();
        foreach (var recipient in Values(item, "recipients"))
        {
            var colon = recipient.IndexOf(':', StringComparison.Ordinal);
            var resolved = colon < 0
                ? FixedRecipients.FirstOrDefault(r => string.Equals(r, recipient, StringComparison.Ordinal))
                : recipient[..colon] switch
                {
                    "group" when references.Resolve('g', recipient[(colon + 1)..]) is { } g => $"group:{g}",
                    "groupLeads" when references.Resolve('g', recipient[(colon + 1)..]) is { } g => $"groupLeads:{g}",
                    "role" when references.Roles.TryGetValue(recipient[(colon + 1)..].Trim(), out var key) => $"role:{key}",
                    _ => null,
                };
            if (resolved is null)
            {
                left.Add($"Recipient \"{recipient}\" is not one the builder knows.");
            }
            else if (!recipients.Contains(resolved, StringComparer.Ordinal))
            {
                recipients.Add(resolved);
            }
        }

        var channels = new List<MessagingKind>();
        foreach (var channel in Values(item, "channels"))
        {
            if (Enum.TryParse<MessagingKind>(channel, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
            {
                if (!channels.Contains(kind))
                {
                    channels.Add(kind);
                }
            }
            else
            {
                left.Add($"Channel \"{channel}\": only WhatsApp, SMS and Teams are available besides the app and email.");
            }
        }

        if (recipients.Count == 0)
        {
            left.Add("Notify: nobody to tell could be found.");
            return null;
        }

        var action = new NotifyAction
        {
            Recipients = recipients,
            Subject = Bilingual(item, "subject", RuleDocuments.MaxSubject),
            Body = Bilingual(item, "body", RuleDocuments.MaxBody),
            Email = !(item.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.False),
            Channels = channels,
        };
        var problems = new List<FormProblem>();
        RuleDocuments.CheckNotify(action, "notify", problems);
        if (problems.Count > 0)
        {
            left.Add("Notify: the wording does not fit the builder (placeholders or length); write it in the builder.");
            return null;
        }

        return action;
    }

    private static IEnumerable<string> Values(JsonElement item, string property = "value")
    {
        if (!item.TryGetProperty(property, out var value))
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single)
        {
            yield return single.Trim();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in value.EnumerateArray().Take(100))
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text)
                {
                    yield return text.Trim();
                }
            }
        }
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    private static LocalizedText Bilingual(JsonElement item, string property, int max)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new LocalizedText(null, null);
        }

        string? Part(string language) => Text(value, language) is { } text ? (text.Length > max ? text[..max] : text) : null;
        return new LocalizedText(Part(LocalizedText.English), Part(LocalizedText.Arabic));
    }

    private static string Phrase(string text)
    {
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > MaxPhrase ? clean[..MaxPhrase] + "…" : clean;
    }

    /// <summary>Arabic when the text has Arabic letters, else English.</summary>
    public static string AiLanguage(string? text) =>
        text?.Any(c => c is >= '؀' and <= 'ۿ') == true ? LocalizedText.Arabic : LocalizedText.English;
}
