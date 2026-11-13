using System.Diagnostics;

namespace Zelvo.Api.Conventions;

/// <summary>
/// Error responses use RFC 9457 problem details. Every problem carries a stable <c>code</c>, which
/// clients translate for display (titles are English and meant for developers), and a
/// <c>traceId</c> matching the server logs.
/// </summary>
internal static class ApiProblem
{
    /// <summary>Problem type URI for <paramref name="code"/>.</summary>
    public static string TypeFor(string code) => $"urn:zelvo:problem:{code}";

    /// <summary>A problem result with a stable code.</summary>
    public static IResult Create(int statusCode, string code, string title, string? detail = null) =>
        TypedResults.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            type: TypeFor(code),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });

    /// <summary>Writes a problem from middleware.</summary>
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string title)
    {
        context.Response.StatusCode = statusCode;
        var service = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = statusCode,
                Title = title,
                Type = TypeFor(code),
                Extensions = { ["code"] = code },
            },
        }).ConfigureAwait(false);
    }

    /// <summary>Adds <c>traceId</c> to every problem and a <c>code</c> where the framework wrote one.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        if (!problem.Extensions.ContainsKey("code"))
        {
            var code = problem.Status switch
            {
                StatusCodes.Status400BadRequest => "validation-failed",
                StatusCodes.Status401Unauthorized => "unauthenticated",
                StatusCodes.Status403Forbidden => "forbidden",
                StatusCodes.Status404NotFound => "not-found",
                StatusCodes.Status405MethodNotAllowed => "method-not-allowed",
                StatusCodes.Status409Conflict => "conflict",
                StatusCodes.Status429TooManyRequests => "rate-limited",
                _ => "server-error",
            };
            problem.Extensions["code"] = code;
            problem.Type ??= TypeFor(code);
        }
    }
}
