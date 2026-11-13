using System.Net.Http.Headers;
using Zelvo.Api.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>A user created in the test database with roles.</summary>
internal sealed record TestUser(Tenant Tenant, string Subject, Guid Id, string Email);

/// <summary>Request helpers: tenant header and sign-in with test tokens.</summary>
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

    /// <summary>Creates a user of <paramref name="memberOf"/> with <paramref name="roles"/>.</summary>
    public static async Task<TestUser> CreateUserAsync(this DatabaseFixture database, Tenant memberOf, params string[] roles)
    {
        await database.EnsureIdentityAsync(memberOf);
        var subject = Guid.NewGuid().ToString();
        var email = $"user-{subject[..8]}@example.test";
        var account = await IdentityProvisioning.EnsureUserAsync(
            database.OwnerConnectionString, memberOf, subject, email, $"Test user {subject[..8]}", roles, TestContext.Current.CancellationToken);
        return new TestUser(memberOf, subject, account.Id, email);
    }

    /// <summary>Signs the client in as <paramref name="user"/> (with a second factor unless <paramref name="mfa"/> is false).</summary>
    public static HttpClient As(this HttpClient client, TestUser user, bool mfa = true)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Issue(user.Tenant, user.Subject, mfa, email: user.Email));
        return client;
    }

    /// <summary>Creates a user of <paramref name="memberOf"/> with <paramref name="roles"/> and signs in as it.</summary>
    public static async Task<HttpClient> SignedInAsync(this HttpClient client, DatabaseFixture database, Tenant memberOf, params string[] roles) =>
        client.As(await database.CreateUserAsync(memberOf, roles));
}

/// <summary>Problem details as clients receive them.</summary>
internal sealed record ProblemBody(int Status, string? Title, string? Type, string Code, string? TraceId, Dictionary<string, string[]>? Errors);
