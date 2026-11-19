import "server-only";
import { createHash } from "node:crypto";
import { NextResponse } from "next/server";
import type { Locale } from "@/i18n/routing";
import { getAuthConfig } from "./auth/config";
import { startSignIn } from "./auth/sign-in";
import { tenantCookieMaxAgeSeconds, tenantCookieName } from "./tenant";

/** Who the demo panel switches to: a sample organisation and, optionally, one of its demo users. */
export interface DemoSwitch {
  tenant: string;
  username: string | null;
  locale: Locale;
}

export const demoSwitchSeconds = 300;

export function demoSwitchKey(ticket: string): string {
  return `zelvo:demo-switch:${createHash("sha256").update(ticket).digest("base64url")}`;
}

/** The login hint that asks the demo sign-in step for `username`. */
export function demoLoginHint(username: string): string {
  return `zelvo-demo:${username}`;
}

/**
 * Opens the organisation: signs the demo user in (one click, no form), or goes to the home page
 * as a visitor. The organisation is remembered on the device.
 */
export async function finishDemoSwitch(next: DemoSwitch): Promise<NextResponse> {
  const config = getAuthConfig(process.env, next.tenant);
  const home = `/${next.locale}`;
  const response = next.username
    ? await startSignIn(config, { locale: next.locale, returnTo: home, mfa: false, loginHint: demoLoginHint(next.username) })
    : NextResponse.redirect(new URL(home, config.appUrl), 303);
  response.cookies.set(tenantCookieName, config.tenant, {
    httpOnly: true,
    secure: config.secureCookies,
    sameSite: "lax",
    path: "/",
    maxAge: tenantCookieMaxAgeSeconds,
  });
  return response;
}
