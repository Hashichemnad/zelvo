using System.Net;
using System.Text.Json.Nodes;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>Installation settings used when setting up tenant realms (section <c>Identity:Provisioning</c>).</summary>
internal sealed class RealmProvisioningOptions
{
    public const string Section = "Identity:Provisioning";

    /// <summary>Bootstrap administrator of Keycloak's master realm. Empty: realms are not provisioned.</summary>
    public string? AdminUser { get; set; }

    public string? AdminPassword { get; set; }

    /// <summary>The web app's public address, for sign-in redirects.</summary>
    public string WebUrl { get; set; } = "http://localhost:3000";

    /// <summary>Secret of the confidential <c>zelvo-web</c> client in every tenant realm.</summary>
    public string? WebClientSecret { get; set; }

    /// <summary>Outgoing mail for sign-in codes and password resets.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Development only: password of one ready-made user per persona in the bootstrap tenant.</summary>
    public string? DevUsersPassword { get; set; }

    /// <summary>Domain of the development users' addresses.</summary>
    public string DevUsersDomain { get; set; } = "zelvo.test";
}

/// <summary>Mail server Keycloak sends sign-in emails through.</summary>
internal sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    public string From { get; set; } = "no-reply@zelvo.test";

    public string FromDisplayName { get; set; } = "Zelvo";

    public string? User { get; set; }

    public string? Password { get; set; }

    public bool StartTls { get; set; }

    public bool Ssl { get; set; }
}

/// <summary>
/// Creates and updates the Keycloak realm of a tenant: security settings, the <c>zelvo-web</c>
/// and <c>zelvo-api</c> clients, the Zelvo sign-in flow (password or email code, then a second
/// factor when asked for <c>acr=mfa</c>), English and Arabic, and mail. Also grants the
/// <c>zelvo-provisioner</c> service account admin rights on the realm. Safe to run repeatedly.
/// </summary>
internal sealed class KeycloakRealmProvisioner(KeycloakAdminClient admin, IdentityOptions identity, RealmProvisioningOptions options)
{
    /// <summary>Level-of-authentication names in tokens (<c>acr</c>): 1 = one factor, 2 = second factor.</summary>
    public const string AcrLoaMap = """{"pwd":1,"mfa":2}""";

    // Identity provider roles: the API sets up UAE PASS (GOV-1) when an administrator saves it.
    // Client roles: the API creates, disables and rotates OAuth clients for integrations (E1).
    private static readonly string[] ProvisionerRealmRoles =
        ["view-realm", "manage-realm", "view-users", "manage-users", "view-identity-providers", "manage-identity-providers", "view-clients", "manage-clients"];

