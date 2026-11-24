import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, notificationEmail, signInWithPassword } from "../support/identity";

/**
 * Notifications (task B5, module 10, LANG-6, GOV-8): a requester reports an incident; the worker
 * notifies them in Zelvo (the header's count, the notifications page) and by email through
 * Mailpit; they mark it read. An administrator previews Arabic wording in Settings → Notifications
 * and finds the email in the delivery log. Pages are checked for accessibility in English and
 * Arabic. Runs in its own Playwright project, after the other admin sign-ins; it changes no settings.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("notifications", () => {
  test.setTimeout(240_000);

  test("a requester is told about their incident in Zelvo and by email, and an administrator previews the Arabic wording", async ({ page, browser }) => {
    const title = `Scanner on floor 4 is offline ${Date.now()}`;

    await signInWithPassword(page, devUser("requester"));
    await page.goto("/en/my-incidents/new");
    await page.getByLabel("Title").fill(title);
    await page.getByRole("button", { name: "Report incident" }).click();
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    const number = (await page.getByText(/^INC-\d{6,}$/).textContent())!.trim();

    // The worker renders the notification within seconds; the page lists it with a link to the incident.
    const item = page.getByTestId("notification-list").getByRole("listitem").filter({ hasText: number });
    await expect(async () => {
      await page.goto("/en/notifications");
      await expect(item).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 60_000, intervals: [2_000] });
    await expect(item.getByRole("link", { name: `${number}: we have received your incident` })).toHaveAttribute("href", /\/en\/my-incidents\//);
    await expect(page.getByTestId("notification-bell")).toHaveAccessibleName(/unread notification/);
    await expectAccessible(page);

    const email = await notificationEmail(devUser("requester"), number);
    expect(email, "notification email in Mailpit").not.toBeNull();
    expect(email!.Text).toContain(title);
    expect(email!.HTML).toContain("Powered by Zelvo");

    await expect(async () => {
      if ((await item.getByRole("button", { name: "Mark as read" }).count()) > 0) await item.getByRole("button", { name: "Mark as read" }).click();
      await expect(item.getByText("Unread")).toHaveCount(0, { timeout: 2_000 });
    }).toPass({ timeout: 30_000 });

    await page.goto("/ar/notifications");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1, name: "الإشعارات" })).toBeVisible();
    await expectAccessible(page);
    await page.goto("/en/account");
    await expect(page.getByTestId("notification-preferences")).toBeVisible();
    await expectAccessible(page);

    // An administrator previews Arabic wording and finds the email in the delivery log.
    const adminContext = await browser.newContext();
    const admin = await adminContext.newPage();
    await signInWithPassword(admin, devUser("admin"));
    await completeSecondFactor(admin, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await admin.goto(`/${locale}/settings/notifications`);
      await expect(admin.getByTestId("notification-settings")).toBeVisible();
      await expectAccessible(admin);
    }

    await admin.goto("/en/settings/notifications");
    const received = admin.getByTestId("event-incident.received");
    await expect(async () => {
      if ((await received.getByRole("button", { name: "Edit wording" }).count()) > 0) await received.getByRole("button", { name: "Edit wording" }).click();
      await expect(received.getByRole("button", { name: "Preview in Arabic" })).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    await received.getByRole("button", { name: "Preview in Arabic" }).click();
    await expect(received.getByTestId("preview-subject")).toHaveText("بلاغ رقم INC-000123: تم استلام بلاغكم");
    await expectAccessible(admin);

    await admin.goto("/en/settings/notifications/deliveries");
    await expect(admin.getByTestId("delivery-log").getByRole("row").filter({ hasText: number }).first()).toBeVisible();
    await expectAccessible(admin);
    await adminContext.close();
  });
});
