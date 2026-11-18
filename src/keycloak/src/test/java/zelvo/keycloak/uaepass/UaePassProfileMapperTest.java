package zelvo.keycloak.uaepass;

import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import com.fasterxml.jackson.databind.ObjectMapper;
import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.keycloak.broker.oidc.mappers.AbstractJsonUserAttributeMapper;
import org.keycloak.broker.provider.BrokeredIdentityContext;
import org.keycloak.sessions.AuthenticationSessionModel;

class UaePassProfileMapperTest {

    private final AuthenticationSessionModel authSession = mock(AuthenticationSessionModel.class);
    private final BrokeredIdentityContext context = mock(BrokeredIdentityContext.class);
    private final Map<String, Object> contextData = new HashMap<>();

    private void userInfo(String json) throws Exception {
        contextData.put(AbstractJsonUserAttributeMapper.CONTEXT_JSON_NODE, new ObjectMapper().readTree(json));
        when(context.getContextData()).thenReturn(contextData);
        when(context.getAuthenticationSession()).thenReturn(authSession);
    }

    @Test
    void keeps_the_level_for_the_session_and_the_emirates_id_for_first_sign_in_only() throws Exception {
        userInfo("{\"userType\":\"SOP3\",\"idn\":\"784199012345671\",\"email\":\"a@b.ae\"}");

        UaePassProfileMapper.apply(context);

        verify(authSession).setUserSessionNote(UaePassNotes.LEVEL, "SOP3");
        org.junit.jupiter.api.Assertions.assertEquals("784199012345671", contextData.get(UaePassProfileMapper.CONTEXT_EMIRATES_ID));
        org.junit.jupiter.api.Assertions.assertEquals("SOP3", contextData.get(UaePassProfileMapper.CONTEXT_LEVEL));
    }

    @Test
    void basic_accounts_get_sop1_and_no_emirates_id() throws Exception {
        userInfo("{\"userType\":\"SOP1\"}");

        UaePassProfileMapper.apply(context);

        verify(authSession).setUserSessionNote(UaePassNotes.LEVEL, "SOP1");
        org.junit.jupiter.api.Assertions.assertFalse(contextData.containsKey(UaePassProfileMapper.CONTEXT_EMIRATES_ID));
    }

    @Test
    void sets_the_level_again_after_the_first_sign_in_steps() throws Exception {
        userInfo("{\"userType\":\"SOP2\"}");
        UaePassProfileMapper.apply(context);
        contextData.remove(AbstractJsonUserAttributeMapper.CONTEXT_JSON_NODE);
        org.mockito.Mockito.clearInvocations(authSession);

        UaePassProfileMapper.restoreLevel(context);

        verify(authSession).setUserSessionNote(UaePassNotes.LEVEL, "SOP2");
    }

    @Test
    void sets_nothing_when_the_level_is_unknown() {
        when(context.getContextData()).thenReturn(contextData);
        when(context.getAuthenticationSession()).thenReturn(authSession);

        UaePassProfileMapper.restoreLevel(context);

        verify(authSession, never()).setUserSessionNote(anyString(), anyString());
    }

    @Test
    void only_works_with_keycloaks_oauth2_broker() {
        org.junit.jupiter.api.Assertions.assertArrayEquals(new String[] {"oauth2"}, new UaePassProfileMapper().getCompatibleProviders());
    }
}
