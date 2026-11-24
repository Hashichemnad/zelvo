import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Workflow rules and approvals (task C3, modules 2 and 10), against `make up`: the administrator
 * opens the rule builder and adds a notify rule; the team lead writes a service whose requests
 * need the approver's approval and the administrator publishes it; the requester requests it and
 * waits; the approver approves it from My approvals; the request then reaches the service desk.
 * Runs in its own project: it adds a catalog, a service and a rule.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("workflow rules and approvals", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(300_000);

  test("a request waits for the approver, who approves it from My approvals", async ({ page, browser }) => {
    const stamp = Date.now();
    const catalogName = `E2E approvals ${stamp}`;
    const serviceName = `E2E projector ${stamp}`;
    const ruleName = `E2E tell the requester ${stamp}`;
    const code = `WF-${String(stamp).slice(-8)}`;

    // The administrator: the rule builder in both languages, and a rule that tells requesters.
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/workflows`);
      await expect(page.locator("main").getByRole("heading", { level: 2 }).first()).toBeVisible();
      await expectAccessible(page);
    }
    await page.goto("/en/settings/workflows");
    await page.getByRole("button", { name: "Add a rule" }).click();
    const form = page.getByTestId("workflow-rule-form");
    await form.getByRole("group", { name: "Name", exact: true }).getByLabel("In English").fill(ruleName);
    await form.getByRole("group", { name: "Name", exact: true }).getByLabel("In Arabic").fill(`إبلاغ تجريبي ${stamp}`);
    await form.getByLabel("Something happens").selectOption("StateChanged");
    await form.getByRole("button", { name: "Add a condition" }).click();
    await form.getByLabel("Field of condition 1").selectOption("state");
    await form.getByLabel("Values (choose one or more)").selectOption(["Resolved"]);
    await form.getByRole("group", { name: "Subject" }).getByLabel("In English").fill("{{ticket.number}} is done");
    await form.getByRole("group", { name: "Subject" }).getByLabel("In Arabic").fill("اكتمل {{ticket.number}}");
    await form.getByRole("group", { name: "Message" }).getByLabel("In English").fill("Dear {{recipient.name}}, it is done.");
    await form.getByRole("group", { name: "Message" }).getByLabel("In Arabic").fill("حضرة {{recipient.name}}، تم التنفيذ.");
    await expectAccessible(page);
    await form.getByRole("button", { name: "Save the rule" }).click();
    await expect(page.getByTestId("workflow-rules").getByText(ruleName, { exact: true })).toBeVisible();

    // A catalog the team lead edits.
    await page.goto("/en/settings/catalog");
    await page.getByRole("button", { name: "Add a catalog" }).click();
    const catalogForm = page.getByRole("form", { name: "Add a catalog" });
    await catalogForm.getByLabel("Key").fill(`wf-${stamp}`);
    await catalogForm.getByRole("group", { name: "Name" }).getByLabel("In English").fill(catalogName);
    await catalogForm.getByRole("group", { name: "Name" }).getByLabel("In Arabic").fill(`اعتمادات تجريبية ${stamp}`);
    await catalogForm.getByLabel("Find a person by name").fill("Layla");
    await catalogForm.getByRole("button", { name: "Find", exact: true }).click();
    await catalogForm.getByRole("button", { name: /Add Layla/ }).click();
    await catalogForm.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("catalog-catalogs").getByText(catalogName)).toBeVisible();

    // The team lead writes the service with an approval by the approver before assignment.
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
    await editor.getByRole("group", { name: "Name", exact: true }).getByLabel("In Arabic").fill(`جهاز عرض ${stamp}`);
    const before = editor.getByRole("group", { name: "Before assignment" });
    await before.getByRole("button", { name: "Add a level" }).click();
    const level = before.getByTestId("approval-level").first();
    await level.getByRole("group", { name: "Name of the level" }).getByLabel("In English").fill("Approver check");
    await level.getByRole("group", { name: "Name of the level" }).getByLabel("In Arabic").fill("تدقيق المعتمِد");
    await level.getByLabel("Approver 1").selectOption("User");
    await level.getByLabel("Find a person by name").fill("Omar");
    await level.getByRole("button", { name: "Find", exact: true }).click();
    await level.getByRole("button", { name: /Omar/ }).click();
    await expectAccessible(lead);
    await lead.getByRole("button", { name: "Save the draft" }).click();
    await expect(lead.getByRole("status").filter({ hasText: "Saved." }).first()).toBeVisible();
    await lead.getByRole("button", { name: "Send for review" }).click();
    await expect(editor.getByText("Under review").first()).toBeVisible();
    await leadContext.close();

    await page.goto("/en/settings/catalog");
    await page.getByRole("link", { name: code }).first().click();
    await page.getByRole("button", { name: "Approve and publish" }).click();
    await expect(page.getByTestId("service-editor").getByText("Active").first()).toBeVisible();

    // The requester requests it: it waits for approval.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto(`/en/catalog?q=${encodeURIComponent(serviceName)}`);
    await requester.getByRole("link", { name: serviceName }).click();
    await requester.getByRole("link", { name: "Request this service" }).click();
    await requester.getByRole("button", { name: "Send the request" }).click();
    await expect(requester).toHaveURL(/\/en\/my-requests\/[0-9a-f-]{36}$/);
    const requestUrl = requester.url();
    await expect(requester.getByRole("status").filter({ hasText: "waiting for approval" })).toBeVisible();
    await expect(requester.getByTestId("request-approvals")).toContainText("Approver check");
    await expectAccessible(requester);

    // The approver decides from My approvals, in both languages.
    const approverContext = await browser.newContext();
    const approver = await approverContext.newPage();
    await signInWithPassword(approver, devUser("approver"));
    await approver.goto("/ar/approvals");
    await expect(approver.getByTestId("approval-table").first()).toBeVisible();
    await expectAccessible(approver);
    await approver.goto("/en/approvals");
    await approver.getByTestId("approval-table").first().getByRole("link").first().click();
    await expect(approver.getByRole("heading", { level: 1 })).toBeVisible();
    await expectAccessible(approver);
    await approver.getByRole("radio", { name: "Approve" }).check();
    await approver.getByRole("button", { name: "Confirm my decision" }).click();
    await expect(approver.getByText("You approved this.")).toBeVisible();
    await approverContext.close();

    await requester.goto(requestUrl);
    await expect(requester.getByTestId("request-approvals")).toContainText("approved");
    await expect(requester.getByRole("status").filter({ hasText: "waiting for approval" })).toHaveCount(0);
    await requester.goto(requestUrl.replace("/en/", "/ar/"));
    await expectAccessible(requester);
    await requesterContext.close();
  });
});
