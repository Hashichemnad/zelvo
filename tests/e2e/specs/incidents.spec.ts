import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Incidents (task B1, module 1): a requester reports an incident and attaches files, which are
 * scanned for malware before anyone can open them; an agent assigns, works and resolves it; the
 * requester reopens it. Accessibility is checked in English and Arabic.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

// The standard antivirus test file: harmless, and every scanner reports it.
const eicar = String.raw`X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*`;

/** Reloads until the file shows `status` (the worker scans within seconds once ClamAV is ready). */
async function waitForScan(page: Page, fileName: string, status: string) {
  await expect(async () => {
    await page.reload();
    await expect(page.getByTestId("attachments").getByRole("listitem").filter({ hasText: fileName })).toContainText(status, { timeout: 2_000 });
  }).toPass({ timeout: 300_000, intervals: [3_000] });
}

test.describe("incidents", () => {
  // Sign-ins with a second factor share one authenticator secret per user; run on one project.
  test.beforeEach(() => test.skip(test.info().project.name !== "desktop", "Runs once, on the desktop project."));
  test.setTimeout(420_000);

  test("a requester reports an incident, an agent resolves it and the requester reopens it", async ({ page, browser }) => {
    const title = `Printer on floor 2 does not print ${Date.now()}`;

    // The requester reports the incident from "My incidents".
    await signInWithPassword(page, devUser("requester"));
    await page.goto("/en/my-incidents");
    await expectAccessible(page);
    await page.getByRole("link", { name: "Report an incident" }).click();
    await page.getByLabel("Title").fill(title);
    await page.getByLabel("What happened?").fill("A paper jam message shows every time.");
    await expectAccessible(page);
    await page.getByRole("button", { name: "Report incident" }).click();
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expect(page.getByText(/^INC-\d{6,}$/)).toBeVisible();
    const incidentUrl = new URL(page.url());
    const id = incidentUrl.pathname.split("/").pop()!;

    // A clean file becomes downloadable after the scan; the test virus file is rejected.
    const attachments = page.getByTestId("attachments");
    await attachments.getByLabel("File to attach").setInputFiles({ name: "error-log.txt", mimeType: "text/plain", buffer: Buffer.from("Error 0x42 at 08:15\n") });
    await attachments.getByRole("button", { name: "Attach file" }).click();
    await expect(attachments.getByRole("status")).toContainText("Attached");
    await attachments.getByLabel("File to attach").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from(eicar) });
    await attachments.getByRole("button", { name: "Attach file" }).click();
    await expect(attachments.getByRole("listitem").filter({ hasText: "notes.txt" })).toContainText("Being checked for malware");

    await waitForScan(page, "error-log.txt", "Checked");
    await waitForScan(page, "notes.txt", "Rejected: malware found");
    const download = page.waitForEvent("download");
    await attachments.getByRole("link", { name: "Download error-log.txt" }).click();
    expect((await download).suggestedFilename()).toBe("error-log.txt");
    await expect(attachments.getByRole("link", { name: "Download notes.txt" })).toHaveCount(0);
    await expectAccessible(page);

    // The same page in Arabic, right to left.
    await page.goto(`/ar/my-incidents/${id}`);
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByText("جديد", { exact: true })).toBeVisible();
    await expectAccessible(page);

    // An agent assigns the incident, starts work and resolves it.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto("/en/incidents?open=true");
    await expectAccessible(agent);
    await agent.goto(`/en/incidents/${id}`);
    const work = agent.getByTestId("incident-work");
    const assignee = work.getByLabel("Assignee");
    const someone = await assignee.locator("option").nth(1).getAttribute("value");
    await assignee.selectOption(someone!);
    await work.getByRole("button", { name: "Assign" }).click();
    await expect(agent.getByText("Assigned", { exact: true }).first()).toBeVisible();
    await work.getByRole("button", { name: "Start work" }).click();
    await expect(agent.getByText("In progress", { exact: true }).first()).toBeVisible();
    await work.getByRole("button", { name: "Resolve" }).click();
    await work.getByLabel("How was it resolved?").fill("Cleared the paper path and replaced the roller.");
    await work.getByRole("button", { name: "Resolve" }).last().click();
    await expect(agent.getByText("Cleared the paper path and replaced the roller.").first()).toBeVisible();
    await expect(agent.getByRole("heading", { name: "History" })).toBeVisible();
    await expect(agent.getByText(/State: In progress → Resolved/)).toBeVisible();
    await expectAccessible(agent);
    await agentContext.close();

    // The requester sees the resolution and reopens the incident.
    await page.goto(`/en/my-incidents/${id}`);
    await expect(page.getByText("Cleared the paper path and replaced the roller.")).toBeVisible();
    await page.getByRole("button", { name: "Reopen incident" }).click();
    await expect(page.getByText("Reopened", { exact: true }).first()).toBeVisible();
  });

  test("requesters cannot open the staff incident list or other people's incidents", async ({ page }) => {
    await signInWithPassword(page, devUser("requester"));
    await page.goto("/en/incidents");
    await expect(page.getByRole("alert")).toContainText("Your role does not include working on incidents.");
    await page.goto("/en/my-incidents/0195f000-0000-7000-8000-000000000999");
    await expect(page.getByRole("alert")).toContainText("does not exist, or you cannot see it");
  });
});
