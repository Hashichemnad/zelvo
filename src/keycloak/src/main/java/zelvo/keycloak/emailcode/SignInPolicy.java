package zelvo.keycloak.emailcode;

import java.util.Arrays;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

/**
 * Who may sign in with an emailed one-time code. Set per tenant in Zelvo (Settings, identity) and
 * copied by the Zelvo API into this authenticator's configuration in the tenant's realm.
 *
 * <ul>
 *   <li>{@code known-users} (default): only people who already have an account.</li>
 *   <li>{@code allowed-domains}: also anyone whose email domain is listed; their account is
 *       created after they enter a correct code.</li>
 *   <li>{@code anyone}: anyone with an email address (public portals).</li>
 * </ul>
 */
public final class SignInPolicy {

    public enum Mode {
        KNOWN_USERS("known-users"),
        ALLOWED_DOMAINS("allowed-domains"),
        ANYONE("anyone");

        private final String value;

        Mode(String value) {
            this.value = value;
        }

        public String value() {
            return value;
        }

        /** Unknown or missing values fall back to the safest mode. */
        public static Mode parse(String value) {
            for (Mode mode : values()) {
                if (mode.value.equals(value)) {
                    return mode;
                }
            }
            return KNOWN_USERS;
        }
    }

    /** Separator Keycloak uses for multi-valued authenticator settings. */
    public static final String MULTIVALUED_SEPARATOR = "##";

    // Deliberately simple: one @, no spaces, a dot in the domain. Keycloak and SMTP do the rest.
    private static final Pattern EMAIL = Pattern.compile("^[^@\\s]{1,64}@[^@\\s]+\\.[^@\\s]+$");
    private static final Pattern DOMAIN = Pattern.compile("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$");
    private static final int EMAIL_MAX_LENGTH = 254;

    private final Mode mode;
    private final Set<String> allowedDomains;

    public SignInPolicy(Mode mode, Set<String> allowedDomains) {
        this.mode = mode;
        this.allowedDomains = allowedDomains.stream()
            .map(SignInPolicy::normalizeDomain)
            .filter(domain -> DOMAIN.matcher(domain).matches())
            .collect(Collectors.toUnmodifiableSet());
    }

    /** Reads the policy from authenticator settings ({@code signInPolicy}, {@code allowedDomains}). */
    public static SignInPolicy fromConfig(String mode, String allowedDomains) {
        Set<String> domains = allowedDomains == null || allowedDomains.isBlank()
            ? Set.of()
            : Arrays.stream(allowedDomains.split(MULTIVALUED_SEPARATOR + "|[,\\s]+"))
                .filter(value -> !value.isBlank())
                .collect(Collectors.toSet());
        return new SignInPolicy(Mode.parse(mode), domains);
    }

    public Mode mode() {
        return mode;
    }

    public Set<String> allowedDomains() {
        return allowedDomains;
    }

    /** May someone without an account get one by proving they own this (normalised) address? */
    public boolean allowsNewAccount(String normalizedEmail) {
        return switch (mode) {
            case KNOWN_USERS -> false;
            case ANYONE -> true;
            case ALLOWED_DOMAINS -> allowedDomains.contains(domainOf(normalizedEmail));
        };
    }

    /** Trims and lower-cases an address; returns null when it is not a plausible address. */
    public static String normalizeEmail(String raw) {
        if (raw == null) {
            return null;
        }
        String email = raw.trim().toLowerCase(Locale.ROOT);
        return email.length() <= EMAIL_MAX_LENGTH && EMAIL.matcher(email).matches() ? email : null;
    }

    static String domainOf(String normalizedEmail) {
        int at = normalizedEmail.lastIndexOf('@');
        return at < 0 ? "" : normalizedEmail.substring(at + 1);
    }

    private static String normalizeDomain(String domain) {
        String value = domain.trim().toLowerCase(Locale.ROOT);
        return value.startsWith("@") ? value.substring(1) : value;
    }
}
