package zelvo.keycloak.uaepass;

import com.fasterxml.jackson.databind.JsonNode;
import java.util.Optional;
import java.util.regex.Pattern;

/**
 * What Zelvo uses from a UAE PASS user information response (docs.uaepass.ae, Attributes List):
 * the account level ({@code userType}: SOP1, SOP2 or SOP3) and, for verified accounts, the
 * Emirates ID number ({@code idn}). Nothing else is kept.
 */
public record UaePassProfile(Level level, Optional<String> emiratesId) {

    /** UAE PASS account levels, lowest first. */
    public enum Level {
        /** Basic: email and mobile verified, identity not verified. */
        SOP1,
        /** Advanced: identity verified. */
        SOP2,
        /** Qualified: identity verified with biometrics. */
        SOP3;

        /** A level name, or empty when it is not one (case-insensitive). */
        public static Optional<Level> parse(String value) {
            if (value == null) {
                return Optional.empty();
            }
            for (Level level : values()) {
                if (level.name().equalsIgnoreCase(value.trim())) {
                    return Optional.of(level);
                }
            }
            return Optional.empty();
        }

        /** Whether UAE PASS verified the person's identity (SOP2 or higher). */
        public boolean verified() {
            return this.compareTo(SOP2) >= 0;
        }

        public boolean atLeast(Level minimum) {
            return this.compareTo(minimum) >= 0;
        }
    }

    /** Emirates ID numbers: 15 digits starting with 784. */
    private static final Pattern EMIRATES_ID = Pattern.compile("^784\\d{12}$");

    /**
     * Reads the profile. A missing or unknown {@code userType} counts as SOP1, the lowest level,
     * so nothing is granted on data UAE PASS did not send. The Emirates ID is only taken from
     * verified accounts, and only when it is well formed.
     */
    public static UaePassProfile from(JsonNode userInfo) {
        Level level = Optional.ofNullable(userInfo)
            .map(node -> node.get("userType"))
            .filter(JsonNode::isTextual)
            .flatMap(node -> Level.parse(node.asText()))
            .orElse(Level.SOP1);
        Optional<String> emiratesId = Optional.empty();
        if (level.verified()) {
            emiratesId = Optional.ofNullable(userInfo.get("idn"))
                .filter(JsonNode::isTextual)
                .map(node -> node.asText().replace("-", "").trim())
                .filter(value -> EMIRATES_ID.matcher(value).matches());
        }
        return new UaePassProfile(level, emiratesId);
    }

    /** Whether {@code value} is a well-formed Emirates ID number (digits only). */
    public static boolean isEmiratesId(String value) {
        return value != null && EMIRATES_ID.matcher(value).matches();
    }
}
