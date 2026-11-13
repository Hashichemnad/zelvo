package zelvo.keycloak.emailcode;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyList;
import static org.mockito.ArgumentMatchers.anyMap;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.RETURNS_SELF;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.times;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import jakarta.ws.rs.core.MultivaluedHashMap;
import jakarta.ws.rs.core.Response;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.email.EmailTemplateProvider;
import org.keycloak.events.EventBuilder;
import org.keycloak.forms.login.LoginFormsProvider;
import org.keycloak.http.HttpRequest;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.models.UserProvider;
import org.keycloak.sessions.AuthenticationSessionModel;
import org.mockito.ArgumentCaptor;

/** The sign-in steps against mocked Keycloak services. */
class EmailCodeAuthenticatorTest {

    private final AtomicLong now = new AtomicLong(1_000_000);
    private final EmailCodeAuthenticator authenticator = new EmailCodeAuthenticator(now::get);

    private final Map<String, String> authNotes = new HashMap<>();
    private final Map<String, String> config = new HashMap<>();
    private final AtomicReference<String> sentCode = new AtomicReference<>();
    private final AtomicReference<String> sentTo = new AtomicReference<>();

    private AuthenticationFlowContext context;
    private UserProvider users;
    private LoginFormsProvider forms;
    private EmailTemplateProvider emails;
    private Response emailForm;
    private Response codeForm;

    @BeforeEach
    void setUp() throws Exception {
        context = mock(AuthenticationFlowContext.class);
        KeycloakSession session = mock(KeycloakSession.class);
        RealmModel realm = mock(RealmModel.class);
        users = mock(UserProvider.class);
        forms = mock(LoginFormsProvider.class, RETURNS_SELF);
        emails = mock(EmailTemplateProvider.class, RETURNS_SELF);
        emailForm = mock(Response.class);
        codeForm = mock(Response.class);

        AuthenticationSessionModel auth = mock(AuthenticationSessionModel.class);
        when(auth.getAuthNote(anyString())).thenAnswer(call -> authNotes.get(call.<String>getArgument(0)));
        org.mockito.Mockito.doAnswer(call -> authNotes.put(call.getArgument(0), call.getArgument(1)))
            .when(auth).setAuthNote(anyString(), anyString());
        org.mockito.Mockito.doAnswer(call -> authNotes.remove(call.<String>getArgument(0)))
            .when(auth).removeAuthNote(anyString());

        AuthenticatorConfigModel configModel = new AuthenticatorConfigModel();
        configModel.setConfig(config);

        when(context.getSession()).thenReturn(session);
        when(context.getRealm()).thenReturn(realm);
        when(context.getAuthenticationSession()).thenReturn(auth);
        when(context.getAuthenticatorConfig()).thenReturn(configModel);
        when(context.form()).thenReturn(forms);
        when(context.getEvent()).thenReturn(mock(EventBuilder.class, RETURNS_SELF));
        when(session.users()).thenReturn(users);
        when(session.getProvider(EmailTemplateProvider.class)).thenReturn(emails);
        when(realm.getName()).thenReturn("zelvo-default");
        when(forms.createForm(EmailCodeAuthenticator.FORM_EMAIL)).thenReturn(emailForm);
        when(forms.createForm(EmailCodeAuthenticator.FORM_CODE)).thenReturn(codeForm);

        org.mockito.Mockito.doAnswer(call -> {
            Map<String, Object> attributes = call.getArgument(3);
            sentCode.set((String) attributes.get("code"));
            sentTo.set(call.getArgument(4));
            return null;
        }).when(emails).send(anyString(), anyList(), anyString(), anyMap(), anyString());
    }

    @Test
    void a_known_user_gets_a_code_and_signs_in_with_it() throws Exception {
        UserModel user = user("u1", "someone@ajman.ae", true);

        submit("email", " Someone@Ajman.ae ");
        assertEquals("someone@ajman.ae", sentTo.get());
        verify(context).challenge(codeForm);

        submit("code", sentCode.get());
        verify(context).setUser(user);
        verify(context).success();
        verify(users, never()).addUser(any(), anyString());
    }

    @Test
    void an_unknown_address_with_known_users_only_gets_the_same_page_but_no_code() throws Exception {
        config.put("signInPolicy", "known-users");

        submit("email", "stranger@ajman.ae");
        verify(context).challenge(codeForm);
        verify(emails, never()).send(anyString(), anyList(), anyString(), anyMap(), anyString());

        submit("code", "000000");
        verify(context).failureChallenge(eq(AuthenticationFlowError.INVALID_CREDENTIALS), eq(codeForm));
        verify(context, never()).success();
        verify(users, never()).addUser(any(), anyString());
    }

