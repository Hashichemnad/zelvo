package zelvo.keycloak.emailcode;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Set;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.NullAndEmptySource;
import org.junit.jupiter.params.provider.ValueSource;

class SignInPolicyTest {

    @Test
    void known_users_only_never_creates_accounts() {
        SignInPolicy policy = SignInPolicy.fromConfig("known-users", "ajman.ae");

        assertFalse(policy.allowsNewAccount("someone@ajman.ae"));
    }

    @ParameterizedTest
    @NullAndEmptySource
    @ValueSource(strings = {"KNOWN-USERS", "everyone", "allowed_domains"})
    void missing_or_unknown_modes_fall_back_to_known_users(String mode) {
        assertEquals(SignInPolicy.Mode.KNOWN_USERS, SignInPolicy.fromConfig(mode, null).mode());
    }

    @ParameterizedTest
    @CsvSource({
        "someone@ajman.ae, true",
        "someone@am.gov.ae, true",
        "someone@sub.ajman.ae, false",
        "someone@ajman.ae.evil.com, false",
        "someone@evilajman.ae, false",
        "someone@gmail.com, false",
    })
    void allowed_domains_match_the_exact_domain(String email, boolean allowed) {
        SignInPolicy policy = SignInPolicy.fromConfig("allowed-domains", "ajman.ae##AM.gov.ae");

        assertEquals(allowed, policy.allowsNewAccount(email));
    }

    @Test
    void allowed_domains_accept_commas_spaces_and_a_leading_at_sign_and_skip_invalid_entries() {
        SignInPolicy policy = SignInPolicy.fromConfig("allowed-domains", " @Ajman.ae, am.gov.ae  not a domain ");

        assertEquals(Set.of("ajman.ae", "am.gov.ae"), policy.allowedDomains());
    }

    @Test
    void allowed_domains_without_domains_allows_nobody() {
        assertFalse(SignInPolicy.fromConfig("allowed-domains", "").allowsNewAccount("someone@ajman.ae"));
    }

    @Test
    void anyone_allows_every_address() {
        assertTrue(SignInPolicy.fromConfig("anyone", null).allowsNewAccount("someone@example.com"));
    }

    @ParameterizedTest
    @CsvSource({
        "'  Someone@Ajman.AE ', someone@ajman.ae",
        "a.b+tag@x.co, a.b+tag@x.co",
    })
    void addresses_are_trimmed_and_lower_cased(String raw, String expected) {
        assertEquals(expected, SignInPolicy.normalizeEmail(raw));
    }

    @ParameterizedTest
    @NullAndEmptySource
    @ValueSource(strings = {"no-at-sign", "two@@ats.com", "a@b", "spaces in@x.com", "a@b@c.com"})
    void implausible_addresses_are_refused(String raw) {
        assertNull(SignInPolicy.normalizeEmail(raw));
    }

    @Test
    void overly_long_addresses_are_refused() {
        assertNull(SignInPolicy.normalizeEmail("a".repeat(60) + "@" + "b".repeat(200) + ".com"));
    }
}
