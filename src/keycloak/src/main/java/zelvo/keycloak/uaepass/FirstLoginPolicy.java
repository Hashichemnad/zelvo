package zelvo.keycloak.uaepass;

import java.util.Map;

/**
 * What happens at a person's first UAE PASS sign-in (GOV-1, product owner decisions 2026-11-18).
 * The tenant's settings are copied by the Zelvo API into the configuration of
 * {@link FirstLoginAuthenticator} in the tenant's realm.
 *
 * <ol>
 *   <li>A verified account (SOP2 or higher) whose Emirates ID is recorded on exactly one
 *       existing account is linked to it straight away. This is how staff accounts are matched.</li>
 *   <li>Otherwise, an existing account with the same email is linked only after its owner proves
 *       it is theirs (the next steps of the flow: a link sent to that email, or its password).</li>
 *   <li>Otherwise a new account is created, as a requester, when the tenant creates requester
 *       accounts automatically and the account level meets the requesters' minimum. Staff
 *       accounts are never created here.</li>
 * </ol>
 */
public final class FirstLoginPolicy {

    /** Configuration key: "true" (default) or "false". */
    public static final String CONFIG_CREATE_REQUESTERS = "createRequesters";

    /** Configuration key: SOP1 (default), SOP2 or SOP3. */
    public static final String CONFIG_REQUESTER_MINIMUM_LEVEL = "requesterMinimumLevel";

    public enum Decision {
        /** Link to the account found by Emirates ID. */
        LINK_BY_EMIRATES_ID,
        /** An account has this email: continue to the steps where its owner proves it. */
        PROVE_EXISTING_ACCOUNT,
        /** Create a requester account. */
        CREATE_REQUESTER,
        /** The tenant does not create accounts, and none matched. */
        REFUSE_NO_ACCOUNT,
        /** A new account would be below the requesters' minimum level. */
        REFUSE_LEVEL_TOO_LOW,
    }

    private final boolean createRequesters;
    private final UaePassProfile.Level requesterMinimum;

    public FirstLoginPolicy(boolean createRequesters, UaePassProfile.Level requesterMinimum) {
        this.createRequesters = createRequesters;
        this.requesterMinimum = requesterMinimum;
    }

    /** From an authenticator configuration; missing or unknown values give the defaults. */
    public static FirstLoginPolicy fromConfig(Map<String, String> config) {
        Map<String, String> values = config == null ? Map.of() : config;
        boolean create = !"false".equalsIgnoreCase(values.getOrDefault(CONFIG_CREATE_REQUESTERS, "true").trim());
        UaePassProfile.Level minimum = UaePassProfile.Level.parse(values.get(CONFIG_REQUESTER_MINIMUM_LEVEL))
            .orElse(UaePassProfile.Level.SOP1);
        return new FirstLoginPolicy(create, minimum);
    }

    /**
     * @param level the UAE PASS account level of this sign-in
     * @param emiratesIdMatches how many accounts have this sign-in's Emirates ID (0 when it has none)
     * @param emailMatches whether an account with the same email exists
     */
    public Decision decide(UaePassProfile.Level level, int emiratesIdMatches, boolean emailMatches) {
        if (level.verified() && emiratesIdMatches == 1) {
            return Decision.LINK_BY_EMIRATES_ID;
        }
        if (emailMatches) {
            return Decision.PROVE_EXISTING_ACCOUNT;
        }
        if (!createRequesters) {
            return Decision.REFUSE_NO_ACCOUNT;
        }
        if (!level.atLeast(requesterMinimum)) {
            return Decision.REFUSE_LEVEL_TOO_LOW;
        }
        return Decision.CREATE_REQUESTER;
    }

    public boolean createRequesters() {
        return createRequesters;
    }

    public UaePassProfile.Level requesterMinimum() {
        return requesterMinimum;
    }
}