    @Test
    void an_allowed_domain_gets_an_account_after_a_correct_code() throws Exception {
        config.put("signInPolicy", "allowed-domains");
        config.put("allowedDomains", "ajman.ae##am.gov.ae");
        UserModel created = mock(UserModel.class);
        when(created.getEmail()).thenReturn("new.person@am.gov.ae");
        when(created.isEnabled()).thenReturn(true);
        when(users.addUser(any(), eq("new.person@am.gov.ae"))).thenReturn(created);

        submit("email", "new.person@am.gov.ae");
        assertEquals("new.person@am.gov.ae", sentTo.get());
        verify(users, never()).addUser(any(), anyString());

        submit("code", sentCode.get());
        verify(created).setEmail("new.person@am.gov.ae");
        verify(created).setEnabled(true);
        verify(created).setEmailVerified(true);
        verify(context).setUser(created);
        verify(context).success();
    }

    @Test
    void another_domain_gets_no_code_and_no_account() throws Exception {
        config.put("signInPolicy", "allowed-domains");
        config.put("allowedDomains", "ajman.ae");

        submit("email", "someone@gmail.com");
        submit("code", "123456");

        verify(emails, never()).send(anyString(), anyList(), anyString(), anyMap(), anyString());
        verify(users, never()).addUser(any(), anyString());
        verify(context, never()).success();
    }

    @Test
    void anyone_gets_an_account_after_a_correct_code() throws Exception {
        config.put("signInPolicy", "anyone");
        UserModel created = mock(UserModel.class);
        when(created.getEmail()).thenReturn("citizen@example.com");
        when(created.isEnabled()).thenReturn(true);
        when(users.addUser(any(), eq("citizen@example.com"))).thenReturn(created);

        submit("email", "citizen@example.com");
        submit("code", sentCode.get());

        verify(context).setUser(created);
        verify(context).success();
    }

    @Test
    void a_disabled_user_gets_no_code() throws Exception {
        user("u2", "gone@ajman.ae", false);

        submit("email", "gone@ajman.ae");
        submit("code", "123456");

        verify(emails, never()).send(anyString(), anyList(), anyString(), anyMap(), anyString());
        verify(context, never()).success();
    }

    @Test
    void five_wrong_codes_end_the_attempt() throws Exception {
        user("u1", "someone@ajman.ae", true);
        submit("email", "someone@ajman.ae");
        String code = sentCode.get();
        String wrong = code.equals("000000") ? "111111" : "000000";

        for (int i = 0; i < OneTimeCode.MAX_ATTEMPTS; i++) {
            submit("code", wrong);
        }
        submit("code", code);

        verify(context, never()).success();
        verify(forms, times(1)).addError(org.mockito.ArgumentMatchers.argThat(
            message -> "zelvoCodeTooManyAttempts".equals(message.getMessage())));
    }

    @Test
    void expired_codes_are_refused() throws Exception {
        user("u1", "someone@ajman.ae", true);
        submit("email", "someone@ajman.ae");
        now.addAndGet(OneTimeCode.DEFAULT_TTL_SECONDS + 1);

        submit("code", sentCode.get());

        verify(context).failureChallenge(AuthenticationFlowError.EXPIRED_CODE, codeForm);
        verify(context, never()).success();
    }

    @Test
    void resending_is_throttled_and_replaces_the_code() throws Exception {
        user("u1", "someone@ajman.ae", true);
        submit("email", "someone@ajman.ae");
        String first = sentCode.get();

        submit("resend", "");
        verify(emails, times(1)).send(anyString(), anyList(), anyString(), anyMap(), anyString());

        now.addAndGet(OneTimeCode.RESEND_AFTER_SECONDS);
        submit("resend", "");
        verify(emails, times(2)).send(anyString(), anyList(), anyString(), anyMap(), anyString());

        if (!first.equals(sentCode.get())) {
            submit("code", first);
            verify(context, never()).success();
        }
        submit("code", sentCode.get());
        verify(context).success();
    }

    @Test
    void an_invalid_address_shows_the_email_form_with_an_error() throws Exception {
        submit("email", "not an address");

        ArgumentCaptor<org.keycloak.models.utils.FormMessage> error =
            ArgumentCaptor.forClass(org.keycloak.models.utils.FormMessage.class);
        verify(forms).addError(error.capture());
        assertEquals("email", error.getValue().getField());
        verify(context).challenge(emailForm);
        assertTrue(authNotes.isEmpty());
    }

    private UserModel user(String id, String email, boolean enabled) {
        UserModel user = mock(UserModel.class);
        when(user.getId()).thenReturn(id);
        when(user.getEmail()).thenReturn(email);
        when(user.isEnabled()).thenReturn(enabled);
        when(user.isEmailVerified()).thenReturn(true);
        when(users.getUserByEmail(any(), eq(email))).thenReturn(user);
        when(users.getUserById(any(), eq(id))).thenReturn(user);
        return user;
    }

    private void submit(String field, String value) {
        MultivaluedHashMap<String, String> form = new MultivaluedHashMap<>();
        form.add(field, value);
        HttpRequest request = mock(HttpRequest.class);
        when(request.getDecodedFormParameters()).thenReturn(form);
        when(context.getHttpRequest()).thenReturn(request);
        authenticator.action(context);
    }
}
