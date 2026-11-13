using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>
/// Field-level access for API responses and changes: hidden fields are left out of responses, and
/// changes to fields the user may not edit are refused.
/// </summary>
internal static class FieldFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The response as JSON without the fields hidden from the current user.</summary>
    public static JsonObject Apply<T>(CurrentUser user, ResourceDefinition resource, T response)
    {
        var json = JsonSerializer.SerializeToNode(response, Json)!.AsObject();
        foreach (var field in resource.Fields)
        {
            if (user.FieldAccessFor(resource, field) == FieldAccess.Hidden)
            {
                json.Remove(field.Key);
            }
        }

        return json;
    }

    /// <summary>Fields among <paramref name="changed"/> the current user may not edit.</summary>
    public static IReadOnlyList<string> NotEditable(CurrentUser user, ResourceDefinition resource, IEnumerable<string> changed) =>
        [.. changed.Where(key => resource.Field(key) is not { } field || user.FieldAccessFor(resource, field) != FieldAccess.Edit)];

    /// <summary>Whether a field is visible to the current user.</summary>
    public static bool IsVisible(CurrentUser user, ResourceDefinition resource, string key) =>
        resource.Field(key) is { } field && user.FieldAccessFor(resource, field) != FieldAccess.Hidden;
}
