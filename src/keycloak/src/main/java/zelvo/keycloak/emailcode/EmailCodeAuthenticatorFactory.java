package zelvo.keycloak.emailcode;

import java.util.List;
import org.keycloak.Config;
import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.AuthenticatorFactory;
import org.keycloak.models.AuthenticationExecutionModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.KeycloakSessionFactory;
import org.keycloak.provider.ProviderConfigProperty;
import org.keycloak.provider.ProviderConfigurationBuilder;

/** Registers {@link EmailCodeAuthenticator} with Keycloak as {@value #PROVIDER_ID}. */
public final class EmailCodeAuthenticatorFactory implements AuthenticatorFactory {

    public static final String PROVIDER_ID = "zelvo-email-code";

    private static final EmailCodeAuthenticator SINGLETON = new EmailCodeAuthenticator();

    @Override
    public String getId() {
        return PROVIDER_ID;
    }

    @Override
    public String getDisplayType() {
        return "Zelvo email one-time code";
    }

    @Override
    public String getHelpText() {
        return "Sign in with a one-time code sent to the user's email address. Zelvo manages the settings.";
    }

    @Override
    public String getReferenceCategory() {
        return "email-code";
    }

    @Override
    public boolean isConfigurable() {
        return true;
    }

    @Override
    public AuthenticationExecutionModel.Requirement[] getRequirementChoices() {
        return REQUIREMENT_CHOICES;
    }

    @Override
    public boolean isUserSetupAllowed() {
        return false;
    }

    @Override
    public List<ProviderConfigProperty> getConfigProperties() {
        return ProviderConfigurationBuilder.create()
            .property()
                .name(EmailCodeAuthenticator.CONFIG_POLICY)
                .label("Who may sign in")
                .helpText("known-users, allowed-domains or anyone. Set by Zelvo; change it in Zelvo Settings.")
                .type(ProviderConfigProperty.LIST_TYPE)
                .options(SignInPolicy.Mode.KNOWN_USERS.value(), SignInPolicy.Mode.ALLOWED_DOMAINS.value(),
                    SignInPolicy.Mode.ANYONE.value())
                .defaultValue(SignInPolicy.Mode.KNOWN_USERS.value())
                .add()
            .property()
                .name(EmailCodeAuthenticator.CONFIG_DOMAINS)
                .label("Allowed email domains")
                .helpText("With allowed-domains: people with these domains get a requester account.")
                .type(ProviderConfigProperty.MULTIVALUED_STRING_TYPE)
                .add()
            .property()
                .name(EmailCodeAuthenticator.CONFIG_TTL)
                .label("Code lifetime (seconds)")
                .helpText("Between 60 and 3600.")
                .type(ProviderConfigProperty.STRING_TYPE)
                .defaultValue(Long.toString(OneTimeCode.DEFAULT_TTL_SECONDS))
                .add()
            .build();
    }

    @Override
    public Authenticator create(KeycloakSession session) {
        return SINGLETON;
    }

    @Override
    public void init(Config.Scope config) {
        // No server-wide settings.
    }

    @Override
    public void postInit(KeycloakSessionFactory factory) {
        // Nothing to do.
    }

    @Override
    public void close() {
        // Nothing to release.
    }
}
