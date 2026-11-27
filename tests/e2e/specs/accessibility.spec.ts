import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Task F7: the WCAG 2.2 AA audit of every page, in English and Arabic, against the demo
 * installation (`make demo`, then `make e2e-demo`), whose sample data fills every module. Each
 * persona signs in with the demo sign-in and visits the pages its role uses; detail pages are
 * found from their list pages. axe checks each page with the WCAG 2.0, 2.1 and 2.2 A and AA rules;
 * the test also checks the language and direction of every page. All findings of a persona are
 * collected (tests/e2e/accessibility-report/<persona>.json) before the test fails, so one run shows
 * everything. Keyboard and screen-reader checks that axe cannot make are in
 * docs/accessibility/wcag-2.2-audit.md.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const tenant = process.env.E2E_DEMO_TENANT ?? "demo-municipality";
/**
 * The reflow tests sign in to the other sample organisation: they visit the same pages as the
 * WCAG audit and may run at the same time on the other worker, and the API limits each person
 * (600 calls a minute), so the same demo user in both would share one limit.
 */
const reflowTenant = process.env.E2E_DEMO_REFLOW_TENANT ?? (tenant === "demo-municipality" ? "demo-company" : "demo-municipality");
/**
 * At least this long between two page visits of one test. A server-rendered page makes several
 * API calls, and the audit opens about 120 pages a person in a few minutes, more than the API's
 * per-person rate limit allows (it then answers 429 and a list page comes back empty, CI run of
 * 2026-11-26). People do not browse that fast; the tests should not either.
 */
const pageIntervalMs = Number(process.env.E2E_PAGE_INTERVAL_MS ?? 1500);
const lastVisit = new WeakMap<Page, number>();

/** Opens a page, keeping at least pageIntervalMs since this page's previous visit. */
async function visit(page: Page, url: string) {
  const wait = (lastVisit.get(page) ?? 0) + pageIntervalMs - Date.now();
  if (wait > 0) {
    await page.waitForTimeout(wait);
  }
  lastVisit.set(page, Date.now());
  await page.goto(url);
}
const reportDir = path.resolve(__dirname, "../accessibility-report");

type Persona = "visitor" | "requester" | "agent" | "lead" | "approver" | "admin" | "executive";

/** A page, or a detail page found by following the first link on `from` whose address matches `link`. */
type Target = string | { from: string; link: RegExp; name: string; optional?: boolean };

