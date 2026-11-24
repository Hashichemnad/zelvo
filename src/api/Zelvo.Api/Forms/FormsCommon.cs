using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Forms;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Forms;

/// <summary>Problems and helpers shared by the forms endpoints (task C1).</summary>
internal static partial class FormsCommon
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    public static partial Regex KeyPattern();

    public static ProblemHttpResult NotFound(string what) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, $"{what}-not-found", $"The {what.Replace('-', ' ')} does not exist.");

    public static ProblemHttpResult KeyTaken() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "key-taken", "Another one already has this key.");

    public static ProblemHttpResult VersionMismatch() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status412PreconditionFailed, "version-mismatch",
            "It was changed by someone else. Load it again and repeat your change.");

    public static ProblemHttpResult Problems(string code, string title, IReadOnlyList<FormProblem> problems, object? more = null)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code, ["problems"] = problems };
        if (more is not null)
        {
            extensions["details"] = more;
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, type: ApiProblem.TypeFor(code), extensions: extensions);
    }

    public static ProblemHttpResult State(string title) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "state-not-allowed", title);

    /// <summary>The <c>If-Match</c> header matches <paramref name="version"/>, or there is none.</summary>
    public static bool Matches(HttpContext http, uint version) =>
        http.Request.Headers.IfMatch.Count == 0 || http.Request.Headers.IfMatch.ToString().Trim() == $"\"{version}\"";

    public static void SetETag(HttpContext http, uint version) => http.Response.Headers.ETag = $"\"{version}\"";

    /// <summary>A JSON string parsed for responses.</summary>
    public static JsonNode? Node(string json) => JsonNode.Parse(json);

    /// <summary>Parses a schema sent by a client into its canonical form; <see langword="null"/> when it does not have the expected shape or is too large.</summary>
    public static string? Canonical<T>(JsonObject? input, Func<string, T?> read)
        where T : class
    {
        if (input is null)
        {
            return null;
        }

        var raw = input.ToJsonString();
        if (raw.Length > Form.SchemaMaxLength || read(raw) is not { } parsed)
        {
            return null;
        }

        return FormJson.Write(parsed);
    }

    /// <summary>Resolves a form schema with the field sets as published now, and lists its problems.</summary>
    public static async Task<(List<ResolvedField> Fields, List<FormProblem> Problems)> ResolveAsync(
        ZelvoDbContext db, FormSchema schema, CancellationToken cancellationToken)
    {
        var problems = new List<FormProblem>();
        var ids = FormResolver.FieldSetIds(schema).ToList();
        var sets = await FormResolver.PublishedFieldSetsAsync(db, ids, cancellationToken);
        foreach (var missing in ids.Where(id => !sets.ContainsKey(id)))
        {
            problems.Add(new(null, "field-set-unavailable", $"The field set {missing} is not published; publish it or remove it from the form."));
        }

        if (schema.Elements.Count(e => e.Type == FormElementType.FieldSet) != ids.Count)
        {
            problems.Add(new(null, "field-set-twice", "A field set can be in a form only once."));
        }

        foreach (var element in schema.Elements)
        {
            if ((element.Type == FormElementType.Field && element.Field is null) || (element.Type == FormElementType.FieldSet && element.FieldSetId is null))
            {
                problems.Add(new(null, "element-invalid", "An element has no field or field set."));
            }
        }

        var fields = FormResolver.Expand(schema, sets);
        if (fields.Count == 0)
        {
            problems.Add(new(null, "form-empty", "Add at least one field."));
        }

        problems.AddRange(FormDefinitionCheck.Check([.. fields.Select(f => f.Field)]));
        return (fields, problems);
    }

    /// <summary>A validation problem that also lists each answer's error with its code.</summary>
    public static ValidationProblem AnswerErrors(IReadOnlyList<FormAnswerError> errors) =>
        TypedResults.ValidationProblem(
            errors.GroupBy(e => "formAnswers." + e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["formErrors"] = errors });

    /// <summary>Bilingual name and description checks shared by forms and field sets.</summary>
    public static ValidationProblem? CheckNames(Data.Localization.LocalizedText? name, Data.Localization.LocalizedText? description)
    {
        if (name is not null && (!name.HasAnyValue || (name.En?.Length ?? 0) > Form.NameMaxLength || (name.Ar?.Length ?? 0) > Form.NameMaxLength))
        {
            return Identity.UserEndpoints.Validation("name", $"Give a name of at most {Form.NameMaxLength} characters in English or Arabic.");
        }

        if (description is not null && ((description.En?.Length ?? 0) > Form.DescriptionMaxLength || (description.Ar?.Length ?? 0) > Form.DescriptionMaxLength))
        {
            return Identity.UserEndpoints.Validation("description", $"Use at most {Form.DescriptionMaxLength} characters in each language.");
        }

        return null;
    }

    public static Data.Localization.LocalizedText? Clean(LocalizedTextInput? input) => input is null ? null : ServiceDesk.DirectoryProblems.Clean(input);

    public static Data.Localization.LocalizedText? Clean(UpdateDescription? input) =>
        input is null ? null : new(input.En?.Trim() is { Length: > 0 } en ? en : null, input.Ar?.Trim() is { Length: > 0 } ar ? ar : null);

    /// <summary>The JSON options for schemas, for serialising responses.</summary>
    public static JsonSerializerOptions Json => FormJson.Options;
}

/// <summary>A new form or field set.</summary>
public sealed class CreateFormRequest
{
    /// <summary>Unique key: small letters, digits and dashes, for example <c>laptop-problem</c>.</summary>
    [Required]
    [StringLength(Form.KeyMaxLength, MinimumLength = 1)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic (at least one).</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it is for (either language may be blank).</summary>
    public UpdateDescription? Description { get; init; }

    /// <summary>The fields to start with (a form: <c>{"elements": [...]}</c>; a field set: <c>{"fields": [...]}</c>).</summary>
    public JsonObject? Schema { get; init; }
}

/// <summary>Changes to a form's or field set's key, name or description. Leave a field out to keep it.</summary>
public sealed class UpdateFormRequest
{
    /// <summary>New key.</summary>
    [StringLength(Form.KeyMaxLength, MinimumLength = 1)]
    public string? Key { get; init; }

    /// <summary>New name.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>New description.</summary>
    public UpdateDescription? Description { get; init; }
}

/// <summary>The draft's fields.</summary>
public sealed class SaveDraftRequest
{
    /// <summary>A form: <c>{"elements": [...]}</c>; a field set: <c>{"fields": [...]}</c>.</summary>
    [Required]
    public JsonObject? Schema { get; init; }
}

/// <summary>A copy of a form.</summary>
public sealed class CopyFormRequest
{
    /// <summary>Key of the copy.</summary>
    [Required]
    [StringLength(Form.KeyMaxLength, MinimumLength = 1)]
    public string? Key { get; init; }

    /// <summary>Name of the copy (default: the original's).</summary>
    public LocalizedTextInput? Name { get; init; }
}

/// <summary>A published version.</summary>
/// <param name="Number">Version number.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="PublishedBy">Who published it.</param>
internal sealed record FormVersionSummary(int Number, DateTimeOffset PublishedAt, Identity.PersonRef? PublishedBy);
