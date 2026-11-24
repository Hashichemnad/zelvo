"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { getDirection, type Locale } from "@/i18n/routing";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Page } from "@/lib/integrations";
import { safeLinkPath, type AppNotification } from "@/lib/notifications";
import { secondaryButton } from "../settings/integrations/ui";

export interface NotificationListProps {
  initial: Page<AppNotification>;
  csrfToken: string;
  display: DateDisplayOptions;
}

/** Tells the header's bell to check the unread count again. */
function changed() {
  window.dispatchEvent(new Event("zelvo:notifications-changed"));
}

/**
 * The signed-in person's notifications, newest first. Each was written in their language when it
 * was sent; one written in the other language (after they changed it) is marked with its own
 * language and direction. Opening one marks it read.
 */
export function NotificationList({ initial, csrfToken, display }: NotificationListProps) {
  const t = useTranslations("notifications");
  const [items, setItems] = useState(initial.items);
  const [total, setTotal] = useState(initial.totalCount);
  const [page, setPage] = useState(initial.page);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const locale = display.locale as Locale;

  async function load(nextPage: number, onlyUnread: boolean, append: boolean) {
    try {
      const result = await getJson<Page<AppNotification>>(`/v1/me/notifications?page=${nextPage}&pageSize=${initial.pageSize}&unreadOnly=${onlyUnread}`);
      setItems(append ? [...items, ...result.items] : result.items);
      setTotal(result.totalCount);
      setPage(result.page);
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  async function markRead(id: string) {
    try {
      await postJson(`/v1/me/notifications/${id}/read`, undefined, csrfToken);
      const now = new Date().toISOString();
      setItems(items.map((n) => (n.id === id ? { ...n, readAt: n.readAt ?? now } : n)));
      changed();
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError && error.status === 404 ? t("unavailable") : t("failed") });
    }
  }

  async function markAllRead() {
    try {
      await postJson("/v1/me/notifications/read-all", undefined, csrfToken);
      const now = new Date().toISOString();
      setItems(unreadOnly ? [] : items.map((n) => ({ ...n, readAt: n.readAt ?? now })));
      setMessage({ ok: true, text: t("allRead") });
      changed();
    } catch {
      setMessage({ ok: false, text: t("failed") });
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-4">
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            checked={unreadOnly}
            onChange={(e) => {
              setUnreadOnly(e.target.checked);
              void load(1, e.target.checked, false);
            }}
          />
          {t("unreadOnly")}
        </label>
        <button type="button" className={secondaryButton} onClick={markAllRead} disabled={!items.some((n) => !n.readAt)}>
          {t("markAllRead")}
        </button>
      </div>
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
      {items.length === 0 ? (
        <p>{unreadOnly ? t("emptyUnread") : t("empty")}</p>
      ) : (
        <ul className="divide-y divide-line rounded-lg border border-line bg-surface" data-testid="notification-list">
          {items.map((n) => {
            const own = n.language === null || n.language === locale;
            const path = safeLinkPath(n.linkPath);
            const title = n.title ?? t(`events.${n.eventType.replace(/[.-]/g, "_")}`);
            return (
              <li key={n.id} className={`flex flex-wrap items-start justify-between gap-3 p-4 ${n.readAt ? "" : "bg-surface-muted"}`}>
                <div className="min-w-0 flex-1 space-y-1" lang={own ? undefined : n.language!} dir={own ? undefined : getDirection(n.language!)}>
                  <p className="font-semibold">
                    {!n.readAt && <span className="me-2 rounded-full border border-brand px-2 text-xs text-brand">{t("unread")}</span>}
                    {path ? (
                      <a href={`/${locale}${path}`} className="text-brand underline-offset-4 hover:underline" onClick={() => void markRead(n.id)}>
                        {title}
                      </a>
                    ) : (
                      title
                    )}
                  </p>
                  {n.text && <p className="break-words">{n.text}</p>}
                  <p className="text-sm text-ink-muted" lang={locale} dir={getDirection(locale)}>
                    <time dateTime={n.createdAt}>{formatDateTime(new Date(n.createdAt), display)}</time>
                  </p>
                </div>
                {!n.readAt && (
                  <button type="button" className={secondaryButton} onClick={() => void markRead(n.id)}>
                    {t("markRead")}
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {items.length < total && (
        <button type="button" className={secondaryButton} onClick={() => void load(page + 1, unreadOnly, true)}>
          {t("more")}
        </button>
      )}
    </div>
  );
}