const pages: Record<Persona, Target[]> = {
  // Task J1: the installed app's offline page (shown online here; the audit checks its layout and wording);
  // task M2: the setup link page (without a link it says the link does not work).
  visitor: ["", "/help", "/catalog", "/contact", "/track", "/status", "/privacy", "/sign-in-problem", "/no-such-page", "/offline", "/account-setup",
    { from: "/help", link: /\/help\/articles\/[0-9a-f-]{36}$/, name: "/help/articles/[id]" },
    { from: "/catalog", link: /\/catalog\/[0-9a-f-]{36}$/, name: "/catalog/[id]" }],
  requester: ["", "/account", "/records", "/records/facilities/new", "/account/privacy", "/my-tickets", "/my-assets", "/my-incidents", "/my-incidents/new", "/my-requests", "/my-voice",
    "/notifications", "/catalog", "/contact",
    { from: "/my-incidents", link: /\/my-incidents\/[0-9a-f-]{36}$/, name: "/my-incidents/[id]" },
    { from: "/my-requests", link: /\/my-requests\/[0-9a-f-]{36}$/, name: "/my-requests/[id]" },
    { from: "/my-voice", link: /\/my-voice\/[0-9a-f-]{36}$/, name: "/my-voice/[id]" },
    { from: "/catalog", link: /\/catalog\/[0-9a-f-]{36}$/, name: "/catalog/[id]" },
    { from: "/contact", link: /\/contact\/[a-z-]+$/, name: "/contact/[type]" }],
  agent: ["/workspace", "/incidents", "/incidents/new", "/requests", "/tasks", "/chats", "/customer-voice", "/knowledge", "/knowledge/new",
    "/ai-approvals", "/notifications", "/cmdb", "/problems", "/problems/new", "/known-errors", "/records", "/records/facilities",
    { from: "/incidents", link: /\/incidents\/[0-9a-f-]{36}$/, name: "/incidents/[id]" },
    { from: "/requests", link: /\/requests\/[0-9a-f-]{36}$/, name: "/requests/[id]" },
    { from: "/customer-voice", link: /\/customer-voice\/[0-9a-f-]{36}$/, name: "/customer-voice/[id]" },
    { from: "/knowledge", link: /\/knowledge\/[0-9a-f-]{36}$/, name: "/knowledge/[id]" },
    { from: "/workspace", link: /\/workspace\/[0-9a-f-]{36}$/, name: "/workspace/[ticket]" },
    // Task G1: the CMDB and an item with its relationships, impact and map.
    { from: "/cmdb", link: /\/cmdb\/[0-9a-f-]{36}$/, name: "/cmdb/[id]" },
    // Task G3: assets, an asset, contracts, licences and the label scanner.
    "/assets", "/assets/contracts", "/assets/licenses", "/assets/scan",
    { from: "/assets", link: /\/assets\/[0-9a-f-]{36}$/, name: "/assets/[id]" },
    // Task H1: problems, one with its analysis and known error, and the known error database.
    { from: "/problems", link: /\/problems\/[0-9a-f-]{36}$/, name: "/problems/[id]" },
    // Task H3: changes, the calendar, raising a change and one change with its plans, risk and tasks.
    "/changes", "/changes/new", "/changes/calendar",
    { from: "/changes", link: /\/changes\/[0-9a-f-]{36}$/, name: "/changes/[id]" },
    // Task K2: the demo's facilities record type and one of its records.
    { from: "/records/facilities", link: /\/records\/facilities\/[0-9a-f-]{36}$/, name: "/records/[type]/[id]" }],
  lead: ["/knowledge/review", "/reports", "/reports/agents", "/settings/canned-responses"],
  // Detail pages marked optional depend on sample data the demo may not have at that moment
  // (an approval waiting for this approver, a webhook, a paid receipt); they are listed as skipped.
  approver: ["/approvals", { from: "/approvals", link: /\/approvals\/[0-9a-f-]{36}$/, name: "/approvals/[id]", optional: true }],
  admin: ["/settings", "/setup", "/privacy-requests", "/payments", "/reports/schedules", "/reports/schedules/new", "/cmdb/new", "/cmdb/review", "/assets/new",
    "/settings/ai", "/settings/ai/features", "/settings/ai/log", "/settings/ai/mcp", "/settings/ai/mcp-servers", "/settings/ai/prices",
    "/settings/ai/usage", "/settings/assignment", "/settings/attachments", "/settings/branding", "/settings/business-calendars",
    "/settings/assets", "/settings/catalog", "/settings/categories", "/settings/cmdb", "/settings/problems", "/settings/changes", "/settings/record-types", "/settings/chat", "/settings/connectors", "/settings/customer-groups",
    "/settings/customer-voice", "/settings/departments", "/settings/field-sets", "/settings/forms", "/settings/groups",
    "/settings/import-export", "/settings/incidents", "/settings/integrations", "/settings/integrations/log",
    "/settings/integrations/webhooks", "/settings/knowledge", "/settings/language", "/settings/license", "/settings/locations",
    "/settings/mailboxes", "/settings/messaging", "/settings/monitoring", "/settings/notifications",
    "/settings/notifications/deliveries", "/settings/payments", "/settings/privacy", "/settings/reports", "/settings/roles",
    "/settings/search", "/settings/sign-in", "/settings/sign-in/uae-pass", "/settings/skills", "/settings/sla-policies",
    "/settings/translations", "/settings/updates", "/settings/updates/malware-definitions", "/settings/users", "/settings/workflows",
    { from: "/settings/users", link: /\/settings\/users\/[0-9a-f-]{36}$/, name: "/settings/users/[id]" },
    { from: "/settings/roles", link: /\/settings\/roles\/[a-z0-9-]+$/, name: "/settings/roles/[key]" },
    { from: "/settings/groups", link: /\/settings\/groups\/[0-9a-f-]{36}$/, name: "/settings/groups/[id]" },
    { from: "/settings/forms", link: /\/settings\/forms\/[0-9a-f-]{36}$/, name: "/settings/forms/[id]" },
    { from: "/settings/field-sets", link: /\/settings\/field-sets\/[0-9a-f-]{36}$/, name: "/settings/field-sets/[id]" },
    { from: "/settings/catalog", link: /\/settings\/catalog\/services\/[0-9a-f-]{36}$/, name: "/settings/catalog/services/[id]" },
    { from: "/settings/mailboxes", link: /\/settings\/mailboxes\/[0-9a-f-]{36}$/, name: "/settings/mailboxes/[id]" },
    { from: "/settings/integrations/webhooks", link: /\/settings\/integrations\/webhooks\/[0-9a-f-]{36}$/, name: "/settings/integrations/webhooks/[id]", optional: true },
    { from: "/privacy-requests", link: /\/privacy-requests\/[0-9a-f-]{36}$/, name: "/privacy-requests/[id]" },
    { from: "/payments", link: /\/payments\/[0-9a-f-]{36}\/receipt$/, name: "/payments/[id]/receipt", optional: true }],
  executive: ["/reports", "/reports/volumes", "/reports/backlog", "/reports/sla", "/reports/happiness", "/customer-voice/report"],
};

