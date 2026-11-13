using System.Net.Http.Headers;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Request helpers: tenant header and test sign-in headers.</summary>
internal static class ApiTestClient
{
    public static HttpClient ForTenant(this ApiFactory factory, Tenant tenant) => factory.ForTenant(tenant.Slug);

    public static HttpClient ForTenant(this ApiFactory factory, string slug)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Zelvo-Tenant", slug);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static HttpClient SignedIn(this HttpClient client, Tenant memberOf, params string[] roles)
    {
        client.DefaultRequestHeaders.Add("X-Test-User", "user-42");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", memberOf.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", string.Join(',', roles));
        return client;
    }
}

/// <summary>Problem details as clients receive them.</summary>
internal sealed record ProblemBody(int Status, string? Title, string? Type, string Code, string? TraceId, Dictionary<string, string[]>? Errors);
