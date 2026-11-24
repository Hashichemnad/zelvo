import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Service catalog (task C2, module 2), against `make up`: the administrator adds a catalog with
 * the team lead as its editor; the lead writes a service from a ready-made template and sends it
 * for review; the administrator (a publisher who did not write it) approves it; a requester finds
 * it in the catalog, requests it and sees the request in "My requests". The new screens are
 * checked for accessibility in English and Arabic. Runs in its own project: it adds a catalog.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("service catalog", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("an editor writes a service, another publisher approves it and a requester requests it", async ({ page, browser }) => {
    const stamp = Date.now();
    const catalogKey = `e2e-${stamp}`;
    const catalogName = `E2E services ${stamp}`;
    const serviceName = `E2E parking permit ${stamp}`;
    const code = `E2E-${String(stamp).slice(-8)}`;

    // The administrator: Settings → Catalog in both languages, and a catalog for the lead.
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/catalog`);
      await expect(page.locator("main").getByRole("heading", { level: 2 }).first()).toBeVisible();
      await expectAccessible(page);
    }
    await page.goto("/en/settings/catalog");
    await page.getByRole("button", { name: "Add a catalog" }).click();
    const catalogForm = page.getByRole("form", { name: "Add a catalog" });
    await catalogForm.getByLabel("Key").fill(catalogKey);
    await catalogForm.getByRole("group", { name: "Name" }).getByLabel("In English").fill(catalogName);
    await catalogForm.getByRole("group", { name: "Name" }).getByLabel("In Arabic").fill(`خدمات تجريبية ${stamp}`);
    await catalogForm.getByLabel("Find a person by name").fill("Layla");
    await catalogForm.getByRole("button", { name: "Find", exact: true }).click();
    await catalogForm.getByRole("button", { name: /Add Layla/ }).click();
    await catalogForm.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("catalog-catalogs").getByText(catalogName)).toBeVisible();

    // The team lead writes the service from a ready-made template and sends it for review.
    const leadContext = await browser.newContext();
    const lead = await leadContext.newPage();
    await signInWithPassword(lead, devUser("lead"));
    await completeSecondFactor(lead, devUser("lead"));
    await lead.goto("/en/settings/catalog");
    const create = lead.getByRole("form", { name: "Create a service" });
    await create.getByLabel("Code").fill(code);
    await create.getByLabel("Start from").selectOption({ label: "General service request (ready-made)" });
    await create.getByLabel("Catalog").selectOption({ label: catalogName });
    await create.getByRole("button", { name: "Create" }).click();
    await expect(lead).toHaveURL(/\/en\/settings\/catalog\/services\/[0-9a-f-]{36}$/);
    const editor = lead.getByTestId("service-editor");
    await editor.getByRole("group", { name: "Name", exact: true }).getByLabel("In English").fill(serviceName);
    await editor.getByRole("group", { name: "Name", exact: true }).getByLabel("In Arabic").fill(`تصريح مواقف ${stamp}`);
    await expectAccessible(lead);
    await lead.getByRole("button", { name: "Save the draft" }).click();
    await expect(lead.getByRole("status").filter({ hasText: "Saved." }).first()).toBeVisible();
    await lead.getByRole("button", { name: "Send for review" }).click();
    await expect(editor.getByText("Under review").first()).toBeVisible();
    // The lead cannot approve their own service.
    await expect(lead.getByRole("button", { name: "Approve and publish" })).toHaveCount(0);
    await leadContext.close();

    // The administrator approves it from the review queue.
    await page.goto("/en/settings/catalog");
    await page.getByRole("link", { name: code }).first().click();
    await page.getByRole("button", { name: "Approve and publish" }).click();
    await expect(page.getByTestId("service-editor").getByText("Active").first()).toBeVisible();

    // A requester finds it, reads the card and requests it.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto(`/en/catalog?q=${encodeURIComponent(serviceName)}`);
    await expectAccessible(requester);
    await requester.getByRole("link", { name: serviceName }).click();
    await expect(requester.getByRole("heading", { level: 1, name: serviceName })).toBeVisible();
    await expectAccessible(requester);
    await requester.getByRole("link", { name: "Request this service" }).click();
    await requester.getByLabel("Details (optional)").fill("For the visitors' car park.");
    await expectAccessible(requester);
    await requester.getByRole("button", { name: "Send the request" }).click();
    await expect(requester).toHaveURL(/\/en\/my-requests\/[0-9a-f-]{36}$/);
    await expect(requester.getByRole("heading", { level: 1, name: serviceName })).toBeVisible();
    await expect(requester.getByRole("progressbar")).toBeVisible();
    await expectAccessible(requester);

    await requester.goto("/en/my-requests");
    await expect(requester.getByTestId("request-table").getByText(serviceName)).toBeVisible();
    await expectAccessible(requester);
    await requester.goto("/ar/my-requests");
    await expect(requester.getByTestId("request-table").getByText(`تصريح مواقف ${stamp}`)).toBeVisible();
    await expectAccessible(requester);
    await requester.goto(`/ar/catalog?q=${encodeURIComponent(`تصريح مواقف ${stamp}`)}`);
    await expect(requester.getByRole("link", { name: `تصريح مواقف ${stamp}` })).toBeVisible();
    await expectAccessible(requester);
    await requesterContext.close();
  });
});
