package zelvo.keycloak.emailcode;

import jakarta.ws.rs.core.MultivaluedMap;
import jakarta.ws.rs.core.Response;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.jboss.logging.Logger;
import org.keycloak.authentication.AuthenticationFlowContext;
import org.keycloak.authentication.AuthenticationFlowError;
import org.keycloak.authentication.Authenticator;
import org.keycloak.email.EmailException;
import org.keycloak.email.EmailTemplateProvider;
import org.keycloak.events.Details;
import org.keycloak.forms.login.LoginFormsProvider;
import org.keycloak.models.AuthenticatorConfigModel;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;
import org.keycloak.models.utils.FormMessage;
import org.keycloak.sessions.AuthenticationSessionModel;
import org.keycloak.storage.adapter.AbstractInMemoryUserAdapter;

/**
 * Sign in with a one-time code sent by email (module 4, "email + one-time code").
 *
 * <p>Step 1 asks for the email address; step 2 asks for the code. To avoid revealing which
 * addresses have accounts, step 2 looks the same whether or not a code was sent. People without an
 * account get one only when the tenant's {@link SignInPolicy} allows it, and only after they
 * enter a correct code. This is a single factor: Zelvo asks agents and admins for a second one.
 */
public final class EmailCodeAuthenticator implements Authenticator {

    static final String FORM_EMAIL = "zelvo-email.ftl";
    static final String FORM_CODE = "zelvo-email-code.ftl";
    static final String EMAIL_TEMPLATE = "zelvo-email-code.ftl";

    static final String NOTE_EMAIL = "zelvo.code.email";
    static final String NOTE_TARGET = "zelvo.code.target";
    static final String TARGET_NONE = "none";
    static final String TARGET_NEW = "new";
    static final String TARGET_USER_PREFIX = "user:";

    static final String CONFIG_POLICY = "signInPolicy";
    static final String CONFIG_DOMAINS = "allowedDomains";
    static final String CONFIG_TTL = "codeTtlSeconds";

    private static final Logger LOG = Logger.getLogger(EmailCodeAuthenticator.class);
    private static final List<String> CODE_NOTES = List.of(
        OneTimeCode.NOTE_HASH, OneTimeCode.NOTE_SALT, OneTimeCode.NOTE_EXPIRES, OneTimeCode.NOTE_SENT,
        OneTimeCode.NOTE_ATTEMPTS, NOTE_EMAIL, NOTE_TARGET);

    private final Clock clock;

    /** Time source, replaceable in tests. */
    @FunctionalInterface
    interface Clock {
        long nowSeconds();
    }

    public EmailCodeAuthenticator() {
        this(() -> System.currentTimeMillis() / 1000);
    }

    EmailCodeAuthenticator(Clock clock) {
        this.clock = clock;
    }

    @Override
    public void authenticate(AuthenticationFlowContext context) {
        UserModel user = context.getUser();
        if (user != null && user.getEmail() != null) {
            // Already identified earlier in the flow (for example re-authentication).
            startCode(context, SignInPolicy.normalizeEmail(user.getEmail()), targetFor(user));
            return;
        }
        context.challenge(emailForm(context, null, null));
    }

    @Override
    public void action(AuthenticationFlowContext context) {
        MultivaluedMap<String, String> form = context.getHttpRequest().getDecodedFormParameters();
        if (form.containsKey("back")) {
            clearNotes(context.getAuthenticationSession());
            context.challenge(emailForm(context, null, null));
        } else if (form.containsKey("resend")) {
            resend(context);
        } else if (form.containsKey("code")) {
            checkCode(context, form.getFirst("code"));
        } else if (form.containsKey("email")) {
            submitEmail(context, form.getFirst("email"));
        } else {
            context.challenge(emailForm(context, null, null));
        }
    }

    private void submitEmail(AuthenticationFlowContext context, String raw) {
        String email = SignInPolicy.normalizeEmail(raw);
        if (email == null) {
            context.challenge(emailForm(context, raw, new FormMessage("email", "zelvoEmailInvalid")));
            return;
        }

        UserModel user = context.getSession().users().getUserByEmail(context.getRealm(), email);
        String target;
        if (user != null) {
            target = targetFor(user);
        } else {
            target = policy(context).allowsNewAccount(email) ? TARGET_NEW : TARGET_NONE;
        }
        startCode(context, email, target);
    }

    private void startCode(AuthenticationFlowContext context, String email, String target) {
        AuthenticationSessionModel auth = context.getAuthenticationSession();
        clearNotes(auth);
        auth.setAuthNote(NOTE_EMAIL, email);
        auth.setAuthNote(NOTE_TARGET, target);
        sendNewCode(context, email, target);
        context.challenge(codeForm(context, email, null, null));
    }

