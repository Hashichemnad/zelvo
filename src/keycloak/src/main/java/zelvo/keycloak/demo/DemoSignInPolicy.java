package zelvo.keycloak.demo;

import java.util.Arrays;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.stream.Collectors;

/**
 * Which sign-in requests the demo sign-in step answers (demo installations only, task D).
 *
 * <p>A request answers only when it carries {@code login_hint=zelvo-demo:<username>} and the
 * username is one of the demo users Zelvo listed in the step's settings. Everything else passes on
 * to the realm's usual sign-in steps. Zelvo adds the step only to the realms of a demo
 * installation, whose sample organisations hold no real data.
 */
public final class DemoSignInPolicy {

    /** Prefix of the login hint that asks for a demo user. */
    public static final String HINT_PREFIX = "zelvo-demo:";

    /** Setting: the demo users' usernames, separated by {@code ##} (Keycloak's multi-value separator). */
    public static final String CONFIG_USERS = "demoUsers";

    private final Set<String> usernames;

    private DemoSignInPolicy(Set<String> usernames) {
        this.usernames = usernames;
    }

    /** The policy from the step's settings; no settings means no demo users. */
    public static DemoSignInPolicy fromConfig(Map<String, String> config) {
        String value = config == null ? null : config.get(CONFIG_USERS);
        if (value == null || value.isBlank()) {
            return new DemoSignInPolicy(Set.of());
        }
        return new DemoSignInPolicy(Arrays.stream(value.split("##"))
            .map(String::trim)
            .filter(name -> !name.isEmpty())
            .map(name -> name.toLowerCase(Locale.ROOT))
            .collect(Collectors.toUnmodifiableSet()));
    }

    /** The demo user a login hint asks for, if it asks for one this realm lists. */
    public Optional<String> requestedUser(String loginHint) {
        if (loginHint == null || !loginHint.startsWith(HINT_PREFIX)) {
            return Optional.empty();
        }
        String username = loginHint.substring(HINT_PREFIX.length()).trim().toLowerCase(Locale.ROOT);
        return usernames.contains(username) ? Optional.of(username) : Optional.empty();
    }
}
