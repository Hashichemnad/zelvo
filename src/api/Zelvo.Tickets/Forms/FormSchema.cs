using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zelvo.Data.Localization;

namespace Zelvo.Tickets.Forms;

/// <summary>Kinds of field the form builder offers (module 2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    /// <summary>Text on one line, or several lines with <see cref="FormField.Multiline"/>.</summary>
    Text,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A date (<c>YYYY-MM-DD</c>).</summary>
    Date,

    /// <summary>One option from a list, or several with <see cref="FormField.Multiple"/>.</summary>
    Dropdown,

    /// <summary>A single tick box (yes or no).</summary>
    Checkbox,

    /// <summary>One option, shown as radio buttons.</summary>
    Radio,

    /// <summary>Files, uploaded and scanned like attachments.</summary>
    File,

    /// <summary>A user of the organisation.</summary>
    User,

    /// <summary>A node of the location tree.</summary>
    Location,

    /// <summary>A signature: drawn (an uploaded PNG) or typed with a confirmation.</summary>
    Signature,

    /// <summary>Rows of answers with the same columns.</summary>
    Table,

    /// <summary>Formatted text in Markdown (never rendered as HTML).</summary>
    RichText,

    /// <summary>
    /// A configuration item of the CMDB (task G1, the picker C1 deferred to module 18), or several
    /// with <see cref="FormField.Multiple"/>; <see cref="FormField.ConfigurationItemClasses"/> limits
    /// the classes. Picked items are linked to the ticket.
    /// </summary>
    ConfigurationItem,
}

/// <summary>Formats a text field can be checked against.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldFormat>))]
public enum FieldFormat
{
    /// <summary>15 digits starting with 784, with a valid check digit.</summary>
    EmiratesId,

    /// <summary>A phone number in international form; UAE numbers may be written locally.</summary>
    Phone,

    /// <summary>An email address.</summary>
    Email,

    /// <summary>An IBAN with a valid check (UAE IBANs have 23 characters).</summary>
    Iban,
}

/// <summary>Values a field can take from the requester's profile.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileSource>))]
public enum ProfileSource
{
    /// <summary>Display name.</summary>
    DisplayName,

    /// <summary>Email address.</summary>
    Email,

    /// <summary>Phone number.</summary>
    Phone,

    /// <summary>Emirates ID number.</summary>
    EmiratesId,

    /// <summary>Employee number.</summary>
    EmployeeId,

    /// <summary>Department (its name).</summary>
    Department,

    /// <summary>Manager (a user).</summary>
    Manager,

    /// <summary>Location (a node of the location tree).</summary>
    Location,
}

/// <summary>What a rule does to its field when it matches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleAction>))]
public enum RuleAction
{
    /// <summary>The field is shown only when a show rule matches.</summary>
    Show,

    /// <summary>The field is hidden when the rule matches.</summary>
    Hide,

    /// <summary>The field must be answered when the rule matches.</summary>
    Require,

    /// <summary>The field cannot be changed when the rule matches.</summary>
    ReadOnly,
}

/// <summary>How a rule's conditions combine.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleMatch>))]
public enum RuleMatch
{
    /// <summary>Every condition holds.</summary>
    All,

    /// <summary>At least one condition holds.</summary>
    Any,
}

/// <summary>Comparisons a condition can make with another field's answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConditionOperator>))]
public enum ConditionOperator
{
    /// <summary>The answer is the value (for several choices: includes it).</summary>
    Equals,

    /// <summary>The answer is not the value.</summary>
    NotEquals,

    /// <summary>The text contains the value (ignoring case), or the choices include it.</summary>
    Contains,

    /// <summary>Not answered.</summary>
    Empty,

    /// <summary>Answered.</summary>
    NotEmpty,

    /// <summary>A number or date after the value.</summary>
    GreaterThan,

    /// <summary>A number or date before the value.</summary>
    LessThan,

    /// <summary>The tick box is ticked.</summary>
    Checked,

    /// <summary>The tick box is not ticked.</summary>
    NotChecked,
}

/// <summary>An option of a dropdown or radio field.</summary>
/// <param name="Value">Stored value, stable when the label changes.</param>
/// <param name="Label">Label in English and Arabic.</param>
public sealed record FieldOption(string Value, LocalizedText Label);

/// <summary>Checks on an answer beyond required.</summary>
public sealed record FieldValidation
{
    /// <summary>Smallest number.</summary>
    public decimal? Min { get; init; }

    /// <summary>Largest number.</summary>
    public decimal? Max { get; init; }

    /// <summary>Fewest characters.</summary>
    public int? MinLength { get; init; }

    /// <summary>Most characters.</summary>
    public int? MaxLength { get; init; }

    /// <summary>A regular expression the whole text must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>A known format to check.</summary>
    public FieldFormat? Format { get; init; }

    /// <summary>Earliest date (<c>YYYY-MM-DD</c>).</summary>
    public string? MinDate { get; init; }

    /// <summary>Latest date (<c>YYYY-MM-DD</c>).</summary>
    public string? MaxDate { get; init; }

    /// <summary>File extensions accepted (a subset of the tenant's allowlist); empty: the allowlist.</summary>
    public IReadOnlyList<string>? FileTypes { get; init; }

    /// <summary>Largest file in MB (at most the tenant's limit).</summary>
    public int? MaxFileSizeMb { get; init; }

    /// <summary>Most files.</summary>
    public int? MaxFiles { get; init; }
}

/// <summary>A condition on another field's answer.</summary>
/// <param name="Field">Key of the other field.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">What to compare with, as text (numbers and dates too); not used by Empty, NotEmpty, Checked, NotChecked.</param>
public sealed record RuleCondition(string Field, ConditionOperator Operator, string? Value = null);

