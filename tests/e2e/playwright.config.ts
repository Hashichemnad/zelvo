import { defineConfig, devices } from "@playwright/test";

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
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] }, testIgnore: /settings\.spec\.ts/ },
    { name: "mobile", use: { ...devices["Pixel 7"] }, testIgnore: /settings\.spec\.ts/ },
    // Changes tenant-wide defaults, so it runs after every other test, on its own.
    {
      name: "tenant-settings",
      use: { ...devices["Desktop Chrome"] },
      testMatch: /settings\.spec\.ts/,
      dependencies: ["desktop", "mobile"],
    },
  ],
});