interface Finding {
  page: string;
  language: string;
  rule: string;
  impact: string | null | undefined;
  help: string;
  targets: string[];
}

/**
 * Waits until a page has loaded and shows its main content. Not "networkidle": signed-in pages
 * keep a live-update connection open (and poll the notification count), so the network is never
 * idle.
 */
async function settle(page: Page) {
  await page.waitForLoadState("load");
  // Zelvo's pages have <main>; a Keycloak sign-in page (a fresh sign-in on the way) has its title.
  await page.locator("main, #kc-page-title").first().waitFor({ state: "visible" });
}

async function signInAs(page: Page, persona: Persona, baseURL: string, organisation = tenant) {
  if (persona === "visitor") {
    return;
  }
  const response = await page.request.post("/auth/demo", {
    form: { tenant: organisation, locale: "en", persona },
    headers: { Origin: baseURL },
  });
  expect(response.ok(), `demo sign-in as ${persona}`).toBeTruthy();
}

async function resolve(page: Page, language: string, target: Target): Promise<{ name: string; url: string | null }> {
  if (typeof target === "string") {
    return { name: target || "/", url: `/${language}${target}` };
  }
  await visit(page, `/${language}${target.from}`);
  await settle(page);
  // Some lists load in the browser after the page; look for the link for up to 10 seconds.
  const deadline = Date.now() + 10_000;
  for (;;) {
    const hrefs = await page.locator("a[href]").evaluateAll((links) => links.map((a) => a.getAttribute("href") ?? ""));
    const href = hrefs.find((h) => target.link.test(h.split("?")[0] ?? ""));
    if (href || Date.now() > deadline) {
      return { name: target.name, url: href ?? null };
    }
    await page.waitForTimeout(500);
  }
}

