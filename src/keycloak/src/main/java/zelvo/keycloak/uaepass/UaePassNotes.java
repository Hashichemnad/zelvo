package zelvo.keycloak.uaepass;

/** Names shared by the UAE PASS mapper, the first-login step and Zelvo's token claims. */
public final class UaePassNotes {

    /**
     * User session note with this sign-in's UAE PASS account level (SOP1, SOP2 or SOP3). The Zelvo
     * API reads it from the access token claim {@code zelvo_uaepass_level}.
     */
    public static final String LEVEL = "zelvo.uaepass.level";

    /** User attribute holding the Emirates ID an administrator recorded for a (staff) account. */
    public static final String EMIRATES_ID_ATTRIBUTE = "zelvo_emirates_id";

    private UaePassNotes() {
    }
}
