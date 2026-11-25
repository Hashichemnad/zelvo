import { defineConfig, devices } from "@playwright/test";

/** Specs that change tenant-wide settings; each runs in its own project, one after another. */
const tenantWide = [/settings\.spec\.ts/, /uae-pass\.spec\.ts/, /integrations\.spec\.ts/, /directory\.spec\.ts/, /sla\.spec\.ts/, /ai\.spec\.ts/, /knowledge\.spec\.ts/, /mcp\.spec\.ts/, /notifications\.spec\.ts/, /forms\.spec\.ts/, /catalog\.spec\.ts/, /channels\.spec\.ts/, /customer-voice\.spec\.ts/, /workflows\.spec\.ts/, /workspace\.spec\.ts/, /setup\.spec\.ts/];

/** Demo mode needs the demo installation (`make demo`); `make e2e-demo` runs it with E2E_DEMO=1. */
const demo = /demo\.spec\.ts/;
const demoRun = process.env.E2E_DEMO === "1";

/**
 * End-to-end tests against a running stack (`make up`). BASE_URL defaults to the local web port.
 * CHROMIUM_PATH lets a machine with a preinstalled browser use it instead of downloading one.
 */

export default defineConfig({
  testDir: "./specs",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: process.env.BASE_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  projects: demoRun
    ? [{ name: "demo", use: { ...devices["Desktop Chrome"] }, testMatch: demo }]
    : [
        { name: "desktop", use: { ...devices["Desktop Chrome"] }, testIgnore: [...tenantWide, demo] },
        { name: "mobile", use: { ...devices["Pixel 7"] }, testIgnore: [...tenantWide, demo] },
        // Changes tenant-wide defaults, so it runs after every other test, on its own.
        {
          name: "tenant-settings",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /settings\.spec\.ts/,
          dependencies: ["desktop", "mobile"],
        },
        // Turns UAE PASS on for the tenant (task D3); after tenant-settings, so the two never sign
        // in as the admin at the same time.
        {
          name: "tenant-uae-pass",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /uae-pass\.spec\.ts/,
          dependencies: ["tenant-settings"],
        },
        // Integrations (task E1) sign in as the admin too; after UAE PASS, on their own.
        {
          name: "tenant-integrations",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /integrations\.spec\.ts/,
          dependencies: ["tenant-uae-pass"],
        },
        // Directory and assignment (task B2) add groups and assignment rules; after integrations, on their own.
        {
          name: "tenant-directory",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /directory\.spec\.ts/,
          dependencies: ["tenant-integrations"],
        },
        // SLA management (task B3) adds a calendar and a policy; after the directory, on its own.
        {
          name: "tenant-sla",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /sla\.spec\.ts/,
          dependencies: ["tenant-directory"],
        },
        // Settings → AI (task E4) signs in as the admin and turns an AI feature on; last, on its own.
        {
          name: "tenant-ai",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /ai\.spec\.ts/,
          dependencies: ["tenant-sla"],
        },
        // Knowledge base (task D1) signs in as the agent and the lead; after AI, on its own.
        {
          name: "knowledge",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /knowledge\.spec\.ts/,
          dependencies: ["tenant-ai"],
        },
        // MCP server (task E6) turns the server on and adds an AI agent; after knowledge, on its own.
        {
          name: "tenant-mcp",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /mcp\.spec\.ts/,
          dependencies: ["knowledge"],
        },
        // Notifications (task B5) sign in as the requester and the admin; after MCP, on their own.
        {
          name: "notifications",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /notifications\.spec\.ts/,
          dependencies: ["tenant-mcp"],
        },
        // Form builder (task C1) signs in as the admin and adds a category and a form; after notifications, on its own.
        {
          name: "forms",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /forms\.spec\.ts/,
          dependencies: ["notifications"],
        },
        // Service catalog (task C2) adds a catalog with the team lead as editor and a service; after forms, on its own.
        {
          name: "catalog",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /catalog\.spec\.ts/,
          dependencies: ["forms"],
        },
        // Live chat (task E2) adds a group and a portal chat widget; after the service catalog, on its own.
        {
          name: "channels",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /channels\.spec\.ts/,
          dependencies: ["catalog"],
        },
        // Customer voice (task C6) turns anonymous messages on; after live chat, on its own.
        {
          name: "customer-voice",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /customer-voice\.spec\.ts/,
          dependencies: ["channels"],
        },
        // Workflow rules and approvals (task C3) add a catalog, a service with an approval and a rule; after customer voice, on its own.
        {
          name: "workflows",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /workflows\.spec\.ts/,
          dependencies: ["customer-voice"],
        },
        // Agent workspace (task B4) signs in as the agent with a second factor, like incidents.spec.ts;
        // run at the same time, both set up the agent's authenticator on a fresh stack and one of them
        // fails. Last, on its own, one test at a time.
        {
          name: "workspace",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /workspace\.spec\.ts/,
          fullyParallel: false,
          dependencies: ["workflows"],
        },
        // Task F2: the setup wizard creates departments from templates (with their groups, routing
        // and SLA policies) and marks the tenant's setup steps; last, on its own.
        {
          name: "setup",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /setup\.spec\.ts/,
          dependencies: ["workspace"],
        },
      ],
});
