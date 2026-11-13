import { createHmac } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { expect, type Page } from "@playwright/test";

/**
 * Helpers for sign-in journeys against the running stack: development users (created by
 * `migrate`), emailed codes read from Mailpit, and authenticator-app codes computed like a phone
 * app does (RFC 6238).
 */

const envFile = path.resolve(__dirname, "../../../deploy/compose/.env");
const stateDir = path.resolve(__dirname, "../.auth");

function readEnv(name: string): string | undefined {
  if (process.env[name]) {
    return process.env[name];
  }
  if (!existsSync(envFile)) {
    return undefined;
  }
  const line = readFileSync(envFile, "utf8").split("\n").find((l) => l.startsWith(`${name}=`));
  return line?.slice(name.length + 1).trim();
}

export const devPassword = (): string => {
  const value = readEnv("ZELVO_DEV_USERS_PASSWORD");
  if (!value) {
    throw new Error("ZELVO_DEV_USERS_PASSWORD is not set (deploy/compose/.env); development users are needed.");
  }
  return value;
};

export const mailpitUrl = process.env.MAILPIT_URL ?? `http://localhost:${readEnv("MAILPIT_PORT") ?? "8025"}`;

export const devUser = (name: "requester" | "agent" | "lead" | "approver" | "admin" | "executive") => `${name}@zelvo.test`;

/** The newest sign-in code sent to an address, or null when none arrives. */
export async function emailedCode(address: string, timeoutMs = 10_000): Promise<string | null> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const search = await fetch(`${mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`);
    const result = (await search.json()) as { messages?: { ID: string }[] };
    if (result.messages?.length) {
      const message = (await (await fetch(`${mailpitUrl}/api/v1/message/${result.messages[0].ID}`)).json()) as { Text: string };
      return message.Text.match(/\b(\d{6})\b/)?.[1] ?? null;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return null;
}

export async function deleteEmailsTo(address: string): Promise<void> {
  await fetch(`${mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`, { method: "DELETE" });
}

function base32(value: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of value.replace(/[\s=]/g, "").toUpperCase()) {
    bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
  }
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) {
    bytes.push(parseInt(bits.slice(i, i + 8), 2));
  }
  return Buffer.from(bytes);
}

export function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const hash = createHmac("sha1", base32(secret)).update(counter).digest();
  const offset = hash[hash.length - 1] & 15;
  return ((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, "0");
}

/**
 * A code Keycloak has not seen yet: it refuses a code that was already used, also by an earlier
 * test run within the same 30-second window. The last used code is kept next to the secret.
 */
async function freshCode(user: string, secret: string): Promise<string> {
  const usedFile = path.join(stateDir, `${user}.used`);
  const used = existsSync(usedFile) ? readFileSync(usedFile, "utf8").trim() : "";
  let code = totp(secret);
  while (code === used) {
    await new Promise((resolve) => setTimeout(resolve, 1_000));
    code = totp(secret);
  }
  mkdirSync(stateDir, { recursive: true });
  writeFileSync(usedFile, code);
  return code;
}

/**
 * Completes Keycloak's second-factor step: sets up an authenticator app the first time (the
 * secret is kept in tests/e2e/.auth for later runs against the same stack), or enters a code.
 */
export async function completeSecondFactor(page: Page, user: string): Promise<void> {
  const secretFile = path.join(stateDir, `${user}.totp`);
  const showSecret = page.getByText(/Unable to scan/i);
  const otp = page.locator("#otp");
  await expect(showSecret.or(otp)).toBeVisible();

  if (await showSecret.isVisible()) {
    // First time: Keycloak asks to set up an authenticator app; read the secret as text.
    await showSecret.click();
    const setup = page.locator("#kc-totp-secret-key");
    await expect(setup).toBeVisible();
    const secret = (await setup.innerText()).replace(/\s/g, "");
    mkdirSync(stateDir, { recursive: true });
    writeFileSync(secretFile, secret);
    await page.locator("#totp").fill(await freshCode(user, secret));
    await page.locator("[type=submit]").first().click();
    return;
  }

  if (!existsSync(secretFile)) {
    throw new Error(`${user} already has an authenticator app, but its secret is unknown. Reset the stack (make reset).`);
  }
  await otp.fill(await freshCode(user, readFileSync(secretFile, "utf8").trim()));
  await page.locator("#kc-login").click();
}

/** Opens the site in a language and starts sign-in from the header. */
export async function startSignIn(page: Page, locale: "en" | "ar"): Promise<void> {
  await page.goto(`/${locale}`);
  await page.getByRole("link", { name: locale === "ar" ? "تسجيل الدخول" : "Sign in" }).click();
  await expect(page).toHaveURL(/\/realms\/zelvo-[a-z0-9-]+\/protocol\/openid-connect\/auth/);
}

export async function signInWithPassword(page: Page, user: string, locale: "en" | "ar" = "en"): Promise<void> {
  await startSignIn(page, locale);
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(devPassword());
  await page.locator("#kc-login").click();
}
