package zelvo.keycloak.uaepass;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.util.Optional;
import org.junit.jupiter.api.Test;

class UaePassProfileTest {

    private static final ObjectMapper JSON = new ObjectMapper();

    private static JsonNode json(String value) throws Exception {
        return JSON.readTree(value);
    }

    @Test
    void verified_accounts_give_their_level_and_emirates_id() throws Exception {
        var profile = UaePassProfile.from(json("{\"userType\":\"SOP3\",\"idn\":\"784-1990-1234567-1\"}"));

        assertEquals(UaePassProfile.Level.SOP3, profile.level());
        assertEquals(Optional.of("784199012345671"), profile.emiratesId());
    }

    @Test
    void basic_accounts_never_give_an_emirates_id() throws Exception {
        var profile = UaePassProfile.from(json("{\"userType\":\"SOP1\",\"idn\":\"784199012345671\"}"));

        assertEquals(UaePassProfile.Level.SOP1, profile.level());
        assertTrue(profile.emiratesId().isEmpty());
    }

    @Test
    void missing_or_unknown_levels_count_as_the_lowest() throws Exception {
        assertEquals(UaePassProfile.Level.SOP1, UaePassProfile.from(json("{}")).level());
        assertEquals(UaePassProfile.Level.SOP1, UaePassProfile.from(json("{\"userType\":\"SOP9\"}")).level());
        assertEquals(UaePassProfile.Level.SOP1, UaePassProfile.from(json("{\"userType\":3}")).level());
        assertEquals(UaePassProfile.Level.SOP1, UaePassProfile.from(null).level());
    }

    @Test
    void malformed_emirates_ids_are_ignored() throws Exception {
        assertTrue(UaePassProfile.from(json("{\"userType\":\"SOP2\",\"idn\":\"123456789012345\"}")).emiratesId().isEmpty());
        assertTrue(UaePassProfile.from(json("{\"userType\":\"SOP2\",\"idn\":\"78419901234567\"}")).emiratesId().isEmpty());
        assertTrue(UaePassProfile.from(json("{\"userType\":\"SOP2\",\"idn\":784199012345671}")).emiratesId().isEmpty());
    }

    @Test
    void levels_compare_and_only_sop2_and_up_are_verified() {
        assertFalse(UaePassProfile.Level.SOP1.verified());
        assertTrue(UaePassProfile.Level.SOP2.verified());
        assertTrue(UaePassProfile.Level.SOP3.atLeast(UaePassProfile.Level.SOP2));
        assertFalse(UaePassProfile.Level.SOP1.atLeast(UaePassProfile.Level.SOP2));
        assertEquals(Optional.of(UaePassProfile.Level.SOP2), UaePassProfile.Level.parse(" sop2 "));
    }
}
