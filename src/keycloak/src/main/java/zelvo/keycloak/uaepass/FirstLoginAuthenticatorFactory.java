package zelvo.keycloak.uaepass;

import java.util.List;
import org.keycloak.Config;
import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.AuthenticatorFactory;
import org.keycloak.models.AuthenticationExecutionModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.KeycloakSessionFactory;
import org.keycloak.provider.ProviderConfigProperty;
import org.keycloak.provider.ProviderConfigurationBuilder;

/** Registers {@link FirstLoginAuthenticator} with Keycloak as {@value #PROVIDER_ID}. */
public final class FirstLoginAuthenticatorFactory implements AuthenticatorFactory {

    public static final String PROVIDER_ID = "zelvo-uaepass-first-login";

    private static final FirstLoginAuthenticator SINGLETON = new FirstLoginAuthenticator();

    private static final AuthenticationExecutionModel.Requirement[] REQUIREMENT_CHOICES = {
        AuthenticationExecutionModel.Requirement.REQUIRED,
        AuthenticationExecutionModel.Requirement.ALTERNATIVE,
        AuthenticationExecutionModel.Requirement.DISABLED,
    };

    @Override
    public String getId() {
        return PROVIDER_ID;
    }

    @Override
    public String getDisplayType() {
        return "Zelvo UAE PASS first sign-in";
    }

    @Override
    public String getHelpText() {
        return "Links by Emirates ID, asks for proof when the email has an account, or creates a requester account. Zelvo manages the settings.";
    }

    @Override
    public String getReferenceCategory() {
        return "uaepass";
    }

    @Override
    public boolean isConfigurable() {
        return true;
    }

    @Override
    public AuthenticationExecutionModel.Requirement[] getRequirementChoices() {
        return REQUIREMENT_CHOICES.clone();
    }

    @Override
    public boolean isUserSetupAllowed() {
        return false;
    }

    @Override
    public List<ProviderConfigProperty> getConfigProperties() {
        return ProviderConfigurationBuilder.create()
            .property()
                .name(FirstLoginPolicy.CONFIG_CREATE_REQUESTERS)
                .label("Create requester accounts")
                .helpText("Whether people without an account get a requester account. Set in Zelvo Settings.")
                .type(ProviderConfigProperty.BOOLEAN_TYPE)
                .defaultValue("true")
                .add()
            .property()
                .name(FirstLoginPolicy.CONFIG_REQUESTER_MINIMUM_LEVEL)
                .label("Requesters' minimum UAE PASS level")
                .helpText("SOP1, SOP2 or SOP3. Set in Zelvo Settings.")
                .type(ProviderConfigProperty.LIST_TYPE)
                .options("SOP1", "SOP2", "SOP3")
                .defaultValue("SOP1")
                .add()
            .build();
    }

    @Override
    public Authenticator create(KeycloakSession session) {
        return SINGLETON;
    }

    @Override
    public void init(Config.Scope config) {
    }

    @Override
    public void postInit(KeycloakSessionFactory factory) {
    }

    @Override
    public void close() {
    }
}