    private void resend(AuthenticationFlowContext context) {
        AuthenticationSessionModel auth = context.getAuthenticationSession();
        String email = auth.getAuthNote(NOTE_EMAIL);
        if (email == null) {
            context.challenge(emailForm(context, null, null));
            return;
        }
        if (!OneTimeCode.mayResend(notes(auth), clock.nowSeconds())) {
            context.challenge(codeForm(context, email, new FormMessage(null, "zelvoCodeResendWait"), null));
            return;
        }
        sendNewCode(context, email, auth.getAuthNote(NOTE_TARGET));
        context.challenge(codeForm(context, email, null, new FormMessage(null, "zelvoCodeResent")));
    }

    private void checkCode(AuthenticationFlowContext context, String entered) {
        AuthenticationSessionModel auth = context.getAuthenticationSession();
        Map<String, String> notes = notes(auth);
        String email = auth.getAuthNote(NOTE_EMAIL);
        OneTimeCode.Check result = OneTimeCode.check(notes, entered, clock.nowSeconds());
        if (result != OneTimeCode.Check.NO_CODE && result != OneTimeCode.Check.TOO_MANY_ATTEMPTS) {
            auth.setAuthNote(OneTimeCode.NOTE_ATTEMPTS, Integer.toString(OneTimeCode.attemptsAfter(notes)));
        }

        String target = auth.getAuthNote(NOTE_TARGET);
        if (result == OneTimeCode.Check.VALID && !TARGET_NONE.equals(target)) {
            UserModel user = resolveUser(context, email, target);
            if (user != null) {
                clearNotes(auth);
                context.setUser(user);
                context.getEvent().detail(Details.AUTH_METHOD, "zelvo-email-code");
                context.success();
                return;
            }
            result = OneTimeCode.Check.WRONG;
        } else if (result == OneTimeCode.Check.VALID) {
            // No code was sent to this address, so no entered value can be right.
            result = OneTimeCode.Check.WRONG;
        }

        switch (result) {
            case EXPIRED -> context.failureChallenge(AuthenticationFlowError.EXPIRED_CODE,
                codeForm(context, email, new FormMessage("code", "zelvoCodeExpired"), null));
            case WRONG -> {
                if (OneTimeCode.attempts(notes(auth)) >= OneTimeCode.MAX_ATTEMPTS) {
                    clearNotes(auth);
                    context.failureChallenge(AuthenticationFlowError.INVALID_CREDENTIALS,
                        emailForm(context, email, new FormMessage(null, "zelvoCodeTooManyAttempts")));
                } else {
                    context.failureChallenge(AuthenticationFlowError.INVALID_CREDENTIALS,
                        codeForm(context, email, new FormMessage("code", "zelvoCodeInvalid"), null));
                }
            }
            case TOO_MANY_ATTEMPTS -> {
                clearNotes(auth);
                context.failureChallenge(AuthenticationFlowError.INVALID_CREDENTIALS,
                    emailForm(context, email, new FormMessage(null, "zelvoCodeTooManyAttempts")));
            }
            default -> context.challenge(emailForm(context, email, null));
        }
    }

    /** The account to sign in, creating it when the policy allowed a new one. */
    private UserModel resolveUser(AuthenticationFlowContext context, String email, String target) {
        KeycloakSession session = context.getSession();
        RealmModel realm = context.getRealm();
        UserModel user;
        if (target.startsWith(TARGET_USER_PREFIX)) {
            user = session.users().getUserById(realm, target.substring(TARGET_USER_PREFIX.length()));
        } else {
            user = session.users().getUserByEmail(realm, email);
            if (user == null) {
                if (!policy(context).allowsNewAccount(email)) {
                    return null;
                }
                user = session.users().addUser(realm, email);
                user.setEmail(email);
                user.setEnabled(true);
                context.getEvent().detail(Details.REGISTER_METHOD, "zelvo-email-code");
                LOG.debugf("Created an account for a verified email address in realm %s", realm.getName());
            }
        }
        if (user == null || !user.isEnabled() || !email.equalsIgnoreCase(user.getEmail())) {
            return null;
        }
        if (!user.isEmailVerified()) {
            // Entering the code proves the person controls the address.
            user.setEmailVerified(true);
        }
        return user;
    }

