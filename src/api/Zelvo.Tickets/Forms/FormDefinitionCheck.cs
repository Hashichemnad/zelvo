using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zelvo.Tickets.Forms;

/// <summary>A problem in a form or field set that stops it from being published.</summary>
/// <param name="Field">Key of the field concerned, if any.</param>
/// <param name="Code">Stable code, for example <c>options-required</c>.</param>
/// <param name="Message">English explanation (the web app shows its own translated text by code).</param>
public sealed record FormProblem(string? Field, string Code, string Message);

/// <summary>
/// Checks a form's fields before it is published: keys, labels, options, limits, defaults and
/// rules (they refer to other fields of the form and form no cycles). Drafts may be saved with
/// problems; they are listed so the builder can show them.
/// </summary>
public static partial class FormDefinitionCheck
{
    /// <summary>Most fields in a resolved form.</summary>
    public const int MaxFields = 200;

    /// <summary>Most options of a dropdown or radio field.</summary>
    public const int MaxOptions = 500;

    /// <summary>Most rules on one field, and conditions in one rule.</summary>
    public const int MaxRules = 20;

    /// <summary>Most columns of a table and rows of an answer.</summary>
    public const int MaxColumns = 20;

    /// <summary>Most rows of a table answer.</summary>
    public const int MaxTableRows = 100;

    /// <summary>Longest label, help text or option label in each language.</summary>
    public const int MaxLabelLength = 500;

    /// <summary>Longest pattern.</summary>
    public const int MaxPatternLength = 500;

    /// <summary>Longest text answer (single line, several lines, rich text).</summary>
    public static int MaxTextLength(FormField field) => field.Type == FieldType.RichText ? 20_000 : field.Multiline ? 10_000 : 2_000;

    private static readonly FieldType[] ColumnTypes = [FieldType.Text, FieldType.Number, FieldType.Date, FieldType.Dropdown, FieldType.Checkbox];

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>Keys of data lists (task K3).</summary>
    [GeneratedRegex("^[a-z][a-zA-Z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    public static partial Regex DataListKeyPattern();

    /// <summary>Checks the resolved fields of a form (its own fields and those of its field sets).</summary>
    public static List<FormProblem> Check(IReadOnlyList<FormField> fields)
    {
        var problems = new List<FormProblem>();
        if (fields.Count > MaxFields)
        {
            problems.Add(new(null, "too-many-fields", $"A form has at most {MaxFields} fields."));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!keys.Add(field.Key))
            {
                problems.Add(new(field.Key, "key-duplicate", $"Two fields have the key '{field.Key}'. Keys must be unique in the form, field sets included."));
            }

            CheckField(field, problems, column: false);
        }

        var byKey = fields.GroupBy(f => f.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var condition in (field.Rules ?? []).SelectMany(r => r.Conditions))
            {
                if (condition.Field == field.Key)
                {
                    problems.Add(new(field.Key, "rule-self", "A rule cannot look at its own field."));
                }
                else if (!byKey.TryGetValue(condition.Field, out var other))
                {
                    problems.Add(new(field.Key, "rule-field-unknown", $"A rule looks at '{condition.Field}', which is not in the form."));
                }
                else
                {
                    CheckCondition(field, condition, other, problems);
                }
            }
        }

        if (problems.All(p => p.Code is not ("key-duplicate" or "rule-field-unknown" or "rule-self")) && FormRules.Order(fields) is null)
        {
            problems.Add(new(null, "rule-cycle", "Rules refer to each other in a circle; remove one of them."));
        }

        return problems;
    }

    /// <summary>Checks the fields of a field set on their own: its rules may only look at its own fields.</summary>
    public static List<FormProblem> CheckFieldSet(IReadOnlyList<FormField> fields) => Check(fields);