/// <summary>A rule on a field: show, hide, require or make read-only when its conditions hold.</summary>
/// <param name="Action">What happens when it matches.</param>
/// <param name="Match">All or any of the conditions.</param>
/// <param name="Conditions">The conditions (at least one).</param>
public sealed record FieldRule(RuleAction Action, RuleMatch Match, IReadOnlyList<RuleCondition> Conditions);

/// <summary>A field of a form or field set.</summary>
public sealed record FormField
{
    /// <summary>Key of the answer: a letter, then letters and digits (camelCase), unique in the form.</summary>
    public required string Key { get; init; }

    /// <summary>Kind of field.</summary>
    public FieldType Type { get; init; }

    /// <summary>Label in English and Arabic.</summary>
    public required LocalizedText Label { get; init; }

    /// <summary>Help text under the field.</summary>
    public LocalizedText? Help { get; init; }

    /// <summary>Placeholder in empty text, number and date fields.</summary>
    public LocalizedText? Placeholder { get; init; }

    /// <summary>Always required (rules can require it too).</summary>
    public bool Required { get; init; }

    /// <summary>Always read-only: keeps its default (rules can make it read-only too).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The answer is personal data: kept apart, masked in the audit log and before AI. Always on for Emirates ID, phone, email and IBAN formats and for signatures.</summary>
    public bool PersonalData { get; init; }

    /// <summary>Text: several lines.</summary>
    public bool Multiline { get; init; }

    /// <summary>Dropdown and configuration item: several choices.</summary>
    public bool Multiple { get; init; }

    /// <summary>
    /// Configuration item: the classes items may be picked from (each with the classes below it);
    /// empty or <see langword="null"/>: any class. People without <c>cmdb.read</c> only ever see
    /// items of classes marked for requesters.
    /// </summary>
    public IReadOnlyList<Guid>? ConfigurationItemClasses { get; init; }

    /// <summary>Dropdown and radio: the options.</summary>
    public IReadOnlyList<FieldOption>? Options { get; init; }

    /// <summary>
    /// Task K3: dropdown and radio fields take their options from this data list (its key) instead
    /// of options of their own; the published form gets the list's active items as options when it
    /// is filled in, so answers are checked against them like any options.
    /// </summary>
    public string? DataList { get; init; }

    /// <summary>Checks on the answer.</summary>
    public FieldValidation? Validation { get; init; }

    /// <summary>Default answer, in the field's answer form.</summary>
    public JsonNode? DefaultValue { get; init; }

    /// <summary>Default from the requester's profile (wins over <see cref="DefaultValue"/> when the profile has a value).</summary>
    public ProfileSource? ProfileDefault { get; init; }

    /// <summary>Table: its columns (text, number, date, dropdown or checkbox; no rules).</summary>
    public IReadOnlyList<FormField>? Columns { get; init; }

    /// <summary>Table: fewest rows.</summary>
    public int? MinRows { get; init; }

    /// <summary>Table: most rows.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Rules on this field.</summary>
    public IReadOnlyList<FieldRule>? Rules { get; init; }

    /// <summary>Whether answers count as personal data, whatever <see cref="PersonalData"/> says.</summary>
    [JsonIgnore]
    public bool IsPersonalData =>
        PersonalData || Type == FieldType.Signature || Validation?.Format is not null
        || ProfileDefault is ProfileSource.Email or ProfileSource.Phone or ProfileSource.EmiratesId;
}

/// <summary>Kinds of element in a form.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FormElementType>))]
public enum FormElementType
{
    /// <summary>A field of the form itself.</summary>
    Field,

    /// <summary>The fields of a field set, as currently published.</summary>
    FieldSet,
}

/// <summary>An element of a form: a field, or a reference to a field set.</summary>
public sealed record FormElement
{
    /// <summary>Field or field set.</summary>
    public FormElementType Type { get; init; }

    /// <summary>The field (for <see cref="FormElementType.Field"/>).</summary>
    public FormField? Field { get; init; }

    /// <summary>The field set (for <see cref="FormElementType.FieldSet"/>).</summary>
    public Guid? FieldSetId { get; init; }
}

/// <summary>What a form holds, in order.</summary>
public sealed record FormSchema
{
    /// <summary>Elements in order.</summary>
    public IReadOnlyList<FormElement> Elements { get; init; } = [];
}

/// <summary>What a field set holds, in order.</summary>
public sealed record FieldSetSchema
{
    /// <summary>Fields in order.</summary>
    public IReadOnlyList<FormField> Fields { get; init; } = [];
}

/// <summary>A field of a resolved form, with the field set it came from.</summary>
/// <param name="Field">The field.</param>
/// <param name="FieldSetId">The field set, or <see langword="null"/> for the form's own fields.</param>
public sealed record ResolvedField(FormField Field, Guid? FieldSetId);

/// <summary>JSON settings for schemas and answers.</summary>
public static class FormJson
{
    /// <summary>camelCase, enums as their names, nulls left out.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
    };

    /// <summary>Reads a form schema; <see langword="null"/> when it is not valid JSON of that shape.</summary>
    public static FormSchema? ReadForm(string json) => Read<FormSchema>(json);

    /// <summary>Reads a field set schema; <see langword="null"/> when it is not valid JSON of that shape.</summary>
    public static FieldSetSchema? ReadFieldSet(string json) => Read<FieldSetSchema>(json);

    /// <summary>Reads resolved fields.</summary>
    public static IReadOnlyList<ResolvedField> ReadResolved(string json) => Read<List<ResolvedField>>(json) ?? [];

    /// <summary>Writes a value as JSON.</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
