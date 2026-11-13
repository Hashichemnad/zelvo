import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import {
  completeSecondFactor,
  deleteEmailsTo,
  devUser,
  emailedCode,
  signInWithPassword,
  startSignIn,
} from "../support/identity";

/**
 * Sign-in journeys (task A2): password, email one-time code, the second factor for staff,
 * sign-out and CSRF protection. Development users are shared, so these run one after another
 * on the desktop project only.
 */
test.describe("identity and access", () => {
  test.describe.configure({ mode: "serial" });
  test.skip(({ isMobile }) => isMobile, "Shared development users: desktop project only");

  test("a requester signs in with a password, sees the account page and signs out", async ({ page }) => {
    await signInWithPassword(page, devUser("requester"));

    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Your account");
    await expect(page.getByTestId("roles")).toHaveText("Requester");
    await expect(page.getByTestId("mfa-status")).toHaveText("Not used for this sign-in");
    const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(axe.violations).toEqual([]);

    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(page).toHaveURL(/\/en$/);
    await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
    expect((await page.request.get("/bff/api/v1/me")).status()).toBe(401);
  });

  test("an approver signs in with a code sent by email, in Arabic", async ({ page }) => {
    const address = devUser("approver");
    await deleteEmailsTo(address);
    await startSignIn(page, "ar");

    await page.locator("#try-another-way").click();
    await page.getByText("رمز عبر البريد الإلكتروني").first().click();
    await page.locator("#email").fill(address);
    await page.locator("#zelvo-send-code").click();
    const code = await emailedCode(address);
    expect(code).not.toBeNull();
    await page.locator("#code").fill(code!);
    await page.locator("#zelvo-sign-in").click();

    await expect(page).toHaveURL(/\/ar\/account$/);
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("حسابك");
    await expect(page.getByTestId("roles")).toHaveText("المعتمِد");
    const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(axe.violations).toEqual([]);
  });

  test("an unknown address gets no code and cannot sign in", async ({ page }) => {
    const address = `nobody-${Date.now()}@example.test`;
    await startSignIn(page, "en");

    await page.locator("#try-another-way").click();
    await page.getByText("Email code").first().click();
    await page.locator("#email").fill(address);
    await page.locator("#zelvo-send-code").click();
    // The page looks the same as for a known address, so it does not reveal who has an account.
    await expect(page.getByText(`If ${address} can sign in here`)).toBeVisible();
    await page.locator("#code").fill("123456");
    await page.locator("#zelvo-sign-in").click();

    await expect(page.getByText("The code is not correct.")).toBeVisible();
    expect(await emailedCode(address, 2_000)).toBeNull();
  });

  test("an agent must add a second factor before the agent role applies", async ({ page }) => {
    await signInWithPassword(page, devUser("agent"));
    await completeSecondFactor(page, devUser("agent"));

    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByTestId("mfa-status")).toHaveText("Used for this sign-in");
    await expect(page.getByTestId("roles")).toHaveText("Agent");
    const me = await (await page.request.get("/bff/api/v1/me")).json();
    expect(me.mfa).toEqual({ satisfied: true, required: false });
    expect(me.permissions).toContain("users.read");
  });

  test("changes through the API proxy need the session's CSRF token", async ({ page }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/en\/account$/);

    const result = await page.evaluate(async () => {
      const csrf = (document.querySelector("input[name=csrf]") as HTMLInputElement).value;
      const body = JSON.stringify({ emailCodeSignIn: "KnownUsers" });
      const put = (headers: Record<string, string>) =>
        fetch("/bff/api/v1/identity/settings", { method: "PUT", headers: { "content-type": "application/json", ...headers }, body });
      return { without: (await put({})).status, wrong: (await put({ "x-csrf-token": "wrong" })).status, right: (await put({ "x-csrf-token": csrf })).status };
    });

    expect(result).toEqual({ without: 403, wrong: 403, right: 200 });
  });

  test("a sign-in that did not start here is refused with an explanation", async ({ page }) => {
    await page.goto("/auth/callback?state=forged&code=forged");

    await expect(page).toHaveURL(/\/en\/sign-in-problem\?reason=expired$/);
    await expect(page.getByText(/started in another window. Please sign in again./)).toHaveAttribute("role", "alert");
    const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(axe.violations).toEqual([]);
  });
});
