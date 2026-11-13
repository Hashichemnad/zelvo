package zelvo.keycloak.emailcode;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.HexFormat;
import java.util.Map;

/**
 * A one-time sign-in code and its state, kept in the Keycloak authentication session (server side,
 * never in the browser). Only a salted SHA-256 hash of the code is stored. A code works once, for a
 * limited time and a limited number of tries.
 */
public final class OneTimeCode {

    public static final int LENGTH = 6;
    public static final long DEFAULT_TTL_SECONDS = 600;
    public static final int MAX_ATTEMPTS = 5;
    public static final long RESEND_AFTER_SECONDS = 60;

    static final String NOTE_HASH = "zelvo.code.hash";
    static final String NOTE_SALT = "zelvo.code.salt";
    static final String NOTE_EXPIRES = "zelvo.code.expires";
    static final String NOTE_SENT = "zelvo.code.sent";
    static final String NOTE_ATTEMPTS = "zelvo.code.attempts";

    private static final SecureRandom RANDOM = new SecureRandom();

    /** Outcome of checking an entered code. */
    public enum Check {
        VALID,
        WRONG,
        EXPIRED,
        TOO_MANY_ATTEMPTS,
        NO_CODE
    }

    private OneTimeCode() {
    }

    /** A random code of {@link #LENGTH} digits (leading zeros allowed). */
    public static String generate() {
        StringBuilder code = new StringBuilder(LENGTH);
        for (int i = 0; i < LENGTH; i++) {
            code.append(RANDOM.nextInt(10));
        }
        return code.toString();
    }

    /** Session notes for a newly sent code. */
    public static Map<String, String> issue(String code, long nowSeconds, long ttlSeconds) {
        byte[] salt = new byte[16];
        RANDOM.nextBytes(salt);
        String encodedSalt = Base64.getEncoder().encodeToString(salt);
        return Map.of(
            NOTE_HASH, hash(code, encodedSalt),
            NOTE_SALT, encodedSalt,
            NOTE_EXPIRES, Long.toString(nowSeconds + ttlSeconds),
            NOTE_SENT, Long.toString(nowSeconds),
            NOTE_ATTEMPTS, "0");
    }

    /**
     * Checks an entered code against the notes. The caller must store {@link #attemptsAfter} back
     * before acting on the result, so every try counts even when the code is wrong.
     */
    public static Check check(Map<String, String> notes, String entered, long nowSeconds) {
        String expected = notes.get(NOTE_HASH);
        String salt = notes.get(NOTE_SALT);
        if (expected == null || salt == null) {
            return Check.NO_CODE;
        }
        if (attempts(notes) >= MAX_ATTEMPTS) {
            return Check.TOO_MANY_ATTEMPTS;
        }
        if (nowSeconds > parseLong(notes.get(NOTE_EXPIRES))) {
            return Check.EXPIRED;
        }
        String digits = entered == null ? "" : entered.replaceAll("\\s", "");
        if (digits.length() != LENGTH || !digits.chars().allMatch(Character::isDigit)) {
            return Check.WRONG;
        }
        boolean same = MessageDigest.isEqual(
            hash(digits, salt).getBytes(StandardCharsets.US_ASCII), expected.getBytes(StandardCharsets.US_ASCII));
        return same ? Check.VALID : Check.WRONG;
    }

    /** The attempt count to store after one more try. */
    public static int attemptsAfter(Map<String, String> notes) {
        return attempts(notes) + 1;
    }

    /** True when a new code may be sent (throttles resending). */
    public static boolean mayResend(Map<String, String> notes, long nowSeconds) {
        String sent = notes.get(NOTE_SENT);
        return sent == null || nowSeconds - parseLong(sent) >= RESEND_AFTER_SECONDS;
    }

    static int attempts(Map<String, String> notes) {
        return (int) parseLong(notes.get(NOTE_ATTEMPTS));
    }

    static String hash(String code, String salt) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            digest.update(Base64.getDecoder().decode(salt));
            return HexFormat.of().formatHex(digest.digest(code.getBytes(StandardCharsets.US_ASCII)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is always available", e);
        }
    }

    private static long parseLong(String value) {
        try {
            return value == null ? 0 : Long.parseLong(value);
        } catch (NumberFormatException e) {
            return 0;
        }
    }
}
