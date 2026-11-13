package zelvo.keycloak.emailcode;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import org.junit.jupiter.api.Test;

class OneTimeCodeTest {

    private static final long NOW = 1_000_000;

    @Test
    void codes_are_six_digits_and_vary() {
        Set<String> codes = new HashSet<>();
        for (int i = 0; i < 200; i++) {
            String code = OneTimeCode.generate();
            assertTrue(code.matches("\\d{6}"), code);
            codes.add(code);
        }
        assertTrue(codes.size() > 190, "codes repeat too often");
    }

    @Test
    void only_a_salted_hash_is_stored() {
        Map<String, String> first = OneTimeCode.issue("123456", NOW, 600);
        Map<String, String> second = OneTimeCode.issue("123456", NOW, 600);

        assertFalse(first.containsValue("123456"));
        assertNotEquals(first.get(OneTimeCode.NOTE_HASH), second.get(OneTimeCode.NOTE_HASH));
    }

    @Test
    void the_right_code_within_its_lifetime_is_valid() {
        Map<String, String> notes = OneTimeCode.issue("012345", NOW, 600);

        assertEquals(OneTimeCode.Check.VALID, OneTimeCode.check(notes, "012345", NOW + 599));
        assertEquals(OneTimeCode.Check.VALID, OneTimeCode.check(notes, " 012 345 ", NOW));
    }

    @Test
    void wrong_or_malformed_codes_are_wrong() {
        Map<String, String> notes = OneTimeCode.issue("012345", NOW, 600);

        assertEquals(OneTimeCode.Check.WRONG, OneTimeCode.check(notes, "012346", NOW));
        assertEquals(OneTimeCode.Check.WRONG, OneTimeCode.check(notes, "12345", NOW));
        assertEquals(OneTimeCode.Check.WRONG, OneTimeCode.check(notes, "abcdef", NOW));
        assertEquals(OneTimeCode.Check.WRONG, OneTimeCode.check(notes, null, NOW));
    }

    @Test
    void codes_expire() {
        Map<String, String> notes = OneTimeCode.issue("012345", NOW, 600);

        assertEquals(OneTimeCode.Check.EXPIRED, OneTimeCode.check(notes, "012345", NOW + 601));
    }

    @Test
    void after_five_tries_even_the_right_code_is_refused() {
        Map<String, String> notes = new HashMap<>(OneTimeCode.issue("012345", NOW, 600));
        for (int i = 0; i < OneTimeCode.MAX_ATTEMPTS; i++) {
            notes.put(OneTimeCode.NOTE_ATTEMPTS, Integer.toString(OneTimeCode.attemptsAfter(notes)));
        }

        assertEquals(OneTimeCode.Check.TOO_MANY_ATTEMPTS, OneTimeCode.check(notes, "012345", NOW));
    }

    @Test
    void without_a_code_nothing_is_valid() {
        assertEquals(OneTimeCode.Check.NO_CODE, OneTimeCode.check(Map.of(), "012345", NOW));
    }

    @Test
    void resending_waits_a_minute() {
        Map<String, String> notes = OneTimeCode.issue("012345", NOW, 600);

        assertFalse(OneTimeCode.mayResend(notes, NOW + 59));
        assertTrue(OneTimeCode.mayResend(notes, NOW + 60));
        assertTrue(OneTimeCode.mayResend(Map.of(), NOW));
    }
}
