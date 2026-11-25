import { expect, test } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Staff need a second factor, and on a fresh stack their first sign-in sets up the authenticator
 * app. When two parallel tests make that first sign-in at the same moment, both register an
 * authenticator and later codes match only one of them. This runs before the parallel projects
 * and signs each staff user in once, one after another, so the setup happens exactly once.
 */
test("staff users have their authenticator set up", async ({ browser }) => {
  test.setTimeout(120_000);
  for (const user of ["admin", "agent", "lead"] as const) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await signInWithPassword(page, devUser(user));
    await completeSecondFactor(page, devUser(user));
    // Back on Zelvo's pages, signed in.
    await expect(page).not.toHaveURL(/\/realms\//);
    await context.close();
  }
});
