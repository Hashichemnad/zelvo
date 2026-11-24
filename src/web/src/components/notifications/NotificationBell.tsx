"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { getJson } from "@/lib/client-api";
import { formatNumber, type DigitSystem } from "@/lib/format";

/** How often the bell asks for the unread count. Live updates over SignalR come with the agent workspace (B4). */
export const bellPollMilliseconds = 30_000;

export interface NotificationBellProps {
  /** Unread count when the page was rendered. */
  initialCount: number;
  locale: string;
  digits: DigitSystem;
}

/**
 * The header's link to the notifications page with the unread count. The count is checked every
 * 30 seconds while the page is visible, and a change is announced to screen readers politely.
 */
export function NotificationBell({ initialCount, locale, digits }: NotificationBellProps) {
  const t = useTranslations("notifications.bell");
  const [count, setCount] = useState(initialCount);
  const [announcement, setAnnouncement] = useState("");
  const last = useRef(initialCount);

  useEffect(() => {
    let stopped = false;
    async function refresh() {
      if (document.visibilityState !== "visible") {
        return;
      }
      try {
        const { count: next } = await getJson<{ count: number }>("/v1/me/notifications/unread-count");
        if (!stopped && next !== last.current) {
          last.current = next;
          setCount(next);
          setAnnouncement(t("unread", { count: next }));
        }
      } catch {
        // Signed out or offline: keep the last count.
      }
    }
    const timer = window.setInterval(refresh, bellPollMilliseconds);
    const onChange = () => void refresh();
    window.addEventListener("zelvo:notifications-changed", onChange);
    return () => {
      stopped = true;
      window.clearInterval(timer);
      window.removeEventListener("zelvo:notifications-changed", onChange);
    };
  }, [t]);

  return (
    <>
      <Link
        href="/notifications"
        className="inline-flex items-center gap-1 rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
        data-testid="notification-bell"
      >
        <svg aria-hidden="true" viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2">
          <path d="M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0" />
        </svg>
        <span>{t("label")}</span>
        {count > 0 && (
          <span className="rounded-full bg-brand px-2 text-xs text-surface" data-testid="notification-count">
            <span aria-hidden="true">{formatNumber(count, locale, digits)}</span>
          </span>
        )}
        <span className="sr-only">{`, ${t("unread", { count })}`}</span>
      </Link>
      {/* A live region without the status role: pages keep their own single role="status" message. */}
      <span className="sr-only" aria-live="polite" aria-atomic="true" data-testid="notification-announcement">
        {announcement}
      </span>
    </>
  );
}
