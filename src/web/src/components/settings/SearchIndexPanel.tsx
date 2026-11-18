"use client";

import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import type { SearchStatus } from "@/lib/api";
import { getJson, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";

const buttonClass =
  "rounded-md px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60";

/** How often the status is read again while changes are waiting or a rebuild runs. */
export const REFRESH_INTERVAL_MS = 3000;

/**
 * Settings → Search: documents in the index, updates waiting, the last error, and the rebuild
 * button. While updates are waiting or a rebuild runs, the status refreshes itself.
 */
export function SearchIndexPanel({
  initial,
  csrfToken,
  display,
}: {
  initial: SearchStatus;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("settings.search");
  const [status, setStatus] = useState(initial);
  const [action, setAction] = useState<"idle" | "confirm" | "rebuilding" | "queued" | "failed" | "refreshFailed">("idle");
  const [queued, setQueued] = useState(0);

  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : t("never"));

  const refresh = useCallback(async () => {
    try {
      setStatus(await getJson<SearchStatus>("/v1/search/status"));
    } catch {
      setAction("refreshFailed");
    }
  }, []);

  const busy = status.pendingUpdates > 0 || status.rebuildInProgress;
  useEffect(() => {
    if (!busy) {
      return undefined;
    }
    const timer = setInterval(refresh, REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [busy, refresh]);

  async function rebuild() {
    setAction("rebuilding");
    try {
      const result = await postJson<{ documentsQueued: number }>("/v1/search/rebuild", undefined, csrfToken);
      setQueued(result.documentsQueued);
      setAction("queued");
      await refresh();
    } catch {
      setAction("failed");
    }
  }

  const message =
    action === "queued"
      ? t("rebuildQueued", { count: queued, formatted: number(queued) })
      : action === "failed"
        ? t("rebuildFailed")
        : action === "refreshFailed"
          ? t("refreshFailed")
          : "";

  return (
    <div className="space-y-6" data-testid="search-index-panel">
      <dl className="grid max-w-2xl gap-x-6 gap-y-3 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t("state")}</dt>
        <dd data-testid="search-state" className={status.available && status.failedUpdates === 0 ? "text-ok" : "text-bad"}>
          {!status.available
            ? t("unavailable")
            : status.rebuildInProgress
              ? t("rebuilding")
              : busy
                ? t("indexing")
                : status.failedUpdates > 0
                  ? t("needsAttention")
                  : t("upToDate")}
        </dd>

        <dt className="font-semibold">{t("documents")}</dt>
        <dd data-testid="search-documents">{status.documents === null ? t("unknown") : number(status.documents)}</dd>

        <dt className="font-semibold">{t("pending")}</dt>
        <dd data-testid="search-pending">{number(status.pendingUpdates)}</dd>

        <dt className="font-semibold">{t("failed")}</dt>
        <dd data-testid="search-failed">{number(status.failedUpdates)}</dd>

        <dt className="font-semibold">{t("lastIndexed")}</dt>
        <dd>{date(status.lastIndexedAt)}</dd>

        <dt className="font-semibold">{t("lastRebuild")}</dt>
        <dd>{status.rebuildCompletedAt ? date(status.rebuildCompletedAt) : t("never")}</dd>

        <dt className="font-semibold">{t("lastError")}</dt>
        <dd data-testid="search-last-error">
          {status.lastError ? (
            <>
              <span className="block">{date(status.lastErrorAt)}</span>
              {/* Technical detail from the search engine, in English. */}
              <code dir="ltr" lang="en" className="mt-1 block break-words rounded bg-surface-muted px-2 py-1 text-sm">
                {status.lastError}
              </code>
            </>
          ) : (
            t("noError")
          )}
        </dd>
      </dl>

      <div className="max-w-2xl space-y-3 rounded-md border border-line p-4">
        <h3 className="text-lg font-semibold">{t("rebuildTitle")}</h3>
        <p>{t("rebuildHelp")}</p>
        <div className="flex flex-wrap items-center gap-3">
          {action === "confirm" ? (
            <>
              <button type="button" onClick={rebuild} className={`${buttonClass} bg-brand text-surface`}>
                {t("rebuildConfirm")}
              </button>
              <button type="button" onClick={() => setAction("idle")} className={`${buttonClass} border border-line text-ink`}>
                {t("cancel")}
              </button>
            </>
          ) : (
            <button
              type="button"
              onClick={() => setAction("confirm")}
              disabled={action === "rebuilding" || status.rebuildInProgress}
              className={`${buttonClass} bg-brand text-surface`}
            >
              {action === "rebuilding" ? t("rebuildStarting") : t("rebuild")}
            </button>
          )}
          <button type="button" onClick={refresh} className={`${buttonClass} border border-line text-ink`}>
            {t("refresh")}
          </button>
        </div>
        {action === "confirm" && <p>{t("rebuildConfirmText")}</p>}
        <p role="status" aria-live="polite" className={action === "failed" || action === "refreshFailed" ? "text-bad" : "text-ok"}>
          {message}
        </p>
      </div>
    </div>
  );
}
