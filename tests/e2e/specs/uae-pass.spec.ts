import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, deleteEmailsTo, devPassword, devUser, mailpitUrl, readEnv, signInWithPassword, startSignIn } from "../support/identity";

/**
 * Sign-in with UAE PASS (task D3, GOV-1) against the UAE PASS mock in Docker Compose
 * (deploy/compose/uaepass-mock). Runs in its own project after all other tests, because it
 * turns UAE PASS on for the tenant; it turns it off again at the end.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const mockUrl = process.env.UAEPASS_MOCK_URL ?? "http://localhost:8095";

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

/** Saves the UAE PASS settings through the web app's API proxy, as the signed-in admin. */
async function setUaePass(page: Page, body: Record<string, unknown>) {
  const status = await page.evaluate(async (values) => {
    const csrf = (document.querySelector("input[name=csrf]") as HTMLInputElement).value;
    const response = await fetch("/bff/api/v1/identity/uae-pass", {
      method: "PUT",
      headers: { "content-type": "application/json", "x-csrf-token": csrf },
      body: JSON.stringify(values),
    });
    return response.status;
  }, body);
  expect(status).toBe(200);
}

/** The mock set-up with the approved default rules. */
function mockSettings() {
  return {
    enabled: true,
    environment: "Mock",
    clientId: "zelvo-mock",
    acceptAsSecondFactor: false,
    requesterMinimumLevel: "SOP1",
    createRequesters: true,
  };
}

async function signInAsAdmin(page: Page) {
  await signInWithPassword(page, devUser("admin"));
  await completeSecondFactor(page, devUser("admin"));
  await expect(page).toHaveURL(/\/(en|ar)\/account$/);
}

/** From Keycloak's sign-in page: UAE PASS, then the mock person with this email. */
async function continueWithUaePass(page: Page, email: string) {
  await page.getByRole("link", { name: "UAE PASS" }).click();
  await expect(page).toHaveURL(new RegExp(`^${mockUrl}/idshub/authorize`));
  await page.locator(`button[data-email="${email}"]`).click();
}

/** The newest link Keycloak sent to an address (account linking email), or null. */
async function emailedLink(address: string, timeoutMs = 10_000): Promise<string | null> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const search = await fetch(`${mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`);
    const result = (await search.json()) as { messages?: { ID: string }[] };
    if (result.messages?.length) {
      const message = (await (await fetch(`${mailpitUrl}/api/v1/message/${result.messages[0].ID}`)).json()) as { Text: string };
      return message.Text.match(/https?:\/\/\S+login-actions\/action-token\S+/)?.[0] ?? null;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return null;
}