    private static void CheckField(FormField field, List<FormProblem> problems, bool column)
    {
        var key = field.Key ?? string.Empty;
        if (!KeyPattern().IsMatch(key))
        {
            problems.Add(new(key, "key-invalid", "Keys start with a small letter, then letters and digits only (at most 60), for example monitorSize."));
        }

        if (field.Label is null || !field.Label.HasAnyValue)
        {
            problems.Add(new(key, "label-required", "Every field needs a label in English or Arabic."));
        }

        CheckText(key, field.Label, "label", problems);
        CheckText(key, field.Help, "help", problems);
        CheckText(key, field.Placeholder, "placeholder", problems);

        if (!Enum.IsDefined(field.Type))
        {
            problems.Add(new(key, "type-unknown", "Unknown field type."));
            return;
        }

        if (column && !ColumnTypes.Contains(field.Type))
        {
            problems.Add(new(key, "column-type", "Table columns are text, number, date, dropdown or checkbox."));
        }

        if (column && (field.Rules?.Count ?? 0) > 0)
        {
            problems.Add(new(key, "column-rules", "Table columns have no rules."));
        }

        if (field.DataList is not null && (field.Type is not (FieldType.Dropdown or FieldType.Radio) || !DataListKeyPattern().IsMatch(field.DataList)))
        {
            problems.Add(new(key, "data-list-invalid", "Only dropdown and radio fields take their options from a data list, named by its key."));
        }

        if (field.Type is FieldType.Dropdown or FieldType.Radio)
        {
            var options = field.Options ?? [];
            if (options.Count == 0 && field.DataList is not null)
            {
                // Task K3: the data list's items become the options when the form is filled in.
            }
            else if (options.Count == 0)
            {
                problems.Add(new(key, "options-required", "Dropdown and radio fields need at least one option."));
            }
            else if (options.Count > MaxOptions)
            {
                problems.Add(new(key, "too-many-options", $"At most {MaxOptions} options."));
            }

            if (options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                problems.Add(new(key, "option-duplicate", "Two options have the same value."));
            }

            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option.Value) || option.Value.Length > 100)
                {
                    problems.Add(new(key, "option-value-invalid", "Option values are 1 to 100 characters."));
                }

                if (option.Label is null || !option.Label.HasAnyValue)
                {
                    problems.Add(new(key, "option-label-required", "Every option needs a label in English or Arabic."));
                }

