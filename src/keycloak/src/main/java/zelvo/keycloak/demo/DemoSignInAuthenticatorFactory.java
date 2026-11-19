package zelvo.keycloak.demo;

import java.util.List;
import org.keycloak.Config;
import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.AuthenticatorFactory;
import org.keycloak.models.AuthenticationExecutionModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.KeycloakSessionFactory;
import org.keycloak.provider.ProviderConfigProperty;
import org.keycloak.provider.ProviderConfigurationBuilder;

/** Registers {@link DemoSignInAuthenticator} with Keycloak as {@value #PROVIDER_ID}. */
public final class DemoSignInAuthenticatorFactory implements AuthenticatorFactory {

    public static final String PROVIDER_ID = "zelvo-demo-sign-in";

    private static final DemoSignInAuthenticator SINGLETON = new DemoSignInAuthenticator();

    @Override
    public String getId() {
        return PROVIDER_ID;
    }

    @Override
    public String getDisplayType() {
        return "Zelvo demo sign-in (demo installations only)";
    }

    @Override
    public String getHelpText() {
        return "Signs in the listed demo users with one click. Zelvo adds it to demo installations only.";
    }

    @Override
    public String getReferenceCategory() {
        return "demo";
    }

    @Override
    public boolean isConfigurable() {
        return true;
    }

    @Override
    public AuthenticationExecutionModel.Requirement[] getRequirementChoices() {
        return new AuthenticationExecutionModel.Requirement[] {
            AuthenticationExecutionModel.Requirement.ALTERNATIVE,
            AuthenticationExecutionModel.Requirement.DISABLED,
        };
    }

    @Override
    public boolean isUserSetupAllowed() {
        return false;
    }

    @Override
    public List<ProviderConfigProperty> getConfigProperties() {
        return ProviderConfigurationBuilder.create()
            .property()
                .name(DemoSignInPolicy.CONFIG_USERS)
                .label("Demo users")
                .helpText("Usernames of the demo users. Set by Zelvo.")
                .type(ProviderConfigProperty.MULTIVALUED_STRING_TYPE)
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