test.describe("UAE PASS sign-in", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test("an admin turns UAE PASS on in Settings with the client ID and secret (GOV-1)", async ({ page }) => {
    await signInAsAdmin(page);
    await page.goto("/en/settings");
    const menu = page.getByRole("navigation", { name: "Settings sections" });
    await menu.getByRole("link", { name: "Sign-in" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/sign-in$/);
    await expectAccessible(page);
    await page.getByRole("main").getByRole("link", { name: "UAE PASS" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/sign-in\/uae-pass$/);
    // The approved defaults (GOV-1, 2026-11-18).
    await expect(page.getByLabel("Accept UAE PASS as the second factor for staff")).not.toBeChecked();
    await expect(page.getByLabel("Minimum UAE PASS account level for requesters")).toHaveValue("SOP1");
    await expect(page.getByLabel("Create requester accounts automatically on first UAE PASS sign-in")).toBeChecked();
    await expect(page.getByTestId("uae-pass-redirect-uri")).toHaveText(/\/realms\/zelvo-[a-z0-9-]+\/broker\/uaepass\/endpoint$/);
    await expectAccessible(page);

    await page.getByLabel("Offer UAE PASS on the sign-in page").check();
    await page.getByLabel("UAE PASS environment").selectOption("Mock");
    await page.getByLabel("Client ID").fill("zelvo-mock");
    await page.getByLabel("Client secret").fill(readEnv("UAEPASS_MOCK_CLIENT_SECRET") ?? "");
    await page.getByRole("button", { name: "Save" }).click();
    await expect(page.getByRole("status")).toHaveText("The UAE PASS settings are saved.");
    await expect(page.getByLabel("Client secret")).toHaveValue("");
    await expect(page.getByText(/A client secret is saved/)).toBeVisible();

    // The secret never comes back from the API.
    const settings = await (await page.request.get("/bff/api/v1/identity/uae-pass")).text();
    expect(settings).not.toContain(readEnv("UAEPASS_MOCK_CLIENT_SECRET"));

    await page.goto("/ar/settings/sign-in/uae-pass");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByLabel("معرّف العميل")).toHaveValue("zelvo-mock");
    await expectAccessible(page);
  });

  test("a citizen signs in with UAE PASS from the Arabic site, gets a requester account, and signing out ends UAE PASS too", async ({ page }) => {
    await startSignIn(page, "ar");
    await page.getByRole("link", { name: "UAE PASS" }).click();
    // Keycloak passes the page's language on (ui_locales), as UAE PASS expects.
    await expect(page).toHaveURL(/ui_locales=ar/);
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("تسجيل الدخول باستخدام الهوية الرقمية");
    await page.locator('button[data-email="mariam.citizen@uaepass.test"]').click();

    // Signed in, the language order applies (profile, then tenant default: English here).
    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByTestId("roles")).toHaveText("Requester");
    await expect(page.getByText("mariam.citizen@uaepass.test")).toBeVisible();

    const uaePassSignOut = page.waitForRequest((request) => request.url().startsWith(`${mockUrl}/idshub/logout`));
    await page.getByRole("button", { name: "Sign out" }).click();
    const request = await uaePassSignOut;
    expect(new URL(request.url()).searchParams.get("redirect_uri")).toMatch(/\/en$/);
    await expect(page).toHaveURL(/\/en$/);
    expect((await page.request.get("/bff/api/v1/me")).status()).toBe(401);
  });

  test("an existing requester links UAE PASS by confirming a link sent to their email", async ({ page }) => {
    const address = devUser("requester");
    await deleteEmailsTo(address);
    await startSignIn(page, "en");
    await continueWithUaePass(page, address);

    // The first time, Keycloak asks the owner of the existing account to confirm by email;
    // after that, UAE PASS signs them straight in.
    const linked = page.waitForURL(/\/en\/account$/, { timeout: 5_000 }).then(() => true, () => false);
    if (!(await linked)) {
      await expect(page.getByText(/email/i).first()).toBeVisible();
      const link = await emailedLink(address);
      expect(link, "account linking email").not.toBeNull();
      await page.goto(link!);
    }

    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByText(address)).toBeVisible();
    await expect(page.getByTestId("roles")).toHaveText("Requester");
  });

  test("an agent signs in with UAE PASS and still needs the authenticator app", async ({ page }) => {
    const address = devUser("agent");
    await deleteEmailsTo(address);
    await startSignIn(page, "en");
    await continueWithUaePass(page, address);

    // First time: Keycloak offers the email link; the agent links with the account's password
    // instead ("Try Another Way"). Later sign-ins go straight to the second factor.
    const tryAnotherWay = page.getByRole("button", { name: "Try Another Way" }).or(page.getByRole("link", { name: "Try Another Way" }));
    const secondFactor = page.getByText(/Unable to scan/i).or(page.locator("#otp"));
    await expect(tryAnotherWay.or(secondFactor).first()).toBeVisible();
    if (await tryAnotherWay.first().isVisible()) {
      await tryAnotherWay.first().click();
      await page.getByText("Username and password").click();
      await page.locator("#password").fill(devPassword());
      await page.locator("#kc-login").click();
    }

    // The agent role needs a second factor, so the web app asks again and Keycloak wants only
    // the authenticator app: UAE PASS counts as the first factor, no password is asked again.
    await expect(page.locator("#password")).toHaveCount(0);
    await completeSecondFactor(page, address);
    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByTestId("roles")).toHaveText("Agent");
    await expect(page.getByTestId("mfa-status")).not.toHaveText("Not used for this sign-in");
  });

  test("a team lead is matched by the Emirates ID an administrator recorded, without proving the email", async ({ page, browser }) => {
    // The admin records the Emirates ID of the team lead's UAE PASS account (a different email).
    await signInAsAdmin(page);
    const users = (await (await page.request.get("/bff/api/v1/users?pageSize=100")).json()) as { items: { id: string; email?: string }[] };
    const lead = users.items.find((u) => u.email === devUser("lead"));
    expect(lead, "development team lead").toBeDefined();
    const csrf = await page.locator("input[name=csrf]").first().inputValue();
    const saved = await page.request.patch(`/bff/api/v1/users/${lead!.id}`, {
      data: { emiratesId: "784-0000-0000000-6" },
      headers: { "x-csrf-token": csrf, origin: new URL(page.url()).origin },
    });
    expect(saved.status()).toBe(200);
    expect(((await saved.json()) as { emiratesId?: string }).emiratesId).toBe("784000000000006");

    const context = await browser.newContext();
    const leadPage = await context.newPage();
    await startSignIn(leadPage, "en");
    await continueWithUaePass(leadPage, "layla.personal@uaepass.test");
    // No email link or password: Keycloak links by Emirates ID and asks only for the second factor.
    await completeSecondFactor(leadPage, devUser("lead"));
    await expect(leadPage).toHaveURL(/\/en\/account$/);
    await expect(leadPage.getByText(devUser("lead"))).toBeVisible();
    await expect(leadPage.getByTestId("roles")).toHaveText("Team lead");
    await context.close();
  });

  test("when the tenant accepts it, a verified UAE PASS sign-in is the second factor for staff", async ({ page, browser }) => {
    await signInAsAdmin(page);
    await setUaePass(page, { ...mockSettings(), acceptAsSecondFactor: true });
    try {
      const context = await browser.newContext();
      const agent = await context.newPage();
      await startSignIn(agent, "en");
      await continueWithUaePass(agent, devUser("agent"));
      await expect(agent).toHaveURL(/\/en\/account$/);
      await expect(agent.getByTestId("roles")).toHaveText("Agent");
      await expect(agent.getByTestId("mfa-status")).toHaveText("Used for this sign-in");
      await context.close();
    } finally {
      await setUaePass(page, { ...mockSettings(), acceptAsSecondFactor: false });
    }
  });

  test("with automatic accounts off only known people get in, and new requesters need the minimum level", async ({ page, browser }) => {
    await signInAsAdmin(page);
    try {
      await setUaePass(page, { ...mockSettings(), createRequesters: false });
      const first = await browser.newContext();
      const unknown = await first.newPage();
      await startSignIn(unknown, "en");
      await continueWithUaePass(unknown, "ravi.resident@uaepass.test");
      await expect(unknown.getByText("There is no account for your UAE PASS yet.", { exact: false })).toBeVisible();
      await first.close();

      await setUaePass(page, { ...mockSettings(), createRequesters: true, requesterMinimumLevel: "SOP2" });
      const second = await browser.newContext();
      const basic = await second.newPage();
      await startSignIn(basic, "ar");
      await basic.getByRole("link", { name: "UAE PASS" }).click();
      await basic.locator('button[data-email="sara.basic@uaepass.test"]').click();
      await expect(basic.getByText("يجب أن يكون حسابك في الهوية الرقمية موثّقًا", { exact: false })).toBeVisible();
      await second.close();
    } finally {
      await setUaePass(page, mockSettings());
    }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    testInfo.setTimeout(90_000);
    const page = await browser.newPage();
    await signInAsAdmin(page);
    await setUaePass(page, { ...mockSettings(), enabled: false });
    await page.close();
  });
});
