package zelvo.keycloak.demo;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Map;
import java.util.Optional;
import org.junit.jupiter.api.Test;

class DemoSignInPolicyTest {

    private final DemoSignInPolicy policy = DemoSignInPolicy.fromConfig(
        Map.of(DemoSignInPolicy.CONFIG_USERS, "agent@municipality.example##Admin@municipality.example"));

    @Test
    void answers_hints_for_listed_demo_users_only() {
        assertEquals(Optional.of("agent@municipality.example"), policy.requestedUser("zelvo-demo:agent@municipality.example"));
        assertEquals(Optional.of("admin@municipality.example"), policy.requestedUser("zelvo-demo:ADMIN@municipality.example"));
        assertTrue(policy.requestedUser("zelvo-demo:someone@municipality.example").isEmpty());
    }

    @Test
    void ignores_ordinary_hints_and_missing_hints() {
        assertTrue(policy.requestedUser("agent@municipality.example").isEmpty());
        assertTrue(policy.requestedUser(null).isEmpty());
        assertTrue(policy.requestedUser("").isEmpty());
    }

    @Test
    void without_settings_nobody_is_a_demo_user() {
        DemoSignInPolicy empty = DemoSignInPolicy.fromConfig(Map.of());

        assertTrue(empty.requestedUser("zelvo-demo:agent@municipality.example").isEmpty());
        assertTrue(DemoSignInPolicy.fromConfig(null).requestedUser("zelvo-demo:").isEmpty());
    }
}
