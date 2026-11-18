package zelvo.keycloak.uaepass;

import com.fasterxml.jackson.databind.JsonNode;
import java.util.List;
import org.keycloak.broker.oidc.mappers.AbstractJsonUserAttributeMapper;
import org.keycloak.broker.provider.AbstractIdentityProviderMapper;
import org.keycloak.broker.provider.BrokeredIdentityContext;
import org.keycloak.models.IdentityProviderMapperModel;
import org.keycloak.models.IdentityProviderSyncMode;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.provider.ProviderConfigProperty;
import org.keycloak.sessions.AuthenticationSessionModel;

/**
 * Reads the UAE PASS profile at every UAE PASS sign-in, before Keycloak decides anything:
 * <ul>
 *   <li>the account level goes into a user session note, so tokens of this session tell the
 *       Zelvo API how strongly the person was identified (SOP1, SOP2 or SOP3);</li>
 *   <li>the Emirates ID of a verified account goes into the brokered context for
 *       {@link FirstLoginAuthenticator}, and is not stored on the user.</li>
 * </ul>
 * Keycloak's own attribute mappers do not work with its generic OAuth 2.0 broker, which UAE PASS
 * needs because it issues no ID token.
 *
 * <p>The level is set again after the first sign-in steps and at every later sign-in
 * ({@code importNewUser}, {@code updateBrokeredUser}; Zelvo sets this mapper's sync mode to
 * FORCE), because linking an existing account (with its password) loses session notes made at
 * the start. It travels in the brokered context as plain text, which Keycloak keeps across those
 * steps.</p>
 */
public final class UaePassProfileMapper extends AbstractIdentityProviderMapper {

    public static final String PROVIDER_ID = "zelvo-uaepass-profile";

    private static final String[] COMPATIBLE_PROVIDERS = {"oauth2"};

    @Override
    public String getId() {
        return PROVIDER_ID;
    }

    @Override
    public String[] getCompatibleProviders() {
        return COMPATIBLE_PROVIDERS.clone();
    }

    @Override
    public String getDisplayCategory() {
        return "Attribute Importer";
    }

    @Override
    public String getDisplayType() {
        return "Zelvo UAE PASS profile";
    }

    @Override
    public String getHelpText() {
        return "Keeps the UAE PASS account level of each sign-in for Zelvo, and the Emirates ID for matching staff accounts. Managed by Zelvo.";
    }

    @Override
    public boolean supportsSyncMode(IdentityProviderSyncMode syncMode) {
        return true;
    }

    @Override
    public List<ProviderConfigProperty> getConfigProperties() {
        return List.of();
    }

    @Override
    public void preprocessFederatedIdentity(KeycloakSession session, RealmModel realm, IdentityProviderMapperModel mapperModel,
        BrokeredIdentityContext context) {
        apply(context);
    }

    @Override
    public void importNewUser(KeycloakSession session, RealmModel realm, UserModel user, IdentityProviderMapperModel mapperModel,
        BrokeredIdentityContext context) {
        restoreLevel(context);
    }

    @Override
    public void updateBrokeredUser(KeycloakSession session, RealmModel realm, UserModel user, IdentityProviderMapperModel mapperModel,
        BrokeredIdentityContext context) {
        restoreLevel(context);
    }

    /** Context data key holding the level as text, so that it survives the first sign-in steps. */
    static final String CONTEXT_LEVEL = "zelvo.uaepass.level";

    /**
     * Context data key holding the Emirates ID of a verified account, for the first sign-in step.
     * Keycloak resets session notes before that step, but keeps the brokered context.
     */
    static final String CONTEXT_EMIRATES_ID = "zelvo.uaepass.emirates-id";

    /** Stores the level and Emirates ID of this sign-in in the authentication session. */
    static void apply(BrokeredIdentityContext context) {
        Object node = context.getContextData().get(AbstractJsonUserAttributeMapper.CONTEXT_JSON_NODE);
        UaePassProfile profile = UaePassProfile.from(node instanceof JsonNode json ? json : null);
        context.getContextData().put(CONTEXT_LEVEL, profile.level().name());
        context.getContextData().remove(CONTEXT_EMIRATES_ID);
        profile.emiratesId().ifPresent(id -> context.getContextData().put(CONTEXT_EMIRATES_ID, id));
        AuthenticationSessionModel authSession = context.getAuthenticationSession();
        if (authSession == null) {
            return;
        }
        authSession.setUserSessionNote(UaePassNotes.LEVEL, profile.level().name());
    }

    /** Sets the level note again from the brokered context; nothing is granted when it is missing. */
    static void restoreLevel(BrokeredIdentityContext context) {
        AuthenticationSessionModel authSession = context.getAuthenticationSession();
        Object level = context.getContextData().get(CONTEXT_LEVEL);
        if (authSession == null) {
            return;
        }
        if (level == null) {
            // Fall back to the profile itself, when Keycloak still has it.
            Object node = context.getContextData().get(AbstractJsonUserAttributeMapper.CONTEXT_JSON_NODE);
            level = node instanceof JsonNode json ? UaePassProfile.from(json).level().name() : null;
        }
        UaePassProfile.Level.parse(level instanceof String text ? text : null)
            .ifPresent(parsed -> authSession.setUserSessionNote(UaePassNotes.LEVEL, parsed.name()));
    }
}
