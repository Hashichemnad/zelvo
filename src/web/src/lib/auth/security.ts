/**
 * Small security helpers for sign-in, kept free of server-only imports so they can be tested.
 */
import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
import { locales, type Locale } from "@/i18n/routing";

/** A random, URL-safe secret (256 bits). */
export function randomSecret(): string {
  return randomBytes(32).toString("base64url");
}

/** Store key for a session: the store never holds the cookie value itself. */
export function sessionKey(sessionId: string): string {
  return `zelvo:session:${createHash("sha256").update(sessionId).digest("base64url")}`;
}

export function pendingSignInKey(state: string): string {
  return `zelvo:signin:${createHash("sha256").update(state).digest("base64url")}`;
}

/** Constant-time comparison of two secrets. */
export function secretsMatch(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) {
    return false;
  }
  const left = Buffer.from(a);
  const right = Buffer.from(b);
  return left.length === right.length && timingSafeEqual(left, right);
}

/**
 * Where to go after signing in: only a path on this site (no scheme, host or protocol-relative
 * `//`), otherwise the home page of the language. Prevents open redirects.
 */
export function safeReturnTo(value: string | null | undefined, locale: Locale): string {
  const fallback = `/${locale}`;
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) {
    return fallback;
  }
  try {
    const url = new URL(value, "http://zelvo.invalid");
    if (url.origin !== "http://zelvo.invalid") {
      return fallback;
    }
    return `${url.pathname}${url.search}`;
  } catch {
    return fallback;
  }
}

/** The language of a sign-in request, defaulting to English. */
export function localeOf(value: string | null | undefined): Locale {
  return (locales as readonly string[]).includes(value ?? "") ? (value as Locale) : "en";
}

/**
 * A changing request is accepted only from this site: the Origin header (sent by browsers on
 * POST) must be the app's own origin, and the session's CSRF token must match.
 */
export function isSameOriginRequest(origin: string | null, appUrl: URL): boolean {
  return origin !== null && origin === appUrl.origin;
}
