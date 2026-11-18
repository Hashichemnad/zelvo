package zelvo.keycloak.demo;

import java.util.Map;
import java.util.Optional;
import org.jboss.logging.Logger;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.Authenticator;
import org.keycloak.authentication.authenticators.util.AcrStore;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.sessions.AuthenticationSessionModel;

/**
 * Demo installations only (task D, product owner decision 2026-11-18): one-click switching between
 * the ready-made demo users. When the sign-in request asks for a listed demo user
 * ({@code login_hint=zelvo-demo:<username>}), that user is signed in without a form, with the
 * second factor counted as done (level 2, {@code acr=mfa}), because demo users of staff roles
 * would otherwise need an authenticator app. Any other request passes on to the usual steps.
 *
 * <p>Zelvo adds this step only to the realms of a demo installation; the API's token checks are
 * the same as everywhere else.
 */
public final class DemoSignInAuthenticator implements Authenticator {

    /** The authentication session's copy of the request's {@code login_hint}. */
    static final String LOGIN_HINT_NOTE = "login_hint";

    /** Level of authentication of a demo sign-in: both factors, as in {@code acr.loa.map} (mfa = 2). */
    static final int DEMO_LEVEL = 2;

    private static final Logger LOG = Logger.getLogger(DemoSignInAuthenticator.class);

    @Override
    public void authenticate(AuthenticationFlowContext context) {
        AuthenticationSessionModel authSession = context.getAuthenticationSession();
        AuthenticatorConfigModel config = context.getAuthenticatorConfig();
        DemoSignInPolicy policy = DemoSignInPolicy.fromConfig(config == null ? Map.of() : config.getConfig());
        Optional<String> requested = policy.requestedUser(authSession.getClientNote(LOGIN_HINT_NOTE));
        if (requested.isEmpty()) {
            context.attempted();
            return;
        }

        // The hint is not an address; do not show it in a sign-in form later.
        authSession.removeClientNote(LOGIN_HINT_NOTE);
        KeycloakSession session = context.getSession();
        RealmModel realm = context.getRealm();
        UserModel user = session.users().getUserByUsername(realm, requested.get());
        if (user == null || !user.isEnabled()) {
            context.attempted();
            return;
        }

        new AcrStore(session, authSession).setLevelAuthenticated(DEMO_LEVEL);
        context.getEvent().detail("zelvo_demo_sign_in", "true");
        LOG.debugf("Demo sign-in as user %s", user.getId());
        context.setUser(user);
        context.success();
    }

    @Override
    public void action(AuthenticationFlowContext context) {
        // No form.
        context.attempted();
    }

    @Override
    public boolean requiresUser() {
        return false;
    }

    @Override
    public boolean configuredFor(KeycloakSession session, RealmModel realm, UserModel user) {
        return true;
    }

    @Override
    public void setRequiredActions(KeycloakSession session, RealmModel realm, UserModel user) {
        // None.
    }

    @Override
    public void close() {
        // Nothing to release.
    }
}
