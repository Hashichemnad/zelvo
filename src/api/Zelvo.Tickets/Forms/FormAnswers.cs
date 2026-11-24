using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zelvo.Tickets.Forms;

/// <summary>An answer that is not accepted.</summary>
/// <param name="Field">Key of the field (<c>table.3.column</c> inside tables).</param>
/// <param name="Code">Stable code, for example <c>required</c> or <c>format-iban</c>.</param>
/// <param name="Message">English explanation (the web app shows its own translated text by code).</param>
public sealed record FormAnswerError(string Field, string Code, string Message);

/// <summary>A file answer to check against the stored file.</summary>
/// <param name="Field">The field.</param>
/// <param name="AttachmentId">The uploaded file.</param>
/// <param name="Signature">A drawn signature (a PNG image).</param>
/// <param name="Validation">The field's file rules.</param>
public sealed record FileReference(string Field, Guid AttachmentId, bool Signature, FieldValidation? Validation);

/// <summary>The checked answers, split into personal data and the rest, with what the caller must look up.</summary>
public sealed class FormAnswerResult
{
    /// <summary>Answers to fields that are not personal data.</summary>
    public JsonObject Answers { get; } = [];

    /// <summary>Answers to fields marked as personal data.</summary>
    public JsonObject PersonalAnswers { get; } = [];

    /// <summary>Problems; nothing is saved while there are any.</summary>
    public List<FormAnswerError> Errors { get; } = [];

    /// <summary>Users that must be active users of the tenant.</summary>
    public List<(string Field, Guid Id)> Users { get; } = [];

    /// <summary>Locations that must be selectable.</summary>
    public List<(string Field, Guid Id)> Locations { get; } = [];

    /// <summary>Files that must be the caller's own unsent uploads for the field.</summary>
    public List<FileReference> Files { get; } = [];
}