    private void sendNewCode(AuthenticationFlowContext context, String email, String target) {
        AuthenticationSessionModel auth = context.getAuthenticationSession();
        String code = OneTimeCode.generate();
        long ttl = ttlSeconds(context.getAuthenticatorConfig());
        OneTimeCode.issue(code, clock.nowSeconds(), ttl).forEach(auth::setAuthNote);
        if (TARGET_NONE.equals(target)) {
            return;
        }

        UserModel user = target.startsWith(TARGET_USER_PREFIX)
            ? context.getSession().users().getUserById(context.getRealm(), target.substring(TARGET_USER_PREFIX.length()))
            : new PendingRecipient(context.getSession(), context.getRealm(), email);
        Map<String, Object> attributes = new HashMap<>();
        attributes.put("code", code);
        attributes.put("ttlMinutes", Math.max(1, ttl / 60));
        attributes.put("realmName", context.getRealm().getDisplayName() != null
            ? context.getRealm().getDisplayName() : context.getRealm().getName());
        try {
            EmailTemplateProvider emails = context.getSession().getProvider(EmailTemplateProvider.class)
                .setRealm(context.getRealm())
                .setAuthenticationSession(auth)
                .setUser(user);
            emails.send("zelvoEmailCodeSubject", List.of(), EMAIL_TEMPLATE, attributes, email);
        } catch (EmailException e) {
            // The page stays the same so the response does not reveal whether the address exists.
            LOG.warnf("Could not send a sign-in code in realm %s: %s", context.getRealm().getName(), e.getMessage());
        }
    }

    private static String targetFor(UserModel user) {
        return user.isEnabled() ? TARGET_USER_PREFIX + user.getId() : TARGET_NONE;
    }

    static SignInPolicy policy(AuthenticationFlowContext context) {
        AuthenticatorConfigModel config = context.getAuthenticatorConfig();
        Map<String, String> values = config == null || config.getConfig() == null ? Map.of() : config.getConfig();
        return SignInPolicy.fromConfig(values.get(CONFIG_POLICY), values.get(CONFIG_DOMAINS));
    }

    static long ttlSeconds(AuthenticatorConfigModel config) {
        String value = config == null || config.getConfig() == null ? null : config.getConfig().get(CONFIG_TTL);
        try {
            long ttl = value == null ? OneTimeCode.DEFAULT_TTL_SECONDS : Long.parseLong(value);
            return Math.min(Math.max(ttl, 60), 3600);
        } catch (NumberFormatException e) {
            return OneTimeCode.DEFAULT_TTL_SECONDS;
        }
    }

    private static Map<String, String> notes(AuthenticationSessionModel auth) {
        Map<String, String> notes = new HashMap<>();
        for (String key : CODE_NOTES) {
            String value = auth.getAuthNote(key);
            if (value != null) {
                notes.put(key, value);
            }
        }
        return notes;
    }

    private static void clearNotes(AuthenticationSessionModel auth) {
        CODE_NOTES.forEach(auth::removeAuthNote);
    }

    private static Response emailForm(AuthenticationFlowContext context, String email, FormMessage error) {
        LoginFormsProvider form = context.form();
        if (email != null) {
            form.setAttribute("email", email);
        }
        if (error != null) {
            form.addError(error);
        }
        return form.createForm(FORM_EMAIL);
    }

    private static Response codeForm(AuthenticationFlowContext context, String email, FormMessage error, FormMessage info) {
        LoginFormsProvider form = context.form()
            .setAttribute("email", email)
            .setAttribute("codeLength", OneTimeCode.LENGTH);
        if (error != null) {
            form.addError(error);
        }
        if (info != null) {
            form.addSuccess(info);
        }
        return form.createForm(FORM_CODE);
    }

    /**
     * Stands in for an account that does not exist yet, so the email templates have a recipient.
     * Kept in memory only; the account is created after a correct code.
     */
    static final class PendingRecipient extends AbstractInMemoryUserAdapter {
        PendingRecipient(KeycloakSession session, RealmModel realm, String email) {
            super(session, realm, "zelvo-pending-" + java.util.UUID.randomUUID());
            setEmail(email);
            setUsername(email);
        }

        @Override
        public org.keycloak.models.SubjectCredentialManager credentialManager() {
            throw new UnsupportedOperationException("A pending recipient has no credentials.");
        }
    }

    @Override
    public boolean requiresUser() {
        return false;
    }

    @Override
    public boolean configuredFor(KeycloakSession session, RealmModel realm, UserModel user) {
        return user == null || user.getEmail() != null;
    }

    @Override
    public void setRequiredActions(KeycloakSession session, RealmModel realm, UserModel user) {
        // Nothing to set up: the address is the credential.
    }

    @Override
    public void close() {
        // Stateless.
    }
}