    public async Task EnsureProvisionerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identity.ProvisionerClientSecret))
        {
            return;
        }

        var client = await FindClientAsync("master", identity.ProvisionerClientId, cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["clientId"] = identity.ProvisionerClientId,
            ["name"] = "Zelvo API (tenant realm settings)",
            ["enabled"] = true,
            ["publicClient"] = false,
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["secret"] = identity.ProvisionerClientSecret,
        };
        if (client is null)
        {
            using var _ = await admin.SendAsync(HttpMethod.Post, "realms/master/clients", body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var _ = await admin.SendAsync(HttpMethod.Put, $"realms/master/clients/{(string)client["id"]!}", body, cancellationToken).ConfigureAwait(false);
        }

        // Since Keycloak 26.8 the admin API accepts only tokens with the admin API audience, which
        // the built-in "kc-admin-api" client scope adds (admin-cli has it by default).
        client = await FindClientAsync("master", identity.ProvisionerClientId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeycloakAdminException("The zelvo-provisioner client is missing in the master realm.");
        var scopes = await admin.GetArrayAsync("realms/master/client-scopes", cancellationToken).ConfigureAwait(false);
        var adminApiScope = scopes.FirstOrDefault(s => (string?)s?["name"] == AdminApiScope)
            ?? throw new KeycloakAdminException($"The Keycloak client scope '{AdminApiScope}' is missing; Keycloak 26.8 or later is required.");
        using var __ = await admin.SendAsync(HttpMethod.Put,
            $"realms/master/clients/{E((string)client["id"]!)}/default-client-scopes/{E((string)adminApiScope["id"]!)}", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keycloak's client scope that gives tokens the admin API audience.</summary>
    internal const string AdminApiScope = "kc-admin-api";

    public async Task EnsureRealmAsync(Tenant tenant, IdentitySettings settings, CancellationToken cancellationToken)
    {
        var realm = identity.RealmFor(tenant.Slug);
        var existing = await admin.GetAsync($"realms/{E(realm)}", cancellationToken).ConfigureAwait(false);
        var representation = RealmRepresentation(realm, tenant);
        if (existing is null)
        {
            using var _ = await admin.SendAsync(HttpMethod.Post, "realms", representation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var _ = await admin.SendAsync(HttpMethod.Put, $"realms/{E(realm)}", representation, cancellationToken).ConfigureAwait(false);
        }

        await EnsureClientAsync(realm, WebClient(tenant), cancellationToken).ConfigureAwait(false);
        await EnsureClientAsync(realm, new JsonObject
        {
            ["clientId"] = identity.Audience,
            ["name"] = "Zelvo API",
            ["enabled"] = true,
            ["bearerOnly"] = true,
            ["publicClient"] = false,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
        }, cancellationToken).ConfigureAwait(false);

        await EnsureBrowserFlowAsync(realm, settings, cancellationToken).ConfigureAwait(false);
        await EnsureUaePassFlowsAsync(realm, cancellationToken).ConfigureAwait(false);
        await EnsureEmiratesIdAttributeAsync(realm, cancellationToken).ConfigureAwait(false);
        await GrantProvisionerAsync(realm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Provider id of the Zelvo extension's demo sign-in step (demo installations only).</summary>
    public const string DemoSignInStep = "zelvo-demo-sign-in";

    /// <summary>
    /// Demo installations only: puts the Zelvo extension's demo sign-in step first in the realm's
    /// browser flow and lists the demo users it may sign in. A sign-in request with
    /// <c>login_hint=zelvo-demo:&lt;username&gt;</c> for one of them then signs that user in at
    /// level 2 (<c>acr=mfa</c>) without a form; every other sign-in passes on to the usual steps.
    /// Never called for other installations, so their realms do not have the step.
    /// </summary>
    public async Task EnsureDemoSignInAsync(string realm, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        const string top = KeycloakAdminClient.BrowserFlow;
        var path = $"realms/{E(realm)}/authentication/flows/{E(top)}/executions";
        JsonNode? Find(IReadOnlyList<JsonNode?> executions) =>
            executions.FirstOrDefault(e => (string?)e?["providerId"] == DemoSignInStep && (int?)e?["level"] == 0);

        var step = Find(await admin.GetArrayAsync(path, cancellationToken).ConfigureAwait(false));
        if (step is null)
        {
            await AddExecutionAsync(realm, top, DemoSignInStep, "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            step = Find(await admin.GetArrayAsync(path, cancellationToken).ConfigureAwait(false));
        }

        // First in the flow, before the session cookie: switching users always signs in the chosen one.
        for (var moves = 0; step is not null && (int?)step["index"] > 0 && moves < 10; moves++)
        {
            using (var _ = await admin.SendAsync(HttpMethod.Post,
                $"realms/{E(realm)}/authentication/executions/{E((string)step["id"]!)}/raise-priority", null, cancellationToken).ConfigureAwait(false))
            {
            }

            step = Find(await admin.GetArrayAsync(path, cancellationToken).ConfigureAwait(false));
        }

        if (step is null)
        {
            throw new KeycloakAdminException($"The demo sign-in step could not be added to realm {realm}.");
        }

        await admin.SetExecutionConfigAsync(realm, step, DemoSignInStep,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["demoUsers"] = string.Join("##", usernames) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or updates a user with a password; returns its Keycloak id.</summary>
    public async Task<string> EnsureUserAsync(Tenant tenant, string email, string firstName, string lastName, string password, CancellationToken cancellationToken)
    {
        var realm = identity.RealmFor(tenant.Slug);
        var found = await admin.GetArrayAsync($"realms/{E(realm)}/users?email={E(email)}&exact=true", cancellationToken).ConfigureAwait(false);
        var user = new JsonObject
        {
            ["username"] = email,
            ["email"] = email,
            ["emailVerified"] = true,
            ["enabled"] = true,
            ["firstName"] = firstName,
            ["lastName"] = lastName,
        };

        string id;
        if (found.Count == 0)
        {
            using var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/users", user, cancellationToken).ConfigureAwait(false);
            found = await admin.GetArrayAsync($"realms/{E(realm)}/users?email={E(email)}&exact=true", cancellationToken).ConfigureAwait(false);
            id = (string)found.Single()!["id"]!;
        }
        else
        {
            id = (string)found[0]!["id"]!;
        }

        try
        {
            using var reset = await admin.SendAsync(HttpMethod.Put, $"realms/{E(realm)}/users/{E(id)}/reset-password",
                new JsonObject { ["type"] = "password", ["value"] = password, ["temporary"] = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (KeycloakAdminException ex) when (ex.Status == HttpStatusCode.BadRequest
            && ex.Message.Contains("invalidPasswordHistoryMessage", StringComparison.Ordinal))
        {
            // The realm's password history refuses a password the user already had, which is the
            // case on every later run of `migrate` with the same development password.
        }

        return id;
    }

    private JsonObject RealmRepresentation(string realm, Tenant tenant)
    {
        var smtp = options.Smtp;
        var representation = new JsonObject
        {
            ["realm"] = realm,
            ["enabled"] = true,
            ["displayName"] = tenant.Name.En ?? tenant.Name.Ar ?? tenant.Slug,
            ["sslRequired"] = "external",
            ["registrationAllowed"] = false,
            ["loginWithEmailAllowed"] = true,
            ["registrationEmailAsUsername"] = true,
            ["duplicateEmailsAllowed"] = false,
            ["resetPasswordAllowed"] = true,
            ["rememberMe"] = false,
            ["verifyEmail"] = false,
            ["editUsernameAllowed"] = false,
            ["bruteForceProtected"] = true,
            ["permanentLockout"] = false,
            ["failureFactor"] = 5,
            ["waitIncrementSeconds"] = 60,
            ["maxFailureWaitSeconds"] = 900,
            ["passwordPolicy"] = "length(12) and notUsername and notEmail and passwordHistory(3)",
            ["internationalizationEnabled"] = true,
            ["supportedLocales"] = new JsonArray("en", "ar"),
            ["defaultLocale"] = "en",
            ["loginTheme"] = "zelvo",
            ["emailTheme"] = "zelvo",
            ["accessTokenLifespan"] = 300,
            ["ssoSessionIdleTimeout"] = 1800,
            ["ssoSessionMaxLifespan"] = 43200,
            ["revokeRefreshToken"] = true,
            ["refreshTokenMaxReuse"] = 0,
            ["otpPolicyType"] = "totp",
            ["otpPolicyAlgorithm"] = "HmacSHA1",
            ["otpPolicyDigits"] = 6,
            ["otpPolicyPeriod"] = 30,
            ["attributes"] = new JsonObject { ["acr.loa.map"] = AcrLoaMap },
        };

        if (!string.IsNullOrEmpty(smtp.Host))
        {
            var server = new JsonObject
            {
                ["host"] = smtp.Host,
                ["port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["from"] = smtp.From,
                ["fromDisplayName"] = smtp.FromDisplayName,
                ["starttls"] = smtp.StartTls ? "true" : "false",
                ["ssl"] = smtp.Ssl ? "true" : "false",
                ["auth"] = string.IsNullOrEmpty(smtp.User) ? "false" : "true",
            };
            if (!string.IsNullOrEmpty(smtp.User))
            {
                server["user"] = smtp.User;
                server["password"] = smtp.Password;
            }

            representation["smtpServer"] = server;
        }

        return representation;
    }

    private JsonObject WebClient(Tenant tenant)
    {
        var webUrl = options.WebUrl.TrimEnd('/');
        return new JsonObject
        {
            ["clientId"] = "zelvo-web",
            ["name"] = "Zelvo web app",
            ["enabled"] = true,
            ["publicClient"] = false,
            ["secret"] = options.WebClientSecret
                ?? throw new InvalidOperationException("Identity:Provisioning:WebClientSecret is required to set up realms."),
            ["standardFlowEnabled"] = true,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["serviceAccountsEnabled"] = false,
            ["frontchannelLogout"] = false,
            ["redirectUris"] = new JsonArray($"{webUrl}/auth/callback"),
            ["webOrigins"] = new JsonArray(webUrl),
            ["rootUrl"] = webUrl,
            ["attributes"] = new JsonObject
            {
                ["pkce.code.challenge.method"] = "S256",
                ["post.logout.redirect.uris"] = $"{webUrl}/*",
            },
            ["defaultClientScopes"] = new JsonArray("web-origins", "acr", "basic", "profile", "email"),
            ["optionalClientScopes"] = new JsonArray(),
            ["protocolMappers"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "zelvo-api audience",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-audience-mapper",
                    ["config"] = new JsonObject
                    {
                        ["included.client.audience"] = identity.Audience,
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "false",
                    },
                },
                new JsonObject
                {
                    // Which identity provider the session came from ("uaepass"), so that signing
                    // out also ends the UAE PASS session, as UAE PASS requires.
                    ["name"] = "zelvo identity provider",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-usersessionmodel-note-mapper",
                    ["config"] = new JsonObject
                    {
                        ["user.session.note"] = "identity_provider",
                        ["claim.name"] = ZelvoClaimTypes.IdentityProvider,
                        ["jsonType.label"] = "String",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "true",
                        ["userinfo.token.claim"] = "false",
                    },
                },
                new JsonObject
                {
                    // The UAE PASS account level of this session (GOV-1 rules for staff and requesters).
                    ["name"] = "zelvo uaepass level",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-usersessionmodel-note-mapper",
                    ["config"] = new JsonObject
                    {
                        ["user.session.note"] = UaePassProvider.LevelSessionNote,
                        ["claim.name"] = ZelvoClaimTypes.UaePassLevel,
                        ["jsonType.label"] = "String",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "true",
                        ["userinfo.token.claim"] = "false",
                    },
                },
                new JsonObject
                {
                    ["name"] = "zelvo tenant id",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-hardcoded-claim-mapper",
                    ["config"] = new JsonObject
                    {
                        ["claim.name"] = ZelvoClaimTypes.TenantId,
                        ["claim.value"] = tenant.Id.ToString(),
                        ["jsonType.label"] = "String",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "true",
                        ["userinfo.token.claim"] = "false",
                    },
                }),
        };
    }

    private async Task EnsureClientAsync(string realm, JsonObject client, CancellationToken cancellationToken)
    {
        var existing = await FindClientAsync(realm, (string)client["clientId"]!, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            using var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/clients", client, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Protocol mappers are not updated by PUT; replace them one by one.
        var id = (string)existing["id"]!;
        var mappers = client["protocolMappers"]?.AsArray();
        client.Remove("protocolMappers");
        client["id"] = id;
        using (var _ = await admin.SendAsync(HttpMethod.Put, $"realms/{E(realm)}/clients/{E(id)}", client, cancellationToken).ConfigureAwait(false))
        {
        }

        if (mappers is null)
        {
            return;
        }

        var current = await admin.GetArrayAsync($"realms/{E(realm)}/clients/{E(id)}/protocol-mappers/models", cancellationToken).ConfigureAwait(false);
        foreach (var mapper in mappers)
        {
            var name = (string)mapper!["name"]!;
            if (current.FirstOrDefault(m => (string?)m?["name"] == name) is { } old)
            {
                using var _ = await admin.SendAsync(HttpMethod.Delete,
                    $"realms/{E(realm)}/clients/{E(id)}/protocol-mappers/models/{E((string)old["id"]!)}", null, cancellationToken).ConfigureAwait(false);
            }

            using var created = await admin.SendAsync(HttpMethod.Post,
                $"realms/{E(realm)}/clients/{E(id)}/protocol-mappers/models", mapper.DeepClone(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> FindClientAsync(string realm, string clientId, CancellationToken cancellationToken)
    {
        var clients = await admin.GetArrayAsync($"realms/{E(realm)}/clients?clientId={E(clientId)}", cancellationToken).ConfigureAwait(false);
        return clients.Count > 0 ? clients[0] : null;
    }

    /// <summary>
    /// The browser flow: an existing session cookie, or (level 1) password or email code, then
    /// (level 2, only when the client asks for <c>acr=mfa</c>) a code from an authenticator app.
    /// Built once; afterwards only the email code settings are updated.
    /// </summary>
    private async Task EnsureBrowserFlowAsync(string realm, IdentitySettings settings, CancellationToken cancellationToken)
    {
        var flows = await admin.GetArrayAsync($"realms/{E(realm)}/authentication/flows", cancellationToken).ConfigureAwait(false);
        if (flows.All(f => (string?)f?["alias"] != KeycloakAdminClient.BrowserFlow))
        {
            using (var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/authentication/flows", new JsonObject
            {
                ["alias"] = KeycloakAdminClient.BrowserFlow,
                ["description"] = "Zelvo sign-in: password or email code, then a second factor for staff.",
                ["providerId"] = "basic-flow",
                ["topLevel"] = true,
                ["builtIn"] = false,
            }, cancellationToken).ConfigureAwait(false))
            {
            }

            const string top = KeycloakAdminClient.BrowserFlow;
            await AddExecutionAsync(realm, top, "auth-cookie", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            await AddFlowAsync(realm, top, "zelvo forms", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);

            await AddFlowAsync(realm, "zelvo forms", "zelvo level 1", "CONDITIONAL", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo level 1", "conditional-level-of-authentication", "REQUIRED", cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["loa-condition-level"] = "1", ["loa-max-age"] = "36000" }).ConfigureAwait(false);
            await AddFlowAsync(realm, "zelvo level 1", "zelvo first factor", "REQUIRED", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo first factor", "auth-username-password-form", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo first factor", "zelvo-email-code", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);

            await AddFlowAsync(realm, "zelvo forms", "zelvo level 2", "CONDITIONAL", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo level 2", "conditional-level-of-authentication", "REQUIRED", cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["loa-condition-level"] = "2", ["loa-max-age"] = "36000" }).ConfigureAwait(false);
            // Required, not one of several alternatives: Keycloak then asks staff without an
            // authenticator app to set one up, instead of failing the sign-in.
            await AddExecutionAsync(realm, "zelvo level 2", "auth-otp-form", "REQUIRED", cancellationToken).ConfigureAwait(false);
        }

        await admin.SetEmailCodeSignInAsync(realm, settings.EmailCodeSignIn, settings.AllowedDomains, cancellationToken).ConfigureAwait(false);

        var representation = await admin.GetAsync($"realms/{E(realm)}", cancellationToken).ConfigureAwait(false);
        if ((string?)representation?["browserFlow"] != KeycloakAdminClient.BrowserFlow)
        {
            using var _ = await admin.SendAsync(HttpMethod.Put, $"realms/{E(realm)}",
                new JsonObject { ["realm"] = realm, ["browserFlow"] = KeycloakAdminClient.BrowserFlow }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sign-in flows of the UAE PASS identity provider (GOV-1), built once; the provider itself is
    /// created when an administrator sets UAE PASS up in Settings.
    /// <list type="bullet">
    /// <item>First UAE PASS sign-in (Zelvo extension step): a verified account whose Emirates ID an
    /// administrator recorded on one account is linked to it; when an account has the same email,
    /// it is linked only after its owner proves it is theirs (a link sent to the email, or the
    /// account's password); otherwise a requester account, if the tenant allows it. No step lets
    /// people edit what UAE PASS sent.</item>
    /// <item>Every UAE PASS sign-in: counts as level 1 (one factor), and asks for the authenticator
    /// app (level 2) when the client asks for <c>acr=mfa</c>, as for staff.</item>
    /// </list>
    /// </summary>
    private async Task EnsureUaePassFlowsAsync(string realm, CancellationToken cancellationToken)
    {
        var flows = await admin.GetArrayAsync($"realms/{E(realm)}/authentication/flows", cancellationToken).ConfigureAwait(false);
        if (flows.All(f => (string?)f?["alias"] != UaePassProvider.FirstLoginFlow))
        {
            await AddTopLevelFlowAsync(realm, UaePassProvider.FirstLoginFlow,
                "First UAE PASS sign-in: linked by Emirates ID, linked by email after proof, or a new requester account.", cancellationToken).ConfigureAwait(false);
            const string first = UaePassProvider.FirstLoginFlow;
            // Zelvo extension step: links by Emirates ID, creates requesters as the tenant allows,
            // or passes on to the proof steps when an account has the same email.
            await AddExecutionAsync(realm, first, UaePassProvider.FirstLoginStep, "ALTERNATIVE", cancellationToken,
                KeycloakAdminClient.FirstLoginConfig(new UaePassProvider(false, UaePassEndpoints.At("https://unset", "https://unset"), "", null))).ConfigureAwait(false);
            await AddFlowAsync(realm, first, "zelvo uaepass prove account", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo uaepass prove account", "idp-email-verification", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            await AddFlowAsync(realm, "zelvo uaepass prove account", "zelvo uaepass prove with password", "ALTERNATIVE", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo uaepass prove with password", "idp-username-password-form", "REQUIRED", cancellationToken).ConfigureAwait(false);
        }

        if (flows.All(f => (string?)f?["alias"] != UaePassProvider.AfterLoginFlow))
        {
            await AddTopLevelFlowAsync(realm, UaePassProvider.AfterLoginFlow,
                "After each UAE PASS sign-in: one factor done; a second factor when asked for acr=mfa.", cancellationToken).ConfigureAwait(false);
            const string after = UaePassProvider.AfterLoginFlow;
            await AddFlowAsync(realm, after, "zelvo uaepass level 1", "CONDITIONAL", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo uaepass level 1", "conditional-level-of-authentication", "REQUIRED", cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["loa-condition-level"] = "1", ["loa-max-age"] = "36000" }).ConfigureAwait(false);
            // Records level 1 in the session, so a later step-up asks only for the second factor.
            await AddExecutionAsync(realm, "zelvo uaepass level 1", "allow-access-authenticator", "REQUIRED", cancellationToken).ConfigureAwait(false);
            await AddFlowAsync(realm, after, "zelvo uaepass level 2", "CONDITIONAL", cancellationToken).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo uaepass level 2", "conditional-level-of-authentication", "REQUIRED", cancellationToken,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["loa-condition-level"] = "2", ["loa-max-age"] = "36000" }).ConfigureAwait(false);
            await AddExecutionAsync(realm, "zelvo uaepass level 2", "auth-otp-form", "REQUIRED", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Declares the Emirates ID attribute in the realm's user profile: administrators (the Zelvo
    /// API) may see and set it, users may not, and it must be 15 digits starting with 784.
    /// </summary>
    private async Task EnsureEmiratesIdAttributeAsync(string realm, CancellationToken cancellationToken)
    {
        var path = $"realms/{E(realm)}/users/profile";
        var profile = await admin.GetAsync(path, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new KeycloakAdminException($"Realm {realm} has no user profile.");
        if (profile["attributes"] is not JsonArray attributes)
        {
            attributes = [];
            profile["attributes"] = attributes;
        }

        var declared = new JsonObject
        {
            ["name"] = UaePassProvider.EmiratesIdAttribute,
            ["displayName"] = "Emirates ID",
            ["multivalued"] = false,
            ["permissions"] = new JsonObject { ["view"] = new JsonArray("admin"), ["edit"] = new JsonArray("admin") },
            ["validations"] = new JsonObject { ["pattern"] = new JsonObject { ["pattern"] = "^784[0-9]{12}$", ["error-message"] = "Emirates ID" } },
        };
        var existing = attributes.FirstOrDefault(a => (string?)a?["name"] == UaePassProvider.EmiratesIdAttribute);
        if (existing is not null && JsonNode.DeepEquals(existing, declared))
        {
            return;
        }

        if (existing is not null)
        {
            attributes.Remove(existing);
        }

        attributes.Add(declared);
        using var _ = await admin.SendAsync(HttpMethod.Put, path, profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddTopLevelFlowAsync(string realm, string alias, string description, CancellationToken cancellationToken)
    {
        using var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/authentication/flows", new JsonObject
        {
            ["alias"] = alias,
            ["description"] = description,
            ["providerId"] = "basic-flow",
            ["topLevel"] = true,
            ["builtIn"] = false,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFlowAsync(string realm, string parent, string alias, string requirement, CancellationToken cancellationToken)
    {
        using (var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/authentication/flows/{E(parent)}/executions/flow",
            new JsonObject { ["alias"] = alias, ["type"] = "basic-flow", ["description"] = alias }, cancellationToken).ConfigureAwait(false))
        {
        }

        await SetRequirementAsync(realm, parent, e => (string?)e["displayName"] == alias, requirement, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddExecutionAsync(string realm, string flow, string provider, string requirement, CancellationToken cancellationToken,
        Dictionary<string, string>? config = null)
    {
        using (var _ = await admin.SendAsync(HttpMethod.Post, $"realms/{E(realm)}/authentication/flows/{E(flow)}/executions/execution",
            new JsonObject { ["provider"] = provider }, cancellationToken).ConfigureAwait(false))
        {
        }

        var execution = await SetRequirementAsync(realm, flow, e => (string?)e["providerId"] == provider, requirement, cancellationToken).ConfigureAwait(false);
        if (config is not null)
        {
            await admin.SetExecutionConfigAsync(realm, execution, provider, config, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sets the requirement of the newest direct child of <paramref name="flow"/> matching <paramref name="match"/>.</summary>
    private async Task<JsonNode> SetRequirementAsync(string realm, string flow, Func<JsonNode, bool> match, string requirement, CancellationToken cancellationToken)
    {
        var executions = await admin.GetArrayAsync($"realms/{E(realm)}/authentication/flows/{E(flow)}/executions", cancellationToken).ConfigureAwait(false);
        var execution = executions.Where(e => e is not null && (int?)e["level"] == 0 && match(e)).LastOrDefault()
            ?? throw new KeycloakAdminException($"Step not found in flow '{flow}' of realm {realm}.");
        execution["requirement"] = requirement;
        using (var _ = await admin.SendAsync(HttpMethod.Put, $"realms/{E(realm)}/authentication/flows/{E(flow)}/executions",
            execution.DeepClone(), cancellationToken).ConfigureAwait(false))
        {
        }

        return execution;
    }

    /// <summary>Gives the provisioner service account admin rights on this realm only.</summary>
    private async Task GrantProvisionerAsync(string realm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identity.ProvisionerClientSecret))
        {
            return;
        }

        var provisioner = await FindClientAsync("master", identity.ProvisionerClientId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeycloakAdminException("The zelvo-provisioner client is missing in the master realm.");
        var serviceUser = await admin.GetAsync($"realms/master/clients/{E((string)provisioner["id"]!)}/service-account-user", cancellationToken).ConfigureAwait(false);
        var realmClient = await FindClientAsync("master", $"{realm}-realm", cancellationToken).ConfigureAwait(false)
            ?? throw new KeycloakAdminException($"The admin client of realm {realm} is missing in the master realm.");
        var roles = new JsonArray();
        foreach (var role in ProvisionerRealmRoles)
        {
            var representation = await admin.GetAsync($"realms/master/clients/{E((string)realmClient["id"]!)}/roles/{E(role)}", cancellationToken).ConfigureAwait(false)
                ?? throw new KeycloakAdminException($"Role {role} of realm {realm} is missing.");
            roles.Add(representation);
        }

        using var _ = await admin.SendAsync(HttpMethod.Post,
            $"realms/master/users/{E((string)serviceUser!["id"]!)}/role-mappings/clients/{E((string)realmClient["id"]!)}", roles, cancellationToken).ConfigureAwait(false);
    }

    private static string E(string value) => KeycloakAdminClient.E(value);
}

/// <summary>The development users: one per persona, in the bootstrap tenant.</summary>
internal static class DevelopmentUsers
{
    public static IReadOnlyList<(string Local, string RoleKey, string FirstName, string LastName)> All { get; } =
    [
        ("requester", BuiltInRoles.Requester, "Rana", "Requester"),
        ("agent", BuiltInRoles.Agent, "Adel", "Agent"),
        ("lead", BuiltInRoles.TeamLead, "Layla", "Lead"),
        ("approver", BuiltInRoles.Approver, "Omar", "Approver"),
        ("admin", BuiltInRoles.TenantAdmin, "Aisha", "Admin"),
        ("executive", BuiltInRoles.ExecutiveAuditor, "Khalid", "Executive"),
    ];
}
