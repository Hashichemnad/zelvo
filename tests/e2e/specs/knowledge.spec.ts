import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Knowledge base (task D1, module 7), against `make up`: an agent writes a bilingual article and
 * sends it for review; a team lead (someone else: four-eyes rule) approves it; a visitor who is
 * not signed in finds it in Help, reads it in English and Arabic and answers "Was this helpful?".
 * Accessibility is checked on every screen. Runs in its own project: it signs in as the agent and
 * the lead, whose authenticator codes other specs use too.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("knowledge base", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("an agent writes an article, a lead approves it and a visitor reads it", async ({ page, browser }) => {
    const title = `Connect to the guest Wi-Fi ${Date.now()}`;

    // The agent writes the article in both languages and sends it for review.
    await signInWithPassword(page, devUser("agent"));
    await completeSecondFactor(page, devUser("agent"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);
    await page.goto("/en/knowledge");
    // The list is always shown, with "No articles match." when it is empty.
    await expect(page.getByTestId("knowledge-list")).toBeVisible();
    await expectAccessible(page);
    await page.getByRole("link", { name: "New article" }).click();
    const editor = page.getByTestId("article-editor");
    await editor.getByLabel("Title").fill(title);
    await editor.getByLabel("Summary").fill("The network name and how to join it.");
    await editor.getByLabel("Article", { exact: true }).fill("## Steps\n\n1. Choose **Guest** in the Wi-Fi list.\n2. Accept the terms.");
    await editor.getByRole("tab", { name: /Arabic/ }).click();
    await editor.getByLabel("Title").fill("الاتصال بشبكة الضيوف اللاسلكية");
    await editor.getByLabel("Article", { exact: true }).fill("## الخطوات\n\n1. اختر **الضيوف** من قائمة الشبكات.");
    await editor.getByRole("radio", { name: /External/ }).check();
    await expectAccessible(page);
    await editor.getByRole("button", { name: "Save as draft" }).click();
    await expect(page.getByTestId("article-workspace")).toBeVisible();
    await expect(page.getByTestId("article-state")).toHaveText("Draft");
    const articleUrl = page.url();
    await page.getByRole("button", { name: "Send for review" }).click();
    await expect(page.getByTestId("article-state")).toHaveText("In review");
    await expect(page.getByText("Waiting for another publisher to review it.")).toBeVisible();
    await expectAccessible(page);

    // The lead reviews it.
    const leadContext = await browser.newContext();
    const lead = await leadContext.newPage();
    await signInWithPassword(lead, devUser("lead"));
    await completeSecondFactor(lead, devUser("lead"));
    await expect(lead).toHaveURL(/\/(en|ar)\/account$/);
    await lead.goto("/en/knowledge/review");
    const item = lead.getByTestId("review-item").filter({ hasText: title });
    await expect(item).toHaveCount(1);
    await expectAccessible(lead);
    await item.getByRole("link").click();
    await expect(lead.getByTestId("review-box")).toBeVisible();
    await expectAccessible(lead);
    await lead.getByRole("button", { name: "Approve and publish" }).click();
    await expect(lead.getByTestId("article-state")).toHaveText("Published");
    await leadContext.close();

    // A visitor finds and reads it, and answers "Was this helpful?".
    const visitorContext = await browser.newContext();
    const visitor = await visitorContext.newPage();
    await visitor.goto("/en/help");
    await expectAccessible(visitor);
    await visitor.getByLabel("Search the help articles").fill("guest Wi-Fi");
    await visitor.getByRole("button", { name: "Search" }).click();
    await visitor.getByRole("link", { name: title }).click();
    const article = visitor.getByTestId("published-article");
    await expect(article.getByRole("heading", { level: 1 })).toHaveText(title);
    await expect(article.getByRole("listitem").first()).toContainText("Guest");
    await expectAccessible(visitor);
    await article.getByRole("button", { name: "Yes" }).click();
    await expect(article.getByText("Thank you for your answer.")).toBeVisible();

    const arabic = visitor.url().replace("/en/", "/ar/");
    await visitor.goto(arabic);
    await expect(visitor.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(visitor.getByTestId("published-article").getByRole("heading", { level: 1 })).toHaveText("الاتصال بشبكة الضيوف اللاسلكية");
    await expect(visitor.getByRole("button", { name: "نعم" })).toHaveAttribute("aria-pressed", "true");
    await expectAccessible(visitor);
    await visitorContext.close();

    // The agent sees the view and the answer.
    await page.goto(articleUrl);
    await expect(page.getByTestId("article-state")).toHaveText("Published");
    await expect(page.getByText(/1 yes/)).toBeVisible();
  });
});
