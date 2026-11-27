import { expect, test } from "@playwright/test";

/**
 * Task J1 (module 19): the installable web app. Each host serves its own manifest in both
 * languages, the service worker opens the offline page without a connection, and the header's
 * links fold behind a menu on phones.
 */
test.describe("installable web app", () => {
  test("every page links the manifest of its language, with the tenant's name and icons", async ({ page, request }) => {
    await page.goto("/ar");
    await expect(page.locator('link[rel="manifest"]')).toHaveAttribute("href", "/manifest.webmanifest?lang=ar");
    await expect(page.locator('link[rel="apple-touch-icon"]')).toHaveAttribute("href", /apple-180/);

    const manifest = await (await request.get("/manifest.webmanifest?lang=ar")).json();
    expect(manifest).toMatchObject({ id: "/", lang: "ar", dir: "rtl", display: "standalone", start_url: "/ar?source=app", scope: "/" });
    expect(manifest.icons.map((i: { purpose: string }) => i.purpose)).toEqual(["any", "any", "maskable", "maskable"]);
    for (const icon of manifest.icons as { src: string }[]) {
      const response = await request.get(icon.src);
      expect(response.status(), icon.src).toBe(200);
      expect(response.headers()["content-type"]).toContain("image/png");
    }
    const english = await (await request.get("/manifest.webmanifest")).json();
    expect(english.dir).toBe("ltr");
    expect(english.id).toBe(manifest.id);
  });

  test("the service worker is never cached and covers the whole site", async ({ request }) => {
    const response = await request.get("/sw.js");
    expect(response.status()).toBe(200);
    expect(response.headers()["cache-control"]).toContain("no-store");
    expect(response.headers()["service-worker-allowed"]).toBe("/");
  });

  test("without a connection a page opens the offline page in its language", async ({ page, context }) => {
    await page.goto("/en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
    });
    // The worker keeps the offline pages when it installs; wait until it controls the page.
    await page.reload();
    await expect.poll(() => page.evaluate(() => Boolean(navigator.serviceWorker.controller))).toBe(true);
    await expect.poll(() => page.evaluate(async () => (await caches.keys()).some((k) => k.startsWith("zelvo-shell-")))).toBe(true);

    await context.setOffline(true);
    await page.goto("/ar/help").catch(() => undefined);
    await expect(page).toHaveURL(/\/ar\/offline$/);
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByTestId("offline-page")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("لا يوجد اتصال بالشبكة");
    await context.setOffline(false);
  });

  test("on phones the header's links are behind a menu button", async ({ page, isMobile }) => {
    test.skip(!isMobile, "phones only");
    await page.goto("/en");
    const menu = page.getByTestId("menu-button");
    await expect(menu).toHaveAttribute("aria-expanded", "false");
    await expect(page.getByRole("link", { name: "Help", exact: true })).toBeHidden();
    await menu.click();
    await expect(menu).toHaveAttribute("aria-expanded", "true");
    await page.getByRole("link", { name: "Help", exact: true }).click();
    await expect(page).toHaveURL(/\/en\/help$/);
    await expect(page.getByTestId("menu-button")).toHaveAttribute("aria-expanded", "false");
  });
});
