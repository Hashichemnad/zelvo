import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Task G1 (module 18): the CMDB against the demo installation (`make demo`, then `make e2e-demo`),
 * whose government entity has business services, applications and servers with relationships.
 * An agent finds srv-web-01, sees what a fault of it affects, reads the relationship map in its
 * list view and moves through the map with the keyboard; an administrator opens Settings → CMDB.
 * Nothing is changed. Runs after the demo tests (project demo-cmdb).
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const tenant = process.env.E2E_DEMO_TENANT ?? "demo-municipality";

async function signInAs(page: Page, persona: "agent" | "admin", baseURL: string) {
  const response = await page.request.post("/auth/demo", { form: { tenant, locale: "en", persona }, headers: { Origin: baseURL } });
  expect(response.ok(), `demo sign-in as ${persona}`).toBeTruthy();
}

async function expectAccessible(page: Page) {
  // The map's canvas is drawn by Cytoscape; its frame is labelled and keyboard-operable.
  expect((await new AxeBuilder({ page }).withTags(wcag).analyze()).violations).toEqual([]);
}

test.describe.configure({ mode: "serial" });

test.describe("CMDB", () => {
  test.skip(tenant !== "demo-municipality", "The journey uses the government entity's sample items.");

  test("an agent finds a server, its impact and its relationships on the map and in the list", async ({ page, baseURL }) => {
    await signInAs(page, "agent", baseURL ?? "http://localhost:3000");
    await page.goto("/en");
    await page.getByTestId("nav-cmdb").click();
    await expect(page).toHaveURL(/\/en\/cmdb$/);

    await page.getByLabel("Number or name").fill("srv-web-01");
    await page.getByRole("button", { name: "Show", exact: true }).click();
    await page.getByTestId("ci-table").getByRole("link").first().click();
    await expect(page.getByRole("heading", { level: 1, name: "srv-web-01" })).toBeVisible();
    await expect(page.getByTestId("ci-attributes")).toContainText("10.10.1.11");

    // What a fault affects: the website on it and the portal that depends on the website.
    const impact = page.getByTestId("ci-impact");
    await expect(impact.getByRole("link", { name: /E-services portal/ })).toBeVisible();
    await expect(impact.getByRole("link", { name: /E-services website/ })).toBeVisible();

    // The map, then the keyboard: the frame takes arrow keys and the selection is announced.
    const canvas = page.getByTestId("ci-map-canvas");
    await expect(canvas).toBeVisible();
    await canvas.focus();
    await page.keyboard.press("Home");
    await expect(page.getByTestId("ci-map-announcement")).toContainText("Selected: CI-");
    const before = await page.getByTestId("ci-map-announcement").textContent();
    for (const key of ["ArrowRight", "ArrowDown", "ArrowLeft", "ArrowUp"]) {
      await page.keyboard.press(key);
      if ((await page.getByTestId("ci-map-announcement").textContent()) !== before) break;
    }
    await expect(page.getByTestId("ci-map-announcement")).not.toHaveText(before ?? "");
    await expectAccessible(page);

    // The list view says the same.
    await page.getByRole("button", { name: "List", exact: true }).click();
    const lines = page.getByTestId("ci-map-lines");
    await expect(lines).toContainText(/E-services website — Runs on → CI-\d+ srv-web-01/);
    await expect(lines).toContainText(/srv-web-01 — Connects to →/);
    await expectAccessible(page);

    // In Arabic, right to left, with the list view remembered on the device.
    await page.goto(page.url().replace("/en/", "/ar/"));
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByTestId("ci-map-list")).toBeVisible();
    await expect(page.getByTestId("ci-map-lines")).toContainText("يعمل على");
    await expectAccessible(page);
  });

  test("an administrator sees the classes, relationship types and number prefix in Settings → CMDB", async ({ page, baseURL }) => {
    await signInAs(page, "admin", baseURL ?? "http://localhost:3000");
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/cmdb`);
      await expect(page.getByTestId("cmdb-classes").getByTestId("cmdb-class").first()).toBeVisible();
      await expect(page.getByTestId("cmdb-types").getByTestId("cmdb-type")).toHaveCount(5);
      await expectAccessible(page);
    }
    await page.goto("/en/cmdb/new");
    await expect(page.getByRole("heading", { level: 1, name: "New configuration item" })).toBeVisible();
    await expectAccessible(page);
  });
});
