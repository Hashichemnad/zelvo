namespace Zelvo.Api.Security;

/// <summary>
/// Baseline security headers for every API response (OWASP ASVS V14.4). The API serves JSON only,
/// so the content security policy blocks everything, except for the built-in API explorer page,
/// which may load its own bundled script and styles.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // Same-origin only: the explorer's assets are bundled with the API and it calls only this API.
    private const string ExplorerPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (context.Request.Path.StartsWithSegments(OpenApi.OpenApiSetup.ExplorerPath, StringComparison.OrdinalIgnoreCase))
        {
            headers["Content-Security-Policy"] = ExplorerPolicy;
        }
        else if (!context.Request.Path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }

        // Task F7 (ASVS V14.3.2): answers are not kept by browsers or shared caches unless an
        // endpoint says otherwise (for example branding images).
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            if (!response.Headers.ContainsKey("Cache-Control"))
            {
                response.Headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, context.Response);

        return next(context);
    }
}
