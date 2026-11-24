using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zelvo.Tickets.Forms;

/// <summary>What the rules make of a field for the current answers.</summary>
/// <param name="Visible">Shown and kept; hidden fields' answers are dropped.</param>
/// <param name="Required">Must be answered.</param>
/// <param name="ReadOnly">Keeps its default; what was sent is ignored.</param>
public sealed record FieldState(bool Visible, bool Required, bool ReadOnly);

/// <summary>
/// Evaluates field rules (module 2: show, hide, require or make read-only based on other answers).
/// Fields are evaluated in an order where every field comes after the fields its rules look at
/// (rules form no cycles; publishing checks that), and a hidden field counts as not answered.
///
/// <list type="bullet">
/// <item>A field with show rules is visible only when one of them matches; a matching hide rule hides it.</item>
/// <item>A visible field is required when it is marked required or a require rule matches.</item>
/// <item>A field is read-only when it is marked read-only or a read-only rule matches.</item>
/// </list>
///
/// The web app evaluates the same rules as people type (<c>src/web/src/lib/forms/rules.ts</c>);
/// the server always evaluates them again. Shared test cases (<c>tests/shared/form-rule-cases.json</c>) keep the two equal.
/// </summary>
public static class FormRules
{
    /// <summary>The state of every field for <paramref name="answers"/>.</summary>
    public static IReadOnlyDictionary<string, FieldState> Evaluate(IReadOnlyList<FormField> fields, JsonObject answers)
    {
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var states = new Dictionary<string, FieldState>(StringComparer.Ordinal);
        foreach (var field in Order(fields) ?? fields)
        {
            var rules = field.Rules ?? [];
            bool Matches(FieldRule rule)
            {
                bool Holds(RuleCondition c)
                {
                    var visible = !byKey.ContainsKey(c.Field) || (states.TryGetValue(c.Field, out var s) && s.Visible);
                    return Test(c, visible ? answers[c.Field] : null);
                }

                return rule.Conditions.Count > 0 && (rule.Match == RuleMatch.All ? rule.Conditions.All(Holds) : rule.Conditions.Any(Holds));
            }

            var shows = rules.Where(r => r.Action == RuleAction.Show).ToList();
            var visibleNow = (shows.Count == 0 || shows.Any(Matches)) && !rules.Any(r => r.Action == RuleAction.Hide && Matches(r));
            var required = visibleNow && (field.Required || rules.Any(r => r.Action == RuleAction.Require && Matches(r)));
            var readOnly = field.ReadOnly || rules.Any(r => r.Action == RuleAction.ReadOnly && Matches(r));
            states[field.Key] = new FieldState(visibleNow, required, readOnly);
        }

        return states;
    }

    /// <summary>Fields ordered so each comes after the fields its rules refer to; <see langword="null"/> when the rules form a cycle.</summary>
    public static IReadOnlyList<FormField>? Order(IReadOnlyList<FormField> fields)
    {
        var byKey = fields.GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ordered = new List<FormField>(fields.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        bool Visit(FormField field)
        {
            if (state.TryGetValue(field.Key, out var s))
            {
                return s == 2;
            }

            state[field.Key] = 1;
            foreach (var dependency in Dependencies(field))
            {
                if (byKey.TryGetValue(dependency, out var other) && !Visit(other))
                {
                    return false;
                }
            }

            state[field.Key] = 2;
            ordered.Add(field);
            return true;
        }

        foreach (var field in byKey.Values)
        {
            if (!Visit(field))
            {
                return null;
            }
        }

        return ordered;
    }

    /// <summary>Keys of the fields the field's rules look at.</summary>
    public static IEnumerable<string> Dependencies(FormField field) =>
        (field.Rules ?? []).SelectMany(r => r.Conditions).Select(c => c.Field).Distinct(StringComparer.Ordinal);

    /// <summary>Whether an answer counts as not given: missing, blank text, no choices, an unticked box or an empty object.</summary>
    public static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.False => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => true,
        _ => false,
    };

    /// <summary>Whether a condition holds for an answer (<see langword="null"/> when the field is hidden or not answered).</summary>
    public static bool Test(RuleCondition condition, JsonNode? value)
    {
        var expected = condition.Value ?? string.Empty;
        return condition.Operator switch
        {
            ConditionOperator.Empty => IsEmpty(value),
            ConditionOperator.NotEmpty => !IsEmpty(value),
            ConditionOperator.Checked => IsTrue(value),
            ConditionOperator.NotChecked => !IsTrue(value),
            ConditionOperator.Equals => !IsEmpty(value) && EqualsValue(value!, expected),
            ConditionOperator.NotEquals => IsEmpty(value) || !EqualsValue(value!, expected),
            ConditionOperator.Contains => !IsEmpty(value) && ContainsValue(value!, expected),
            ConditionOperator.GreaterThan => Compare(value, expected) is > 0,
            ConditionOperator.LessThan => Compare(value, expected) is < 0,
            _ => false,
        };
    }

    private static bool IsTrue(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static bool EqualsValue(JsonNode value, string expected)
    {
        if (value is JsonArray array)
        {
            return array.Any(item => item is not null && EqualsValue(item, expected));
        }

        if (value is not JsonValue v)
        {
            return false;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.Number => decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && v.GetValue<decimal>() == number,
            JsonValueKind.True => string.Equals(expected, "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => string.Equals(expected, "false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.String => string.Equals(v.GetValue<string>().Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool ContainsValue(JsonNode value, string expected)
    {
        if (value is JsonArray array)
        {
            return array.Any(item => item is not null && EqualsValue(item, expected));
        }

        return value is JsonValue v && v.GetValueKind() == JsonValueKind.String
            && v.GetValue<string>().Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Numbers compare as numbers, dates (<c>YYYY-MM-DD</c>) as dates; anything else does not compare.</summary>
    private static int? Compare(JsonNode? value, string expected)
    {
        if (value is not JsonValue v)
        {
            return null;
        }

        if (v.GetValueKind() == JsonValueKind.Number)
        {
            return decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? v.GetValue<decimal>().CompareTo(number) : null;
        }

        if (v.GetValueKind() == JsonValueKind.String
            && DateOnly.TryParseExact(v.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && DateOnly.TryParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var other))
        {
            return date.CompareTo(other);
        }

        return null;
    }
}