for (const persona of Object.keys(pages) as Persona[]) {
  test(`every page of the ${persona} meets WCAG 2.2 AA in English and Arabic`, async ({ page, baseURL }) => {
    test.setTimeout(20 * 60_000);
    // Task F7 also checks the Content-Security-Policy on every page: anything it blocks is a finding.
    await page.addInitScript(() => {
      const w = window as unknown as { cspViolations: string[] };
      w.cspViolations = [];
      document.addEventListener("securitypolicyviolation", (e) => w.cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    });
    await signInAs(page, persona, baseURL ?? "http://localhost:3000");
    const findings: Finding[] = [];
    const cspViolations: string[] = [];
    const missing: string[] = [];
    const skipped: string[] = [];
    for (const language of ["en", "ar"]) {
      for (const target of pages[persona]) {
        const { name, url } = await resolve(page, language, target);
        if (!url) {
          (typeof target !== "string" && target.optional ? skipped : missing).push(`${language} ${name}`);
          continue;
        }
        await visit(page, url);
        await settle(page);
        // Every page says its language and direction (WCAG 3.1.1, and right to left for Arabic).
        await expect(page.locator("html")).toHaveAttribute("lang", language);
        await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
        for (const v of await page.evaluate(() => (window as unknown as { cspViolations?: string[] }).cspViolations ?? [])) {
          cspViolations.push(`${language} ${name}: ${v}`);
        }
        const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
        for (const v of results.violations) {
          findings.push({ page: name, language, rule: v.id, impact: v.impact, help: v.help, targets: v.nodes.slice(0, 5).map((n) => n.target.join(" ")) });
        }
      }
    }
    mkdirSync(reportDir, { recursive: true });
    writeFileSync(path.join(reportDir, `${persona}.json`), JSON.stringify({ persona, missing, skipped, findings, cspViolations }, null, 2));
    expect(findings, `axe findings for the ${persona} (details in accessibility-report/${persona}.json)`).toEqual([]);
    expect(missing, "detail pages without sample data to follow").toEqual([]);
    expect(cspViolations, "content the Content-Security-Policy blocked").toEqual([]);
  });
}

/**
 * Keyboard use (WCAG 2.1.1, 2.1.2, 2.4.1, 2.4.7), which axe cannot check: on the main pages of
 * each kind of person, the first Tab reaches the skip link, which moves focus to the main
 * content; Tab then moves through the page without getting stuck, and every focused control
 * shows a visible focus indicator.
 */
const keyboardPages: { persona: Persona; paths: string[] }[] = [
  { persona: "visitor", paths: ["", "/help", "/catalog", "/contact"] },
  { persona: "requester", paths: ["/my-tickets", "/my-incidents/new"] },
  { persona: "agent", paths: ["/workspace", "/incidents"] },
  { persona: "admin", paths: ["/settings", "/settings/branding"] },
];

for (const { persona, paths } of keyboardPages) {
  test(`the ${persona}'s main pages work with the keyboard in English and Arabic`, async ({ page, baseURL }) => {
    test.setTimeout(10 * 60_000);
    await signInAs(page, persona, baseURL ?? "http://localhost:3000");
    const problems: string[] = [];
    for (const language of ["en", "ar"]) {
      for (const p of paths) {
        await visit(page, `/${language}${p}`);
        await settle(page);
        await page.keyboard.press("Tab");
        const first = await page.evaluate(() => (document.activeElement as HTMLAnchorElement | null)?.getAttribute("href"));
        if (first !== "#main") {
          problems.push(`${language}${p}: the first Tab reaches ${first ?? "nothing"}, not the skip link`);
        } else {
          await page.keyboard.press("Enter");
          const inMain = await page.evaluate(() => {
            const main = document.getElementById("main");
            return !!main && (document.activeElement === main || main.contains(document.activeElement) || location.hash === "#main");
          });
          if (!inMain) {
            problems.push(`${language}${p}: the skip link does not move to the main content`);
          }
        }

        const seen = new Set<string>();
        let repeats = 0;
        for (let i = 0; i < 40; i++) {
          await page.keyboard.press("Tab");
          const focus = await page.evaluate(() => {
            const el = document.activeElement as HTMLElement | null;
            if (!el || el === document.body) {
              return null;
            }
            const style = getComputedStyle(el);
            const visible = (style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0) || style.boxShadow !== "none";
            const path: string[] = [];
            for (let node: Element | null = el; node && path.length < 6; node = node.parentElement) {
              path.unshift(`${node.tagName.toLowerCase()}${node.id ? `#${node.id}` : ""}:${node.parentElement ? Array.prototype.indexOf.call(node.parentElement.children, node) : 0}`);
            }
            return { key: path.join(">"), visible, label: (el.getAttribute("aria-label") ?? el.textContent ?? el.tagName).trim().slice(0, 40) };
          });
          if (!focus) {
            break;
          }
          if (seen.has(focus.key)) {
            repeats++;
          }
          seen.add(focus.key);
          if (!focus.visible) {
            problems.push(`${language}${p}: no visible focus on "${focus.label}"`);
          }
        }
        // A trap keeps focus on the same few elements; normal pages wrap around at most once.
        if (seen.size < 3 && repeats > 10) {
          problems.push(`${language}${p}: focus is stuck`);
        }
      }
    }
    expect([...new Set(problems)]).toEqual([]);
  });
}

/**
 * Reflow (WCAG 1.4.10): at 320 CSS pixels wide (1280 px zoomed to 400 %) every page of the audit
 * scrolls only vertically, in both directions of text. Wide data tables may scroll inside their
 * own box.
 */
const reflowPages = (Object.keys(pages) as Persona[]).map((persona) => ({
  persona,
  paths: pages[persona].filter((t): t is string => typeof t === "string"),
}));

for (const { persona, paths } of reflowPages) {
  test(`the ${persona}'s main pages reflow at 320 pixels in English and Arabic`, async ({ page, baseURL }) => {
    test.setTimeout(10 * 60_000);
    await page.setViewportSize({ width: 320, height: 640 });
    await signInAs(page, persona, baseURL ?? "http://localhost:3000", reflowTenant);
    const wide: string[] = [];
    for (const language of ["en", "ar"]) {
      for (const p of paths) {
        await visit(page, `/${language}${p}`);
        await settle(page);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        if (overflow > 1) {
          wide.push(`${language}${p}: ${overflow} px wider than the screen`);
        }
      }
    }
    expect(wide).toEqual([]);
  });
}
