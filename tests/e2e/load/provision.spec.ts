import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Task F7: prepares a running installation for the load tests in tests/load (see its README).
 * Runs only with E2E_LOAD=1 (`npx playwright test --project=load-provision`):
 *
 * 1. The administrator creates LOAD_API_KEYS API keys that act as the agent role, with a rate
 *    limit of 100,000 a minute each (the most an administrator may set).
 * 2. The development agent signs in once with a second factor, and LOAD_SESSIONS more web
 *    sessions are opened from Keycloak's single sign-on (no new codes), so load-test agents use
 *    the web app's backend-for-frontend exactly as browsers do.
 * 3. The agent raises LOAD_INCIDENTS open sample incidents for the development requester, so
 *    lists, searches, incident pages and changes have data (integrations may read incidents but
 *    not change them, so changes in the load tests always go as people).
 * 4. Task L1 (problems, changes, CMDB): the administrator adds Zelvo's standard CMDB classes,
 *    makes (or reuses) the discovery API source "Load test discovery" and one more API key for
 *    pushing configuration items to it (tenant administrator role, scopes cmdb.manage and the
 *    reads and links tests/load/cmdb/provision-cmdb.mjs needs); the agent raises LOAD_PROBLEMS
 *    problems from three sample incidents each, with a bilingual known error, and LOAD_CHANGES
 *    normal changes with plans and windows over the next 30 days (raising problems and changes is
 *    for people only). LOAD_CMDB=0 leaves step 4 out. The configuration items themselves are
 *    pushed afterwards by tests/load/cmdb/provision-cmdb.mjs, which also links them to these
 *    problems and changes.
 *
 * Everything is written to tests/load/.credentials/load.json (git-ignored): keys and session
 * cookies are real credentials of that installation. Revoke the keys in Settings → Integrations
 * after the run.
 */

const apiUrl = process.env.API_URL ?? "http://localhost:8080";
const tenant = process.env.LOAD_TENANT ?? "default";
const keyCount = Number(process.env.LOAD_API_KEYS ?? "10");
const incidentCount = Number(process.env.LOAD_INCIDENTS ?? "500");
const sessionCount = Number(process.env.LOAD_SESSIONS ?? "50");
const problemCount = Number(process.env.LOAD_PROBLEMS ?? "50");
const changeCount = Number(process.env.LOAD_CHANGES ?? "100");
const phase2 = process.env.LOAD_CMDB !== "0";
const output = path.resolve(__dirname, "../../load/.credentials/load.json");
// Reading only: integrations may not change incidents or write comments (people only). Task L1
// adds the Phase 2 reads the agent role holds: problems and known errors, changes, the CMDB.
const scopes = ["incidents.read", "knowledge.read", "problems.read", "changes.read", "cmdb.read"];
// Task L1: the key that pushes configuration items through the discovery API (cmdb.manage) and
// links them to the sample problems and changes (problems.edit, changes.edit). Integrations may
// hold these; cmdb.discovery.manage (making the source) stays with people, so the administrator
// makes the source below.
const cmdbScopes = ["cmdb.read", "cmdb.manage", "problems.read", "problems.edit", "changes.read", "changes.edit"];
const discoverySourceName = "Load test discovery";

