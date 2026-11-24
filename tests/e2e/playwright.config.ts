import { defineConfig, devices } from "@playwright/test";

/** Specs that change tenant-wide settings; each runs in its own project, one after another. */
const tenantWide = [/settings\.spec\.ts/, /uae-pass\.spec\.ts/, /integrations\.spec\.ts/, /directory\.spec\.ts/, /sla\.spec\.ts/, /ai\.spec\.ts/, /knowledge\.spec\.ts/, /notifications\.spec\.ts/];

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
        // Notifications (task B5) sign in as the requester and the admin; after knowledge, on their own.
        {
          name: "notifications",
          use: { ...devices["Desktop Chrome"] },
          testMatch: /notifications\.spec\.ts/,
          dependencies: ["knowledge"],
        },
      ],
});
