import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Customer voice (module 3, task C6; GOV-2, GOV-3), against `make up`: a requester makes a
 * complaint from "Contact us"; the agent sets its root cause and resolves it; the requester
 * confirms the resolution and answers the happiness meter. The administrator allows anonymous
 * messages; a visitor makes a suggestion without signing in, keeps the tracking code and follows
 * it. The executive opens the customer voice report. Screens are checked for accessibility in
 * English and Arabic. Runs in its own project: it turns anonymous messages on.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("customer voice", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("a complaint is resolved, confirmed and rated, and a visitor follows an anonymous suggestion", async ({ page, browser }) => {
    const stamp = Date.now();
    const title = `Long wait at the counter ${stamp}`;

    // The requester makes a complaint from "Contact us".
    await signInWithPassword(page, devUser("requester"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/contact`);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expectAccessible(page);
    }
    await page.goto("/en/contact");
    await page.getByTestId("contact-complaint").click();
    await page.getByLabel("What is your complaint about?").fill(title);
    await page.getByLabel("What happened?").fill("I waited an hour although I had an appointment.");
    await expectAccessible(page);
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page).toHaveURL(/\/en\/my-voice\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expect(page.getByRole("progressbar")).toHaveAttribute("aria-valuetext", /Step 1 of 4/);
    const recordUrl = page.url();
    const recordId = recordUrl.split("/").pop()!;

    // The agent sets the root cause and resolves it.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto(`/en/customer-voice/${recordId}`);
    await expect(agent.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expectAccessible(agent);
    await agent.getByLabel("Root cause").selectOption({ label: "Delay in delivering the service" });
    await agent.getByRole("button", { name: "Save the details" }).click();
    await expect(agent.getByText("Delay in delivering the service").first()).toBeVisible();
    await agent.getByLabel("Change the state").selectOption({ label: "Resolved" });
    await agent.getByLabel("Resolution summary").fill("We added staff at peak hours.");
    await agent.getByRole("button", { name: "Apply" }).click();
    await expect(agent.getByText("Waiting for the customer's confirmation")).toBeVisible();
    await agentContext.close();

    // The requester confirms the resolution and rates it.
    await page.goto(recordUrl);
    await expect(page.getByTestId("confirm-resolution")).toBeVisible();
    await expectAccessible(page);
    await page.getByRole("button", { name: "Yes, it is resolved" }).click();
    await expect(page.getByText("Confirmed by the customer")).toBeVisible();
    await page.getByTestId("happiness-ticket").getByRole("radio", { name: /Happy$/ }).check({ force: true });
    await page.getByRole("button", { name: "Send my rating" }).click();
    await expect(page.getByText("Thank you for your rating")).toBeVisible();
    await page.goto("/ar/my-voice");
    await expect(page.getByTestId("voice-table").getByText(title)).toBeVisible();
    await expectAccessible(page);

    // The administrator allows anonymous messages.
    const adminContext = await browser.newContext();
    const admin = await adminContext.newPage();
    await signInWithPassword(admin, devUser("admin"));
    await completeSecondFactor(admin, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await admin.goto(`/${locale}/settings/customer-voice`);
      await expect(admin.getByTestId("voice-settings")).toBeVisible();
      await expectAccessible(admin);
    }
    await admin.goto("/en/settings/customer-voice");
    await admin.getByLabel(/Visitors may send complaints/).check();
    await admin.getByRole("button", { name: "Save" }).click();
    await expect(admin.getByRole("status").filter({ hasText: "Saved." })).toBeVisible();
    await adminContext.close();

    // A visitor makes a suggestion anonymously and follows it with the tracking code.
    const visitorContext = await browser.newContext();
    const visitor = await visitorContext.newPage();
    await visitor.goto("/en/contact/suggestion");
    await expect(visitor.getByText(/sent anonymously/)).toBeVisible();
    await visitor.getByLabel("What do you suggest?").fill(`Evening hours ${stamp}`);
    await visitor.getByRole("button", { name: "Send anonymously" }).click();
    const number = (await visitor.getByTestId("anonymous-number").textContent())!.trim();
    const code = (await visitor.getByTestId("anonymous-code").textContent())!.trim();
    await expectAccessible(visitor);
    await visitor.goto("/en/track");
    await visitor.getByLabel("Tracking reference").fill(number);
    await visitor.getByLabel("Private access code").fill(code);
    await visitor.getByRole("button", { name: "Show" }).click();
    await expect(visitor.getByTestId("tracked-record").getByText(`Evening hours ${stamp}`)).toBeVisible();
    await expect(visitor.getByText("Under review").first()).toBeVisible();
    await expectAccessible(visitor);
    await visitor.goto("/ar/track");
    await expectAccessible(visitor);
    await visitorContext.close();

    // The executive reads the report.
    const executiveContext = await browser.newContext();
    const executive = await executiveContext.newPage();
    await signInWithPassword(executive, devUser("executive"));
    await completeSecondFactor(executive, devUser("executive"));
    for (const locale of ["en", "ar"]) {
      await executive.goto(`/${locale}/customer-voice/report`);
      await expect(executive.getByTestId("report-happiness")).toBeVisible();
      await expectAccessible(executive);
    }
    await executiveContext.close();
  });
});
