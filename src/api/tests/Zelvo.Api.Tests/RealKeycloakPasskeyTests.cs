using System.Text.Json.Nodes;
using Zelvo.Api.Identity;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>
/// Task J3 against a real Keycloak with the Zelvo extension (opt-in): the provisioner builds the
/// passkey-aware second factor for new realms, rebuilds the level 2 of realms made before J3 once,
/// and each passkey setting lands in the realm. Run with
/// <c>ZELVO_TEST_KEYCLOAK=http://127.0.0.1:8080</c> and <c>ZELVO_TEST_KEYCLOAK_ADMIN_PASSWORD</c>
/// (the bootstrap administrator <c>admin</c>), for example against <c>make up</c>'s Keycloak.
/// </summary>
public sealed class RealKeycloakPasskeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_second_factor_skips_the_app_code_after_a_passkey_and_follows_the_setting()
    {
        var url = Environment.GetEnvironmentVariable("ZELVO_TEST_KEYCLOAK");
        var password = Environment.GetEnvironmentVariable("ZELVO_TEST_KEYCLOAK_ADMIN_PASSWORD");
        Assert.SkipWhen(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password), "Set ZELVO_TEST_KEYCLOAK and ZELVO_TEST_KEYCLOAK_ADMIN_PASSWORD to run against a real Keycloak.");

        using var http = new HttpClient();
        var admin = KeycloakAdminClient.ForAdministrator(http, url!, "admin", password!);
        var identity = new IdentityOptions { PublicUrl = url! };
        var provisioner = new KeycloakRealmProvisioner(admin, identity, new RealmProvisioningOptions { WebUrl = "http://localhost:3000", WebClientSecret = Guid.NewGuid().ToString("N") });
        var tenant = new Tenant { Slug = $"pk{Guid.NewGuid():N}"[..20], Name = new LocalizedText("Passkey test", null) };
        var realm = identity.RealmFor(tenant.Slug);

        await provisioner.EnsureRealmAsync(tenant, new IdentitySettings(), Ct);
        await AssertShapeAsync(admin, realm, otp: "REQUIRED", passkey: "DISABLED");
        var policy = (await admin.GetAsync($"realms/{realm}", Ct))!;
        Assert.Equal(("required", "required", true), ((string?)policy["webAuthnPolicyPasswordlessUserVerificationRequirement"],
            (string?)policy["webAuthnPolicyPasswordlessResidentKey"], (bool?)policy["webAuthnPolicyPasswordlessPasskeysEnabled"]));
        Assert.True((bool?)(await admin.GetAsync($"realms/{realm}/authentication/required-actions/{KeycloakAdminClient.PasskeyRegisterAction}", Ct))!["enabled"]);

        // A realm as built before J3: the authenticator app code straight under level 2.
        foreach (var step in (await admin.GetArrayAsync($"realms/{realm}/authentication/flows/zelvo%20level%202/executions", Ct))
                     .Where(s => (int?)s!["level"] == 0 && (string?)s["providerId"] != "conditional-level-of-authentication"))
        {
            using var _ = await admin.SendAsync(HttpMethod.Delete, $"realms/{realm}/authentication/executions/{(string)step!["id"]!}", null, Ct);
        }

        using (await admin.SendAsync(HttpMethod.Post, $"realms/{realm}/authentication/flows/zelvo%20level%202/executions/execution",
            new JsonObject { ["provider"] = "auth-otp-form" }, Ct))
        {
        }

        await provisioner.EnsureRealmAsync(tenant, new IdentitySettings { Passkeys = PasskeySignIn.RequiredForStaff }, Ct);
        await AssertShapeAsync(admin, realm, otp: "DISABLED", passkey: "REQUIRED");

        await admin.SetPasskeysAsync(realm, PasskeySignIn.Off, Ct);
        await AssertShapeAsync(admin, realm, otp: "REQUIRED", passkey: "DISABLED");
        Assert.False((bool?)(await admin.GetAsync($"realms/{realm}", Ct))!["webAuthnPolicyPasswordlessPasskeysEnabled"]);
        Assert.False((bool?)(await admin.GetAsync($"realms/{realm}/authentication/required-actions/{KeycloakAdminClient.PasskeyRegisterAction}", Ct))!["enabled"]);

        using (await admin.SendAsync(HttpMethod.Delete, $"realms/{realm}", null, Ct))
        {
        }
    }

    private static async Task AssertShapeAsync(KeycloakAdminClient admin, string realm, string otp, string passkey)
    {
        var all = await admin.GetArrayAsync($"realms/{realm}/authentication/flows/{Uri.EscapeDataString(KeycloakAdminClient.BrowserFlow)}/executions", Ct);
        string Shape(JsonNode? e) => $"{(int?)e!["level"]}:{(string?)e["providerId"] ?? (string?)e["displayName"]}:{(string?)e["requirement"]}";
        var level2 = all.SkipWhile(e => (string?)e!["displayName"] != "zelvo level 2").Skip(1).TakeWhile(e => (int?)e!["level"] > 1).Select(Shape).ToList();
        Assert.Equal(
        [
            "2:conditional-level-of-authentication:REQUIRED",
            "2:allow-access-authenticator:REQUIRED",
            $"2:{KeycloakAdminClient.SecondFactorFlow}:CONDITIONAL",
            "3:conditional-credential:REQUIRED",
            $"3:auth-otp-form:{otp}",
            $"3:webauthn-authenticator-passwordless:{passkey}",
        ], level2);
    }
}
