import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { devUser, signInWithPassword } from "../support/identity";

/**
 * Self-service portal (module 4, task D2), against `make up`: the requester raises an incident from
 * the home page in three clicks or fewer and well within a minute, finds it with "How can we help?",
 * and sees it in My tickets with its progress. Home and My tickets are checked for accessibility in
 * English and Arabic, on desktop and on a phone.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("self-service portal", () => {
  test.setTimeout(120_000);

  test("a requester raises a ticket from the home page in three clicks and follows it", async ({ page }) => {
    const title = `Portal printer jam ${Date.now()}`;
    await signInWithPassword(page, devUser("requester"));
    await page.goto("/en");
    await expect(page.getByRole("heading", { level: 1, name: "How can we help?" })).toBeVisible();
    await expectAccessible(page);

    const started = Date.now();
    await page.getByTestId("portal-report").click(); // 1
    await page.getByLabel("Title").fill(title);
    await page.getByRole("button", { name: "Report incident" }).click(); // 2
    await expect(page).toHaveURL(/\/en\/my-incidents\/[0-9a-f-]{36}$/);
    expect(Date.now() - started).toBeLessThan(60_000);

    // The new incident reaches the search index within a few seconds (the outbox is relayed about
    // every second), and the box searches when its text changes, so search again until it is found.
    await page.goto("/en");
    const search = page.getByRole("searchbox");
    const found = page.getByTestId("portal-results").getByRole("link", { name: title });
    await expect(async () => {
      await search.fill("");
      await search.fill(title);
      await expect(found).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });

    await page.goto("/en/my-tickets");
    const card = page.getByTestId("portal-ticket").filter({ hasText: title });
    await expect(card.getByRole("progressbar")).toHaveAttribute("aria-valuetext", /Step \d of 4/);
    await expectAccessible(page);
    await page.goto("/ar/my-tickets");
    await expectAccessible(page);
    await page.goto("/ar");
    await expectAccessible(page);
  });
});
