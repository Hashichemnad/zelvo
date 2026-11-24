using System.Text.RegularExpressions;

namespace Zelvo.Search;

/// <summary>
/// Who may find a document. A document lists its readers; a search only returns documents with at
/// least one reader the caller is (<see cref="ForCaller"/>). Each document type can also require a
/// permission (<see cref="SearchTypeRegistration"/>), checked at search time.
/// </summary>
public static partial class SearchReaders
{
    /// <summary>Everyone, including visitors who are not signed in.</summary>
    public const string Public = "public";

    /// <summary>Every signed-in user of the tenant.</summary>
    public const string SignedIn = "users";

    /// <summary>One user (their Zelvo user id).</summary>
    public static string User(Guid userId) => $"user:{userId}";

    /// <summary>Holders of a role (its key, for example <c>agent</c>).</summary>
    public static string Role(string roleKey) => $"role:{roleKey}";

    /// <summary>Members of a group (groups arrive with module 10, task B2).</summary>
    public static string Group(Guid groupId) => $"group:{groupId}";

    /// <summary>People of a department (task C2: services shown to departments).</summary>
    public static string Department(Guid departmentId) => $"department:{departmentId}";

    /// <summary>People at a location or below it (task C2: services shown to locations); callers are readers of their location and every location above it.</summary>
    public static string Location(Guid locationId) => $"location:{locationId}";

    /// <summary>Whether <paramref name="reader"/> is one of the forms above.</summary>
    public static bool IsReader(string? reader) => reader is not null && ReaderPattern().IsMatch(reader);

    /// <summary>The readers a caller is: anonymous visitors are only <see cref="Public"/>.</summary>
    public static IReadOnlyList<string> ForCaller(
        Guid? userId, IEnumerable<string> roleKeys, IEnumerable<Guid>? groupIds = null, Guid? departmentId = null, IEnumerable<Guid>? locationAncestry = null)
    {
        ArgumentNullException.ThrowIfNull(roleKeys);
        if (userId is not { } id)
        {
            return [Public];
        }

        return
        [
            Public, SignedIn, User(id), .. roleKeys.Select(Role), .. (groupIds ?? []).Select(Group),
            .. departmentId is { } department ? [Department(department)] : Array.Empty<string>(),
            .. (locationAncestry ?? []).Select(Location),
        ];
    }

    [GeneratedRegex("^(public|users|user:[0-9a-f-]{36}|group:[0-9a-f-]{36}|department:[0-9a-f-]{36}|location:[0-9a-f-]{36}|role:[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?)$")]
    private static partial Regex ReaderPattern();
}
