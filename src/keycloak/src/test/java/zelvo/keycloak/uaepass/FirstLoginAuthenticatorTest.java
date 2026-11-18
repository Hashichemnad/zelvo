package zelvo.keycloak.uaepass;

import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.RETURNS_SELF;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import jakarta.ws.rs.core.Response;
import java.util.HashMap;
import java.util.Map;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.authenticators.broker.util.SerializedBrokeredIdentityContext;
import org.keycloak.broker.provider.BrokeredIdentityContext;
import org.keycloak.events.EventBuilder;
import org.keycloak.forms.login.LoginFormsProvider;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.FederatedIdentityModel;
import org.keycloak.models.IdentityProviderModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.models.UserProvider;
import org.keycloak.sessions.AuthenticationSessionModel;

/** The first UAE PASS sign-in step against mocked Keycloak services. */
class FirstLoginAuthenticatorTest {

    private static final String EMIRATES_ID = "784199012345671";

    private final FirstLoginAuthenticator authenticator = new FirstLoginAuthenticator();
    private final Map<String, String> userSessionNotes = new HashMap<>();
    private final Map<String, String> authNotes = new HashMap<>();
    private final Map<String, String> config = new HashMap<>();

    private AuthenticationFlowContext context;
    private UserProvider users;
    private RealmModel realm;
    private BrokeredIdentityContext broker;
    private SerializedBrokeredIdentityContext serialized;
    private UserModel staff;

    @BeforeEach
    void setUp() {
        context = mock(AuthenticationFlowContext.class);
        KeycloakSession session = mock(KeycloakSession.class);
        users = mock(UserProvider.class);
        realm = mock(RealmModel.class);
        AuthenticationSessionModel authSession = mock(AuthenticationSessionModel.class);
        when(context.getSession()).thenReturn(session);
        when(context.getRealm()).thenReturn(realm);
        when(context.getAuthenticationSession()).thenReturn(authSession);
        when(session.users()).thenReturn(users);
        when(authSession.getUserSessionNotes()).thenReturn(userSessionNotes);
        when(authSession.getAuthNote(anyString())).thenAnswer(a -> authNotes.get(a.getArgument(0, String.class)));
        AuthenticatorConfigModel configModel = new AuthenticatorConfigModel();
        configModel.setConfig(config);
        when(context.getAuthenticatorConfig()).thenReturn(configModel);

        LoginFormsProvider forms = mock(LoginFormsProvider.class, RETURNS_SELF);
        when(forms.createErrorPage(any())).thenReturn(mock(Response.class));
        when(context.form()).thenReturn(forms);
        when(context.getEvent()).thenReturn(mock(EventBuilder.class, RETURNS_SELF));

        broker = mock(BrokeredIdentityContext.class);
        IdentityProviderModel idp = new IdentityProviderModel();
        idp.setAlias("uaepass");
        when(broker.getIdpConfig()).thenReturn(idp);
        when(broker.getEmail()).thenReturn("person@example.ae");
        when(broker.getModelUsername()).thenReturn("person@example.ae");
        when(broker.getUsername()).thenReturn("person@example.ae");
        serialized = mock(SerializedBrokeredIdentityContext.class);
        when(serialized.getEmail()).thenReturn("person@example.ae");
        when(serialized.getModelUsername()).thenReturn("person@example.ae");

        staff = mock(UserModel.class);
        when(staff.getId()).thenReturn("staff-1");
    }

    private final Map<String, Object> contextData = new HashMap<>();

    private void signIn(String level, String emiratesId) {
        // As the profile mapper leaves them; session notes are reset before this step.
        contextData.put(UaePassProfileMapper.CONTEXT_LEVEL, level);
        if (emiratesId != null) {
            contextData.put(UaePassProfileMapper.CONTEXT_EMIRATES_ID, emiratesId);
        }
        when(broker.getContextData()).thenReturn(contextData);
    }

    private void recorded(UserModel... matches) {
        when(users.searchForUserByUserAttributeStream(realm, UaePassNotes.EMIRATES_ID_ATTRIBUTE, EMIRATES_ID))
            .thenAnswer(a -> Stream.of(matches));
    }

    @Test
    void links_a_verified_sign_in_to_the_account_with_its_emirates_id() {
        signIn("SOP2", EMIRATES_ID);
        recorded(staff);
        config.put(FirstLoginPolicy.CONFIG_CREATE_REQUESTERS, "false");

        authenticator.authenticateImpl(context, serialized, broker);

        verify(context).setUser(staff);
        verify(context).success();
        verify(users, never()).addUser(any(), anyString());
    }

    @Test
    void refuses_when_that_account_already_has_another_uae_pass_account() {
        signIn("SOP3", EMIRATES_ID);
        recorded(staff);
        when(users.getFederatedIdentity(realm, staff, "uaepass")).thenReturn(new FederatedIdentityModel("uaepass", "other", "other"));

        authenticator.authenticateImpl(context, serialized, broker);

        verify(context, never()).setUser(any());
        verify(context).failureChallenge(eq(AuthenticationFlowError.ACCESS_DENIED), any());
    }

    @Test
    void without_automatic_accounts_unknown_people_are_refused() {
        signIn("SOP3", null);
        config.put(FirstLoginPolicy.CONFIG_CREATE_REQUESTERS, "false");

        authenticator.authenticateImpl(context, serialized, broker);

        verify(context).failureChallenge(eq(AuthenticationFlowError.ACCESS_DENIED), any());
        verify(users, never()).addUser(any(), anyString());
    }

    @Test
    void new_requesters_below_the_minimum_level_are_refused() {
        signIn("SOP1", null);
        config.put(FirstLoginPolicy.CONFIG_REQUESTER_MINIMUM_LEVEL, "SOP2");

        authenticator.authenticateImpl(context, serialized, broker);

        verify(context).failureChallenge(eq(AuthenticationFlowError.ACCESS_DENIED), any());
        verify(users, never()).addUser(any(), anyString());
    }

    @Test
    void an_unverified_sign_in_is_never_linked_by_emirates_id() {
        signIn("SOP1", EMIRATES_ID);
        recorded(staff);
        config.put(FirstLoginPolicy.CONFIG_CREATE_REQUESTERS, "false");

        authenticator.authenticateImpl(context, serialized, broker);

        verify(context, never()).setUser(any());
        verify(context).failureChallenge(eq(AuthenticationFlowError.ACCESS_DENIED), any());
    }
}
