package zelvo.keycloak.uaepass;

import jakarta.ws.rs.core.Response;
import java.util.List;
import java.util.Map;
import org.jboss.logging.Logger;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.authenticators.broker.AbstractIdpAuthenticator;
import org.keycloak.authentication.authenticators.broker.IdpCreateUserIfUniqueAuthenticator;
import org.keycloak.authentication.authenticators.broker.util.ExistingUserInfo;
import org.keycloak.authentication.authenticators.broker.util.SerializedBrokeredIdentityContext;
import org.keycloak.broker.provider.BrokeredIdentityContext;
import org.keycloak.events.Errors;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.sessions.AuthenticationSessionModel;

/**
 * First UAE PASS sign-in, in place of Keycloak's "Create User If Unique": links by Emirates ID,
 * sends people whose email has an account on to prove it, or creates a requester account, as
 * {@link FirstLoginPolicy} decides. The outcome is one of:
 * <ul>
 *   <li>success with the existing user set (Keycloak links UAE PASS to that account);</li>
 *   <li>success with a new user (inherited);</li>
 *   <li>attempted, so the flow's next alternative asks the account owner for proof;</li>
 *   <li>a refusal page.</li>
 * </ul>
 */
public final class FirstLoginAuthenticator extends IdpCreateUserIfUniqueAuthenticator {

    /** Login theme messages (English and Arabic in the zelvo theme). */
    static final String MESSAGE_NO_ACCOUNT = "zelvoUaePassNoAccount";
    static final String MESSAGE_LEVEL_TOO_LOW = "zelvoUaePassLevelTooLow";
    static final String MESSAGE_ALREADY_LINKED = "zelvoUaePassAlreadyLinked";

    private static final Logger LOG = Logger.getLogger(FirstLoginAuthenticator.class);

    @Override
    protected void authenticateImpl(AuthenticationFlowContext context, SerializedBrokeredIdentityContext serialized,
        BrokeredIdentityContext brokerContext) {
        KeycloakSession session = context.getSession();
        RealmModel realm = context.getRealm();
        AuthenticationSessionModel authSession = context.getAuthenticationSession();
        if (authSession.getAuthNote(AbstractIdpAuthenticator.EXISTING_USER_INFO) != null) {
            context.attempted();
            return;
        }

        FirstLoginPolicy policy = FirstLoginPolicy.fromConfig(config(context));
        // Keycloak resets session notes before the first sign-in flow, so the level and Emirates ID
        // come from the brokered context, where the profile mapper put them.
        UaePassProfile.Level level = UaePassProfile.Level.parse(text(brokerContext, UaePassProfileMapper.CONTEXT_LEVEL))
            .orElse(UaePassProfile.Level.SOP1);
        String emiratesId = text(brokerContext, UaePassProfileMapper.CONTEXT_EMIRATES_ID);
        List<UserModel> byEmiratesId = UaePassProfile.isEmiratesId(emiratesId)
            ? session.users().searchForUserByUserAttributeStream(realm, UaePassNotes.EMIRATES_ID_ATTRIBUTE, emiratesId)
                .limit(2).toList()
            : List.of();
        String alias = brokerContext.getIdpConfig().getAlias();

        FirstLoginPolicy.Decision decision;
        if (level.verified() && byEmiratesId.size() == 1) {
            decision = FirstLoginPolicy.Decision.LINK_BY_EMIRATES_ID;
        } else {
            ExistingUserInfo byEmail = checkExistingUser(context, getUsername(context, serialized, brokerContext), serialized, brokerContext);
            decision = policy.decide(level, byEmiratesId.size(), byEmail != null);
        }

        switch (decision) {
            case LINK_BY_EMIRATES_ID -> {
                UserModel user = byEmiratesId.get(0);
                if (session.users().getFederatedIdentity(realm, user, alias) != null) {
                    // That account is already linked to another UAE PASS account.
                    refuse(context, MESSAGE_ALREADY_LINKED, Errors.FEDERATED_IDENTITY_EXISTS);
                    return;
                }
                LOG.debugf("UAE PASS sign-in linked by Emirates ID to user %s", user.getId());
                context.setUser(user);
                context.success();
            }
            case PROVE_EXISTING_ACCOUNT, CREATE_REQUESTER -> super.authenticateImpl(context, serialized, brokerContext);
            case REFUSE_NO_ACCOUNT -> refuse(context, MESSAGE_NO_ACCOUNT, Errors.USER_NOT_FOUND);
            case REFUSE_LEVEL_TOO_LOW -> refuse(context, MESSAGE_LEVEL_TOO_LOW, Errors.ACCESS_DENIED);
        }
    }

    private static String text(BrokeredIdentityContext context, String key) {
        Object value = context.getContextData() == null ? null : context.getContextData().get(key);
        return value instanceof String string ? string : null;
    }

    private void refuse(AuthenticationFlowContext context, String message, String eventError) {
        sendFailureChallenge(context, Response.Status.FORBIDDEN, eventError, message, AuthenticationFlowError.ACCESS_DENIED);
    }

    private static Map<String, String> config(AuthenticationFlowContext context) {
        AuthenticatorConfigModel model = context.getAuthenticatorConfig();
        return model == null ? Map.of() : model.getConfig();
    }
}