/// <summary>
/// Checks answers to a form on the server (module 2: required fields, formats, min/max values,
/// file type and size), after applying defaults and rules. Hidden fields' answers are dropped;
/// read-only fields keep their default whatever was sent. Answers to unknown fields are refused.
/// </summary>
public static class FormAnswers
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Checks <paramref name="submitted"/> against <paramref name="fields"/>; <paramref name="defaults"/> are the answers defaults give (profile values first).</summary>
    public static FormAnswerResult Check(IReadOnlyList<FormField> fields, JsonObject submitted, JsonObject defaults)
    {
        var result = new FormAnswerResult();
        var keys = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, _) in submitted)
        {
            if (!keys.Contains(key))
            {
                result.Errors.Add(new(key, "field-unknown", $"The form has no field '{key}'."));
            }
        }

        // Answers as the rules see them: what was sent, the default where nothing was sent.
        var working = new JsonObject();
        foreach (var field in fields)
        {
            var value = submitted.TryGetPropertyValue(field.Key, out var given) ? given : defaults[field.Key];
            working[field.Key] = value?.DeepClone();
        }

        var states = FormRules.Evaluate(fields, working);
        // Read-only fields keep their default; rules are evaluated again with those values.
        foreach (var field in fields.Where(f => states[f.Key].ReadOnly))
        {
            working[field.Key] = defaults[field.Key]?.DeepClone();
        }

        states = FormRules.Evaluate(fields, working);
        foreach (var field in fields)
        {
            var state = states[field.Key];
            var value = working[field.Key];
            if (!state.Visible || FormRules.IsEmpty(value))
            {
                if (state.Visible && state.Required)
                {
                    result.Errors.Add(new(field.Key, "required", "This field is required."));
                }

                // An unticked box is an answer too ("no"), kept when it was given.
                if (state.Visible && field.Type == FieldType.Checkbox && FormDefinitionCheck.Kind(value) == JsonValueKind.False && !state.Required)
                {
                    result.Answers[field.Key] = false;
                }

                continue;
            }

            var errors = result.Errors.Count;
            var clean = CheckValue(field, value!, result, field.Key);
            if (result.Errors.Count == errors && clean is not null)
            {
                (field.IsPersonalData ? result.PersonalAnswers : result.Answers)[field.Key] = clean;
            }
        }

        return result;
    }

    /// <summary>Checks a value of a field without references (text, number, date, choices, tick box); used for defaults and table cells.</summary>
    internal static JsonNode? CheckSimple(FormField field, JsonNode value, List<FormAnswerError> errors, string path)
    {
        var result = new FormAnswerResult();
        var clean = CheckValue(field, value, result, path);
        errors.AddRange(result.Errors);
        return clean;
    }

    private static JsonNode? CheckValue(FormField field, JsonNode value, FormAnswerResult result, string path)
    {
        void Error(string code, string message) => result.Errors.Add(new(path, code, message));
        var v = field.Validation;
        switch (field.Type)
        {
            case FieldType.Text:
            case FieldType.RichText:
            {
                if (FormDefinitionCheck.Kind(value) != JsonValueKind.String)
                {
                    Error("type", "Expected text.");
                    return null;
                }

                var text = value.GetValue<string>().Trim();
                if (!field.Multiline && field.Type == FieldType.Text && text.Contains('\n', StringComparison.Ordinal))
                {
                    text = text.ReplaceLineEndings(" ");
                }

                var max = Math.Min(v?.MaxLength ?? int.MaxValue, FormDefinitionCheck.MaxTextLength(field));
                if (text.Length > max)
                {
                    Error("max-length", $"Use at most {max} characters.");
                    return null;
                }

                if (v?.MinLength is { } min && text.Length < min)
                {
                    Error("min-length", $"Use at least {min} characters.");
                    return null;
                }

                if (v?.Format is { } format)
                {
                    var normal = FieldFormats.Normalize(format, text);
                    if (normal is null)
                    {
                        Error("format-" + JsonNamingPolicy.KebabCaseLower.ConvertName(format.ToString()), FormatMessage(format));
                        return null;
                    }

                    text = normal;
                }

                if (v?.Pattern is { } pattern)
                {
                    bool matches;
                    try
                    {
                        matches = Regex.IsMatch(text, $"^(?:{pattern})$", RegexOptions.CultureInvariant, PatternTimeout);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        matches = false;
                    }
                    catch (ArgumentException)
                    {
                        matches = false;
                    }

                    if (!matches)
                    {
                        Error("pattern", "The answer is not in the expected form.");
                        return null;
                    }
                }

                return JsonValue.Create(text);
            }

            case FieldType.Number:
            {
                decimal number;
                if (FormDefinitionCheck.Kind(value) == JsonValueKind.Number)
                {
                    number = value.GetValue<decimal>();
                }
                else if (FormDefinitionCheck.Kind(value) == JsonValueKind.String
                    && decimal.TryParse(value.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    number = parsed;
                }
                else
                {
                    Error("type", "Expected a number.");
                    return null;
                }

                if (v?.Min is { } min && number < min)
                {
                    Error("min", $"Use {min.ToString(CultureInfo.InvariantCulture)} or more.");
                    return null;
                }

                if (v?.Max is { } max && number > max)
                {
                    Error("max", $"Use {max.ToString(CultureInfo.InvariantCulture)} or less.");
                    return null;
                }

                return JsonValue.Create(number);
            }

            case FieldType.Date:
            {
                if (FormDefinitionCheck.Kind(value) != JsonValueKind.String || !FormDefinitionCheck.IsDate(value.GetValue<string>()))
                {
                    Error("type", "Expected a date (YYYY-MM-DD).");
                    return null;
                }

                var date = value.GetValue<string>();
                if (v?.MinDate is { } from && string.CompareOrdinal(date, from) < 0)
                {
                    Error("min-date", $"Use {from} or later.");
                    return null;
                }

                if (v?.MaxDate is { } to && string.CompareOrdinal(date, to) > 0)
                {
                    Error("max-date", $"Use {to} or earlier.");
                    return null;
                }

                return JsonValue.Create(date);
            }

            case FieldType.Checkbox:
                if (FormDefinitionCheck.Kind(value) is not (JsonValueKind.True or JsonValueKind.False))
                {
                    Error("type", "Expected true or false.");
                    return null;
                }

                return JsonValue.Create(value.GetValue<bool>());

            case FieldType.Radio:
            case FieldType.Dropdown:
            {
                var options = (field.Options ?? []).Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
                if (field.Multiple)
                {
                    var items = value is JsonArray array ? array : FormDefinitionCheck.Kind(value) == JsonValueKind.String ? [value.DeepClone()] : null;
                    if (items is null || items.Any(i => FormDefinitionCheck.Kind(i) != JsonValueKind.String))
                    {
                        Error("type", "Expected a list of options.");
                        return null;
                    }

                    var chosen = items.Select(i => i!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList();
                    if (chosen.Any(c => !options.Contains(c)))
                    {
                        Error("option-unknown", "Choose from the options.");
                        return null;
                    }

                    // Stored in the order of the options.
                    return new JsonArray([.. (field.Options ?? []).Where(o => chosen.Contains(o.Value)).Select(o => (JsonNode?)JsonValue.Create(o.Value))]);
                }

                if (FormDefinitionCheck.Kind(value) != JsonValueKind.String || !options.Contains(value.GetValue<string>()))
                {
                    Error("option-unknown", "Choose from the options.");
                    return null;
                }

                return JsonValue.Create(value.GetValue<string>());
            }

            case FieldType.User:
            case FieldType.Location:
            {
                if (FormDefinitionCheck.Kind(value) != JsonValueKind.String || !Guid.TryParse(value.GetValue<string>(), out var id))
                {
                    Error("type", "Expected an id.");
                    return null;
                }

                (field.Type == FieldType.User ? result.Users : result.Locations).Add((path, id));
                return JsonValue.Create(id.ToString());
            }

            case FieldType.File:
            {
                var items = value as JsonArray;
                var ids = new List<Guid>();
                foreach (var item in items ?? [])
                {
                    if (FormDefinitionCheck.Kind(item) != JsonValueKind.String || !Guid.TryParse(item!.GetValue<string>(), out var id))
                    {
                        items = null;
                        break;
                    }

                    ids.Add(id);
                }

                if (items is null)
                {
                    Error("type", "Expected a list of uploaded file ids.");
                    return null;
                }

                ids = [.. ids.Distinct()];
                var maxFiles = v?.MaxFiles ?? 5;
                if (ids.Count > maxFiles)
                {
                    Error("max-files", $"Attach at most {maxFiles} files.");
                    return null;
                }

                result.Files.AddRange(ids.Select(id => new FileReference(path, id, false, v)));
                return new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id.ToString()))]);
            }

            case FieldType.Signature:
            {
                if (value is not JsonObject signature)
                {
                    Error("type", "Expected a drawn or typed signature.");
                    return null;
                }

                if (signature["attachmentId"] is { } attachment)
                {
                    if (FormDefinitionCheck.Kind(attachment) != JsonValueKind.String || !Guid.TryParse(attachment.GetValue<string>(), out var id) || signature.Count != 1)
                    {
                        Error("type", "Expected the id of the uploaded drawing.");
                        return null;
                    }

                    result.Files.Add(new FileReference(path, id, true, null));
                    return new JsonObject { ["attachmentId"] = id.ToString() };
                }

                var name = signature["typedName"] is { } n && FormDefinitionCheck.Kind(n) == JsonValueKind.String ? n.GetValue<string>().Trim() : null;
                var confirmed = signature["confirmed"] is { } c && FormDefinitionCheck.Kind(c) == JsonValueKind.True;
                if (string.IsNullOrEmpty(name) || name.Length > 200 || !confirmed || signature.Count != 2)
                {
                    Error("signature-incomplete", "Type your full name and confirm, or draw your signature.");
                    return null;
                }

                return new JsonObject { ["typedName"] = name, ["confirmed"] = true };
            }

            case FieldType.Table:
            {
                if (value is not JsonArray rows || rows.Any(r => r is not JsonObject))
                {
                    Error("type", "Expected a list of rows.");
                    return null;
                }

                var max = Math.Min(field.MaxRows ?? FormDefinitionCheck.MaxTableRows, FormDefinitionCheck.MaxTableRows);
                if (rows.Count > max)
                {
                    Error("max-rows", $"Add at most {max} rows.");
                    return null;
                }

                if (field.MinRows is { } minRows && rows.Count < minRows)
                {
                    Error("min-rows", $"Add at least {minRows} rows.");
                    return null;
                }

                var columns = field.Columns ?? [];
                var cleanRows = new JsonArray();
                var before = result.Errors.Count;
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = (JsonObject)rows[i]!;
                    var cleanRow = new JsonObject();
                    foreach (var (key, _) in row)
                    {
                        if (columns.All(c => c.Key != key))
                        {
                            result.Errors.Add(new($"{path}.{i}.{key}", "field-unknown", $"The table has no column '{key}'."));
                        }
                    }

                    foreach (var column in columns)
                    {
                        var cell = row[column.Key];
                        if (FormRules.IsEmpty(cell))
                        {
                            if (column.Required)
                            {
                                result.Errors.Add(new($"{path}.{i}.{column.Key}", "required", "This field is required."));
                            }

                            continue;
                        }

                        if (CheckValue(column, cell!, result, $"{path}.{i}.{column.Key}") is { } cleanCell)
                        {
                            cleanRow[column.Key] = cleanCell;
                        }
                    }

                    cleanRows.Add(cleanRow);
                }

                return result.Errors.Count == before ? cleanRows : null;
            }

            default:
                Error("type", "Unknown field type.");
                return null;
        }
    }

    private static string FormatMessage(FieldFormat format) => format switch
    {
        FieldFormat.EmiratesId => "Use the 15-digit Emirates ID number, starting with 784.",
        FieldFormat.Phone => "Use a phone number such as +971 50 123 4567.",
        FieldFormat.Email => "Use a valid email address.",
        FieldFormat.Iban => "Use a valid IBAN, such as AE07 0331 2345 6789 0123 456.",
        _ => "Not in the expected format.",
    };
}
