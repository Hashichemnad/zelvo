package zelvo.keycloak.uaepass;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static zelvo.keycloak.uaepass.FirstLoginPolicy.Decision.CREATE_REQUESTER;
import static zelvo.keycloak.uaepass.FirstLoginPolicy.Decision.LINK_BY_EMIRATES_ID;
import static zelvo.keycloak.uaepass.FirstLoginPolicy.Decision.PROVE_EXISTING_ACCOUNT;
import static zelvo.keycloak.uaepass.FirstLoginPolicy.Decision.REFUSE_LEVEL_TOO_LOW;
import static zelvo.keycloak.uaepass.FirstLoginPolicy.Decision.REFUSE_NO_ACCOUNT;
import static zelvo.keycloak.uaepass.UaePassProfile.Level.SOP1;
import static zelvo.keycloak.uaepass.UaePassProfile.Level.SOP2;
import static zelvo.keycloak.uaepass.UaePassProfile.Level.SOP3;

import java.util.Map;
import org.junit.jupiter.api.Test;

class FirstLoginPolicyTest {

    private final FirstLoginPolicy defaults = FirstLoginPolicy.fromConfig(Map.of());

    @Test
    void defaults_create_requesters_from_sop1() {
        assertTrue(defaults.createRequesters());
        assertEquals(SOP1, defaults.requesterMinimum());
        assertEquals(CREATE_REQUESTER, defaults.decide(SOP1, 0, false));
    }

    @Test
    void a_verified_account_with_one_matching_emirates_id_is_linked_straight_away() {
        assertEquals(LINK_BY_EMIRATES_ID, defaults.decide(SOP2, 1, true));
        assertEquals(LINK_BY_EMIRATES_ID, new FirstLoginPolicy(false, SOP3).decide(SOP2, 1, false));
    }

    @Test
    void emirates_id_matches_of_unverified_or_ambiguous_accounts_are_not_used() {
        assertEquals(CREATE_REQUESTER, defaults.decide(SOP1, 1, false));
        assertEquals(PROVE_EXISTING_ACCOUNT, defaults.decide(SOP3, 2, true));
    }

    @Test
    void an_existing_email_needs_proof_whatever_the_settings() {
        assertEquals(PROVE_EXISTING_ACCOUNT, defaults.decide(SOP1, 0, true));
        assertEquals(PROVE_EXISTING_ACCOUNT, new FirstLoginPolicy(false, SOP3).decide(SOP1, 0, true));
    }

    @Test
    void without_automatic_accounts_only_known_people_get_in() {
        var knownOnly = FirstLoginPolicy.fromConfig(Map.of(FirstLoginPolicy.CONFIG_CREATE_REQUESTERS, "false"));

        assertFalse(knownOnly.createRequesters());
        assertEquals(REFUSE_NO_ACCOUNT, knownOnly.decide(SOP3, 0, false));
    }

    @Test
    void new_requesters_below_the_minimum_level_are_refused() {
        var verifiedOnly = FirstLoginPolicy.fromConfig(Map.of(FirstLoginPolicy.CONFIG_REQUESTER_MINIMUM_LEVEL, "SOP2"));

        assertEquals(REFUSE_LEVEL_TOO_LOW, verifiedOnly.decide(SOP1, 0, false));
        assertEquals(CREATE_REQUESTER, verifiedOnly.decide(SOP2, 0, false));
    }

    @Test
    void unknown_configuration_values_fall_back_to_the_defaults() {
        var policy = FirstLoginPolicy.fromConfig(Map.of(
            FirstLoginPolicy.CONFIG_CREATE_REQUESTERS, "maybe",
            FirstLoginPolicy.CONFIG_REQUESTER_MINIMUM_LEVEL, "SOP7"));

        assertTrue(policy.createRequesters());
        assertEquals(SOP1, policy.requesterMinimum());
    }
}
