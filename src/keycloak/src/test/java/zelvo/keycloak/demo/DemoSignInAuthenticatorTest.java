package zelvo.keycloak.demo;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.Mockito.doAnswer;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.events.EventBuilder;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.models.UserProvider;
import org.keycloak.sessions.AuthenticationSessionModel;

/** The demo sign-in step against mocked Keycloak services. */
class DemoSignInAuthenticatorTest {

    private final DemoSignInAuthenticator authenticator = new DemoSignInAuthenticator();
    private final Map<String, String> clientNotes = new HashMap<>();
    private final Map<String, String> authNotes = new HashMap<>();
    private final Map<String, String> config = new HashMap<>();

    private AuthenticationFlowContext context;
    private UserProvider users;
    private RealmModel realm;
    private UserModel agent;

    @BeforeEach
    void setUp() {
        context = mock(AuthenticationFlowContext.class);
        KeycloakSession session = mock(KeycloakSession.class);
        realm = mock(RealmModel.class);
        users = mock(UserProvider.class);
        agent = mock(UserModel.class);
        when(agent.isEnabled()).thenReturn(true);
        when(agent.getId()).thenReturn("agent-id");

        AuthenticationSessionModel auth = mock(AuthenticationSessionModel.class);
        when(auth.getClientNote(anyString())).thenAnswer(call -> clientNotes.get(call.<String>getArgument(0)));
        doAnswer(call -> clientNotes.remove(call.<String>getArgument(0))).when(auth).removeClientNote(anyString());
        when(auth.getAuthNote(anyString())).thenAnswer(call -> authNotes.get(call.<String>getArgument(0)));
        doAnswer(call -> authNotes.put(call.getArgument(0), call.getArgument(1))).when(auth).setAuthNote(anyString(), anyString());

        AuthenticatorConfigModel configModel = new AuthenticatorConfigModel();
        configModel.setConfig(config);
        config.put(DemoSignInPolicy.CONFIG_USERS, "agent@municipality.example##admin@municipality.example");

        when(context.getSession()).thenReturn(session);
        when(context.getRealm()).thenReturn(realm);
        when(context.getAuthenticationSession()).thenReturn(auth);
        when(context.getAuthenticatorConfig()).thenReturn(configModel);
        when(context.getEvent()).thenReturn(mock(EventBuilder.class, org.mockito.Mockito.RETURNS_SELF));
        when(session.users()).thenReturn(users);
        when(users.getUserByUsername(realm, "agent@municipality.example")).thenReturn(agent);
    }

    @Test
    void signs_in_a_listed_demo_user_with_both_factors() {
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "zelvo-demo:agent@municipality.example");

        authenticator.authenticate(context);

        verify(context).setUser(agent);
        verify(context).success();
        assertEquals("2", authNotes.get("level-of-authentication"));
        assertTrue(!clientNotes.containsKey(DemoSignInAuthenticator.LOGIN_HINT_NOTE), "The hint is not shown in later forms.");
    }

    @Test
    void passes_on_without_a_demo_hint() {
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "agent@municipality.example");

        authenticator.authenticate(context);

        verify(context).attempted();
        verify(context, never()).success();
        verify(context, never()).setUser(any());
    }

    @Test
    void passes_on_for_users_that_are_not_listed() {
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "zelvo-demo:someone@municipality.example");

        authenticator.authenticate(context);

        verify(context).attempted();
        verify(context, never()).setUser(any());
    }

    @Test
    void passes_on_for_listed_users_that_are_missing_or_disabled() {
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "zelvo-demo:admin@municipality.example");

        authenticator.authenticate(context);

        verify(context).attempted();
        when(agent.isEnabled()).thenReturn(false);
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "zelvo-demo:agent@municipality.example");
        authenticator.authenticate(context);
        verify(context, never()).success();
    }

    @Test
    void without_settings_nobody_signs_in() {
        config.clear();
        clientNotes.put(DemoSignInAuthenticator.LOGIN_HINT_NOTE, "zelvo-demo:agent@municipality.example");

        authenticator.authenticate(context);

        verify(context).attempted();
        verify(context, never()).success();
    }
}