/** Calls the API through the web app's proxy from the page, as the signed-in person. */
async function bff<T>(page: Page, method: string, apiPath: string, body?: unknown): Promise<T> {
  const csrf = await page.locator('input[name="csrf"]').first().getAttribute("value");
  return page.evaluate(
    async ({ method, apiPath, body, csrf }) => {
      const response = await fetch(`/bff/api/v1/${apiPath}`, {
        method,
        headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrf ?? "" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        throw new Error(`${method} ${apiPath}: ${response.status} ${await response.text()}`);
      }
      return (response.status === 204 ? undefined : await response.json()) as T;
    },
    { method, apiPath, body, csrf },
  );
}

/** The cookie header a load-test agent sends, and the session's CSRF token. */
async function sessionOf(context: BrowserContext, page: Page): Promise<{ cookie: string; csrf: string }> {
  const cookies = await context.cookies(page.url());
  const session = cookies.find((c) => /zelvo-session$/.test(c.name));
  expect(session, "the web app's session cookie").toBeTruthy();
  const csrf = await page.locator('input[name="csrf"]').first().getAttribute("value");
  expect(csrf).toBeTruthy();
  return { cookie: `${session!.name}=${session!.value}`, csrf: csrf! };
}

test.describe("load-test provisioning", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(30 * 60_000);

  const result: {
    createdAt: string;
    apiUrl: string;
    webUrl: string;
    tenant: string;
    requesterId: string;
    apiKeys: { id: string; key: string }[];
    incidentIds: string[];
    sessions: { cookie: string; csrf: string }[];
    problems: { id: string; incidentIds: string[] }[];
    changes: { id: string; plannedStart: string; plannedEnd: string; configurationItemIds: string[] }[];
    cmdb?: { sourceId: string; key: { id: string; key: string }; items: number; itemIds: string[]; impactIds: string[]; mapIds: string[] };
  } = {
    createdAt: new Date().toISOString(),
    apiUrl,
    webUrl: "",
    tenant,
    requesterId: "",
    apiKeys: [],
    incidentIds: [],
    sessions: [],
    problems: [],
    changes: [],
  };

  test("the administrator creates API keys for the agent role", async ({ page, baseURL }) => {
    result.webUrl = baseURL ?? "http://localhost:3000";
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    const stamp = new Date().toISOString().slice(0, 16);
    for (let i = 0; i < keyCount; i++) {
      const created = await bff<{ client: { id: string }; apiKey: string }>(page, "POST", "integrations/clients", {
        kind: "ApiKey",
        name: `Load test ${stamp} #${i + 1}`,
        description: "Task F7 load test; revoke after the run.",
        role: "agent",
        scopes,
        rateLimitPerMinute: 100_000,
      });
      result.apiKeys.push({ id: created.client.id, key: created.apiKey });
    }

    const people = await bff<{ items: { id: string; email: string }[] }>(page, "GET", `users?q=${encodeURIComponent(devUser("requester"))}`);
    const requester = people.items.find((u) => u.email === devUser("requester"));
    expect(requester, "the development requester").toBeTruthy();
    result.requesterId = requester!.id;

    if (phase2) {
      // Task L1: the classes the pushed items use, the discovery API source and its key.
      await bff(page, "POST", "cmdb/classes/standard", {});
      const sources = await bff<{ id: string; name: string; kind: string }[]>(page, "GET", "cmdb/discovery/sources");
      const existing = sources.find((s) => s.name === discoverySourceName && s.kind === "DiscoveryApi");
      const source =
        existing ??
        (await bff<{ id: string }>(page, "POST", "cmdb/discovery/sources", {
          name: discoverySourceName,
          kind: "DiscoveryApi",
          active: true,
        }));
      const pusher = await bff<{ client: { id: string }; apiKey: string }>(page, "POST", "integrations/clients", {
        kind: "ApiKey",
        name: `Load test CMDB ${stamp}`,
        description: "Task L1 load test: pushes configuration items; revoke after the run.",
        role: "tenant-admin",
        scopes: cmdbScopes,
        rateLimitPerMinute: 100_000,
      });
      result.cmdb = { sourceId: source.id, key: { id: pusher.client.id, key: pusher.apiKey }, items: 0, itemIds: [], impactIds: [], mapIds: [] };
    }
  });

  test("the agent opens web sessions for the load-test agents and raises sample incidents", async ({ page, context }) => {
    await signInWithPassword(page, devUser("agent"));
    await completeSecondFactor(page, devUser("agent"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);
    result.sessions.push(await sessionOf(context, page));

    for (let i = 1; i < sessionCount; i++) {
      // Forget only the web app's session; Keycloak's single sign-on (with the second factor)
      // signs the next one in without a new code.
      const keep = (await context.cookies()).filter((c) => !/zelvo-session$/.test(c.name));
      await context.clearCookies();
      await context.addCookies(keep);
      await page.goto("/auth/login?mfa=1&locale=en&returnTo=%2Fen%2Faccount");
      await expect(page).toHaveURL(/\/en\/account$/);
      result.sessions.push(await sessionOf(context, page));
    }

    // Sample incidents, raised by the agent as people do ("Log an incident"): integrations may
    // read incidents but not write them. The installation must not require a form for incidents
    // without a category (a required field would refuse these).
    const words = ["printer", "VPN", "email", "laptop", "Wi-Fi", "password", "Teams", "badge", "monitor", "طابعة", "الشبكة", "البريد"];
    for (let i = 0; i < incidentCount; i++) {
      const word = words[i % words.length];
      const created = await bff<{ id: string }>(page, "POST", "incidents", {
        title: `Load test: ${word} not working (${i + 1})`,
        description: `Sample incident ${i + 1} for the load test. ${word} stopped working this morning.`,
        requesterId: result.requesterId,
        impact: (i % 3) + 1,
        urgency: ((i + 1) % 3) + 1,
      });
      result.incidentIds.push(created.id);
    }

    if (phase2) {
      // Task L1: problems raised from three sample incidents each, with a known error, so the
      // problem pages, the known error search and the incidents' matching known errors have data.
      const problems = Math.min(problemCount, Math.floor(result.incidentIds.length / 3));
      for (let i = 0; i < problems; i++) {
        const word = words[i % words.length];
        const incidentIds = result.incidentIds.slice(i * 3, i * 3 + 3);
        const created = await bff<{ id: string }>(page, "POST", "problems", {
          title: `Load test: ${word} keeps failing (${i + 1})`,
          description: `Sample problem ${i + 1} for the load test, raised from three incidents about ${word}.`,
          incidentIds,
        });
        await bff(page, "PUT", `problems/${created.id}/known-error`, {
          title: { en: `${word} stops working after the morning update`, ar: `يتوقف ${word} عن العمل بعد تحديث الصباح` },
          symptoms: { en: `${word} does not respond.`, ar: `لا يستجيب ${word}.` },
          workaround: { en: `Restart ${word} and sign in again.`, ar: `أعد تشغيل ${word} ثم سجّل الدخول مرة أخرى.` },
        });
        result.problems.push({ id: created.id, incidentIds });
      }

      // Normal changes with plans, in evening windows over the next 30 days. Changes i, i + 30,
      // i + 60 and i + 90 share a window; provision-cmdb.mjs gives them the same server, so the
      // calendar and the conflict check find conflicts.
      const tomorrow = new Date();
      tomorrow.setUTCHours(0, 0, 0, 0);
      tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
      for (let i = 0; i < changeCount; i++) {
        const start = new Date(tomorrow.getTime() + ((i % 30) * 24 + 18 + (i % 3) * 2) * 3_600_000);
        const end = new Date(start.getTime() + 3 * 3_600_000);
        const word = words[i % words.length];
        const created = await bff<{ id: string }>(page, "POST", "changes", {
          kind: "Normal",
          title: `Load test: update the ${word} servers (${i + 1})`,
          description: `Sample change ${i + 1} for the load test.`,
          plannedStart: start.toISOString(),
          plannedEnd: end.toISOString(),
          implementationPlan: "Stop the service, install the update, start the service.",
          testPlan: "Sign in and open the main page.",
          backoutPlan: "Restore the snapshot taken before the update.",
        });
        result.changes.push({ id: created.id, plannedStart: start.toISOString(), plannedEnd: end.toISOString(), configurationItemIds: [] });
      }
    }

    mkdirSync(path.dirname(output), { recursive: true });
    writeFileSync(output, JSON.stringify(result, null, 2), { mode: 0o600 });
  });
});