                CheckText(key, option.Label, "option", problems);
            }
        }
        else if ((field.Options?.Count ?? 0) > 0)
        {
            problems.Add(new(key, "options-not-allowed", "Only dropdown and radio fields have options."));
        }

        if (field.Multiple && field.Type is not (FieldType.Dropdown or FieldType.ConfigurationItem))
        {
            problems.Add(new(key, "multiple-not-allowed", "Only dropdown and configuration item fields allow several choices."));
        }

        if ((field.ConfigurationItemClasses?.Count ?? 0) > 0 && field.Type != FieldType.ConfigurationItem)
        {
            problems.Add(new(key, "classes-not-allowed", "Only configuration item fields have classes."));
        }
        else if (field.ConfigurationItemClasses is { Count: > MaxOptions } || (field.ConfigurationItemClasses?.Distinct().Count() ?? 0) != (field.ConfigurationItemClasses?.Count ?? 0))
        {
            problems.Add(new(key, "classes-invalid", $"At most {MaxOptions} different classes."));
        }

        if (field.Multiline && field.Type != FieldType.Text)
        {
            problems.Add(new(key, "multiline-not-allowed", "Only text fields have several lines."));
        }

        CheckValidation(field, problems);

        if (field.Type == FieldType.Table)
        {
            var columns = field.Columns ?? [];
            if (column)
            {
                problems.Add(new(key, "column-type", "Tables cannot hold tables."));
            }
            else if (columns.Count == 0 || columns.Count > MaxColumns)
            {
                problems.Add(new(key, "columns-required", $"A table has 1 to {MaxColumns} columns."));
            }

            if (columns.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            {
                problems.Add(new(key, "key-duplicate", "Two columns have the same key."));
            }

            foreach (var c in columns)
            {
                var nested = new List<FormProblem>();
                CheckField(c, nested, column: true);
                problems.AddRange(nested.Select(p => p with { Field = $"{key}.{p.Field}" }));
            }

            if (field.MinRows is < 0 or > MaxTableRows || field.MaxRows is < 1 or > MaxTableRows
                || (field.MinRows is { } min && field.MaxRows is { } max && min > max))
            {
                problems.Add(new(key, "rows-invalid", $"Rows are between 0 and {MaxTableRows}, and the fewest is not more than the most."));
            }
        }
        else if ((field.Columns?.Count ?? 0) > 0 || field.MinRows is not null || field.MaxRows is not null)
        {
            problems.Add(new(key, "columns-not-allowed", "Only tables have columns and rows."));
        }

        if (field.ProfileDefault is { } source && !ProfileFits(field, source))
        {
            problems.Add(new(key, "profile-default-type", "This profile value does not fit this type of field."));
        }

        if (field.DefaultValue is not null)
        {
            if (field.Type is FieldType.File or FieldType.Signature or FieldType.User or FieldType.Location or FieldType.Table or FieldType.ConfigurationItem)
            {
                problems.Add(new(key, "default-not-allowed", "This type of field has no fixed default; use a profile value instead."));
            }
            else
            {
                var errors = new List<FormAnswerError>();
                FormAnswers.CheckSimple(field, field.DefaultValue, errors, key);
                if (errors.Count > 0)
                {
                    problems.Add(new(key, "default-invalid", $"The default is not a valid answer: {errors[0].Message}"));
                }
            }
        }

        if ((field.Rules?.Count ?? 0) > MaxRules)
        {
            problems.Add(new(key, "too-many-rules", $"At most {MaxRules} rules per field."));
        }

        foreach (var rule in field.Rules ?? [])
        {
            if (!Enum.IsDefined(rule.Action) || !Enum.IsDefined(rule.Match))
            {
                problems.Add(new(key, "rule-invalid", "Unknown rule action or match."));
            }

            if (rule.Conditions is null || rule.Conditions.Count == 0 || rule.Conditions.Count > MaxRules)
            {
                problems.Add(new(key, "rule-conditions", $"A rule has 1 to {MaxRules} conditions."));
            }
        }
    }

    private static void CheckValidation(FormField field, List<FormProblem> problems)
    {
        var v = field.Validation;
        if (v is null)
        {
            return;
        }

        var key = field.Key;
        if ((v.Min is not null || v.Max is not null) && field.Type != FieldType.Number)
        {
            problems.Add(new(key, "validation-not-allowed", "Smallest and largest values are for number fields."));
        }

        if (v.Min is { } min && v.Max is { } max && min > max)
        {
            problems.Add(new(key, "validation-range", "The smallest value is larger than the largest."));
        }

        if ((v.MinLength is not null || v.MaxLength is not null || v.Pattern is not null || v.Format is not null) && field.Type != FieldType.Text)
        {
            problems.Add(new(key, "validation-not-allowed", "Length, pattern and format are for text fields."));
        }

        if (v.MinLength is < 0 || v.MaxLength is < 1 || v.MaxLength > MaxTextLength(field) || (v.MinLength is { } a && v.MaxLength is { } b && a > b))
        {
            problems.Add(new(key, "validation-length", $"Lengths are between 0 and {MaxTextLength(field)}, and the shortest is not more than the longest."));
        }

        if (v.Format is { } format && !Enum.IsDefined(format))
        {
            problems.Add(new(key, "format-unknown", "Unknown format."));
        }

        if (v.Pattern is { } pattern)
        {
            if (pattern.Length > MaxPatternLength || !IsSafePattern(pattern))
            {
                problems.Add(new(key, "pattern-invalid", "The pattern is not a valid regular expression (at most 500 characters)."));
            }
        }

        if ((v.MinDate is not null || v.MaxDate is not null) && field.Type != FieldType.Date)
        {
            problems.Add(new(key, "validation-not-allowed", "Earliest and latest dates are for date fields."));
        }

        if ((v.MinDate is { } from && !IsDate(from)) || (v.MaxDate is { } to && !IsDate(to)))
        {
            problems.Add(new(key, "validation-date", "Dates are written YYYY-MM-DD."));
        }
        else if (v.MinDate is { } d1 && v.MaxDate is { } d2 && string.CompareOrdinal(d1, d2) > 0)
        {
            problems.Add(new(key, "validation-range", "The earliest date is after the latest."));
        }

        if ((v.FileTypes is not null || v.MaxFileSizeMb is not null || v.MaxFiles is not null) && field.Type != FieldType.File)
        {
            problems.Add(new(key, "validation-not-allowed", "File types, sizes and counts are for file fields."));
        }

        if (v.MaxFiles is < 1 or > 20 || v.MaxFileSizeMb is < 1 or > AttachmentPolicy.InstallationMaxSizeMb)
        {
            problems.Add(new(key, "validation-files", $"1 to 20 files of 1 to {AttachmentPolicy.InstallationMaxSizeMb} MB."));
        }

        if (v.FileTypes is { } types && types.Any(t => FileTypes.Find(t) is null))
        {
            problems.Add(new(key, "validation-file-type", "Unknown file type."));
        }
    }

    private static void CheckCondition(FormField field, RuleCondition condition, FormField other, List<FormProblem> problems)
    {
        if (!Enum.IsDefined(condition.Operator))
        {
            problems.Add(new(field.Key, "rule-invalid", "Unknown comparison."));
            return;
        }

        var needsValue = condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals or ConditionOperator.Contains
            or ConditionOperator.GreaterThan or ConditionOperator.LessThan;
        if (needsValue && string.IsNullOrWhiteSpace(condition.Value))
        {
            problems.Add(new(field.Key, "rule-value-required", $"The condition on '{other.Key}' needs a value."));
        }

        if (condition.Operator is ConditionOperator.Checked or ConditionOperator.NotChecked && other.Type != FieldType.Checkbox)
        {
            problems.Add(new(field.Key, "rule-operator", $"'{other.Key}' is not a tick box."));
        }

        if (condition.Operator is ConditionOperator.GreaterThan or ConditionOperator.LessThan)
        {
            var fits = other.Type switch
            {
                FieldType.Number => decimal.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                FieldType.Date => IsDate(condition.Value ?? string.Empty),
                _ => false,
            };
            if (!fits)
            {
                problems.Add(new(field.Key, "rule-operator", $"Before and after compare numbers or dates; '{other.Key}' or the value does not fit."));
            }
        }

        if (condition.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals && other.Type is FieldType.Dropdown or FieldType.Radio
            && other.DataList is null && !(other.Options ?? []).Any(o => o.Value == condition.Value))
        {
            problems.Add(new(field.Key, "rule-option-unknown", $"'{other.Key}' has no option '{condition.Value}'."));
        }
    }

    private static void CheckText(string key, Data.Localization.LocalizedText? text, string what, List<FormProblem> problems)
    {
        if (text is not null && ((text.En?.Length ?? 0) > MaxLabelLength || (text.Ar?.Length ?? 0) > MaxLabelLength))
        {
            problems.Add(new(key, "text-too-long", $"The {what} is longer than {MaxLabelLength} characters."));
        }
    }

    private static bool ProfileFits(FormField field, ProfileSource source) => source switch
    {
        ProfileSource.Manager => field.Type == FieldType.User,
        ProfileSource.Location => field.Type == FieldType.Location,
        _ => field.Type == FieldType.Text,
    };

    internal static bool IsDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Patterns run with a time limit, so a bad pattern cannot hold up the server; it must compile.</summary>
    private static bool IsSafePattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A value as JSON text, for messages.</summary>
    internal static string Describe(JsonNode? node) => node?.ToJsonString() ?? "nothing";

    /// <summary>The value kind of a node (<see cref="JsonValueKind.Undefined"/> when it is not a plain value).</summary>
    internal static JsonValueKind Kind(JsonNode? node) => node is JsonValue v ? v.GetValueKind() : JsonValueKind.Undefined;
}
