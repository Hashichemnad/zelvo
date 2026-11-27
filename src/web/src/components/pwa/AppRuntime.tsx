"use client";

import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import { usePathname } from "next/navigation";
import { postJson } from "@/lib/client-api";
import { setOfflineWorkAllowed } from "@/lib/pwa/app-state";
import { useOnline } from "@/lib/pwa/hooks";
import { claimStore, listOutbox, offlineStoreAvailable, sendQueuedWork } from "@/lib/pwa/offline-store";

export interface AppRuntimeProps {
  /** The signed-in person's id, or null for visitors. */
  userId: string | null;
  /** The session's CSRF token, for sending unsent work. */
  csrfToken: string | null;
  /** Whether the tenant lets the app keep data on the device (decision 13). */
  offlineData: boolean;
}

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

const dismissedKey = "zelvo-install-dismissed";

function remembered(): boolean {
  try {
    return window.localStorage.getItem(dismissedKey) === "1";
  } catch {
    return false;
  }
}

function remember(): void {
  try {
    window.localStorage.setItem(dismissedKey, "1");
  } catch {
    // Storage blocked: the offer comes back next visit.
  }
}

/** iPhone or iPad Safari, where installing is "Add to Home Screen" in the Share menu. */
export function needsIosGuidance(userAgent: string, standalone: boolean, maxTouchPoints = 0): boolean {
  if (standalone) return false;
  const ios = /iphone|ipad|ipod/i.test(userAgent) || (/macintosh/i.test(userAgent) && maxTouchPoints > 1);
  const otherBrowser = /crios|fxios|edgios|opios/i.test(userAgent);
  return ios && !otherBrowser && /safari/i.test(userAgent);
}

function isStandalone(): boolean {
  return window.matchMedia?.("(display-mode: standalone)").matches === true || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

/**
 * The installable web app on every page (module 19, task J1): registers the service worker, makes
 * the offline data belong to the signed-in person (clearing it for anyone else, after signing out
 * or when the tenant turns it off), sends unsent work when the connection comes back, says when
 * the device is offline, and offers to install the app (or, on iPhone and iPad, how to).
 */
export function AppRuntime({ userId, csrfToken, offlineData }: AppRuntimeProps) {
  const t = useTranslations("app");
  const locale = useLocale();
  const online = useOnline();
  const [installEvent, setInstallEvent] = useState<BeforeInstallPromptEvent | null>(null);
  const [dismissed, setDismissed] = useState(false);
  // iPhone and iPad have no install prompt: say how to add the app to the home screen instead.
  const iosDevice = useSyncExternalStore(
    () => () => undefined,
    () => !remembered() && needsIosGuidance(navigator.userAgent, isStandalone(), navigator.maxTouchPoints),
    () => false,
  );
  const iosGuide = iosDevice && !dismissed;
  const [message, setMessage] = useState<string | null>(null);
  // The offline page is cached without a session: it must never clear what it is there to show.
  const offlinePage = /\/offline$/.test(usePathname() ?? "");

  const flush = useCallback(async () => {
    if (!userId || !csrfToken || !offlineData) return;
    if ((await listOutbox()).length === 0) return;
    const { sent, failed } = await sendQueuedWork((item) => postJson(item.path, item.body, csrfToken).then(() => undefined));
    if (sent > 0) setMessage(t("sent", { count: sent }));
    else if (failed > 0) setMessage(t("notSent", { count: failed }));
  }, [userId, csrfToken, offlineData, t]);

  useEffect(() => {
    if ("serviceWorker" in navigator && window.isSecureContext) {
      navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => undefined);
    }
    setOfflineWorkAllowed(Boolean(userId && csrfToken && offlineData && offlineStoreAvailable()));
    if (offlinePage || !navigator.onLine) return;
    void claimStore(userId, offlineData).then(flush).catch(() => undefined);
  }, [userId, csrfToken, offlineData, flush, offlinePage]);

  useEffect(() => {
    const goneOnline = () => void flush().catch(() => undefined);
    window.addEventListener("online", goneOnline);
    return () => window.removeEventListener("online", goneOnline);
  }, [flush]);

  useEffect(() => {
    if (remembered() || isStandalone()) return;
    const offer = (event: Event) => {
      event.preventDefault();
      setInstallEvent(event as BeforeInstallPromptEvent);
    };
    window.addEventListener("beforeinstallprompt", offer);
    const installed = () => setInstallEvent(null);
    window.addEventListener("appinstalled", installed);
    return () => {
      window.removeEventListener("beforeinstallprompt", offer);
      window.removeEventListener("appinstalled", installed);
    };
  }, []);

  async function install() {
    if (!installEvent) return;
    await installEvent.prompt();
    await installEvent.userChoice.catch(() => undefined);
    setInstallEvent(null);
  }

  function dismiss() {
    remember();
    setInstallEvent(null);
    setDismissed(true);
  }

  const bar = "mx-auto flex max-w-5xl flex-wrap items-center gap-3 px-4 py-2 text-sm";
  return (
    <>
      {/* A live region without role="status", so pages keep their own status message as the only one. */}
      <div aria-live="polite" aria-atomic="true" className="sr-only" data-testid="app-message">
        {message}
      </div>
      {!online && !offlinePage && (
        <div className="border-b border-line bg-surface-muted" data-testid="offline-banner">
          <p className={bar}>
            <span>{offlineData && userId ? t("offlineBanner") : t("offlineBannerNoData")}</span>
            <a href={`/${locale}/offline`} className="font-semibold text-brand underline">{t("openOffline")}</a>
          </p>
        </div>
      )}
      {message && (
        <div className="border-b border-line bg-surface-muted">
          <p className={bar} aria-hidden="true">{message}</p>
        </div>
      )}
      {(installEvent || iosGuide) && (
        <section aria-label={t("installLabel")} className="border-b border-line bg-surface" data-testid="install-offer">
          <div className={bar}>
            {installEvent ? (
              <>
                <span>{t("installText")}</span>
                <button type="button" onClick={() => void install()} className="rounded-md bg-brand px-3 py-1.5 font-semibold text-surface">
                  {t("install")}
                </button>
              </>
            ) : (
              <span data-testid="ios-guide">{t("iosGuide")}</span>
            )}
            <button type="button" onClick={dismiss} className="rounded-md border border-line px-3 py-1.5 font-semibold">
              {t("notNow")}
            </button>
          </div>
        </section>
      )}
    </>
  );
}
