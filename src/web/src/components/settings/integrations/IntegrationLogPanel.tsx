"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, getJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { prettyJson, type IntegrationLogDetail, type IntegrationLogEntry, type Page } from "@/lib/integrations";
import { cellClass, codeClass, inputClass, secondaryButton, tableClass } from "./ui";

const pageSize = 25;

/** Settings → Integrations → Integration log: calls in and out, with masked bodies on demand. */
export function IntegrationLogPanel({ initial, display }: { initial: Page<IntegrationLogEntry>; display: DateDisplayOptions }) {
  const t = useTranslations("settings.integrations");
  const tl = useTranslations("settings.integrations.log");
  const tCommon = useTranslations("common");
  const [page, setPage] = useState(initial);
  const [direction, setDirection] = useState<"all" | "Incoming" | "Outgoing">("all");
  const [failedOnly, setFailedOnly] = useState(false);
  const [open, setOpen] = useState<Record<number, IntegrationLogDetail | "loading">>({});
  const [error, setError] = useState<string | null>(null);

  const date = (value: string) => formatDateTime(new Date(value), display);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const pages = Math.max(1, Math.ceil(page.totalCount / pageSize));

  async function load(number: number, dir = direction, failed = failedOnly) {
    setError(null);
    const query = new URLSearchParams({ page: String(number), pageSize: String(pageSize) });
    if (dir !== "all") {
      query.set("direction", dir);
    }
    if (failed) {
      query.set("failed", "true");
    }
    try {
      setPage(await getJson<Page<IntegrationLogEntry>>(`/v1/integrations/log?${query}`));
      setOpen({});
    } catch (refused) {
      setError(refused instanceof ApiError && refused.rateLimited ? tCommon("rateLimited") : t("failed"));
    }
  }

  async function toggle(id: number) {
    if (open[id]) {
      const rest = { ...open };
      delete rest[id];
      setOpen(rest);
      return;
    }
    setOpen({ ...open, [id]: "loading" });
    try {
      const detail = await getJson<IntegrationLogDetail>(`/v1/integrations/log/${id}`);
      setOpen((current) => ({ ...current, [id]: detail }));
    } catch {
      setError(t("failed"));
    }
  }

  return (
    <div className="space-y-4" data-testid="integration-log">
      <div className="flex flex-wrap items-end gap-4">
        <div>
          <label htmlFor="log-direction" className="font-semibold">{tl("direction")}</label>
          <select
            id="log-direction"
            value={direction}
            onChange={(e) => {
              const value = e.target.value as typeof direction;
              setDirection(value);
              void load(1, value);
            }}
            className={inputClass}
          >
            {(["all", "Incoming", "Outgoing"] as const).map((value) => (
              <option key={value} value={value}>{tl(`directions.${value}`)}</option>
            ))}
          </select>
        </div>
        <label className="flex items-center gap-2 pb-2">
          <input
            type="checkbox"
            checked={failedOnly}
            onChange={(e) => {
              setFailedOnly(e.target.checked);
              void load(1, direction, e.target.checked);
            }}
          />
          {tl("failedOnly")}
        </label>
      </div>
      <p role="status" aria-live="polite" className="text-bad">{error ?? ""}</p>

      {page.items.length === 0 ? (
        <p>{tl("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={tl("listLabel")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{tl("when")}</th>
                <th scope="col" className={cellClass}>{tl("who")}</th>
                <th scope="col" className={cellClass}>{tl("call")}</th>
                <th scope="col" className={cellClass}>{tl("status")}</th>
                <th scope="col" className={cellClass}>{tl("duration")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{tl("details")}</span></th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((entry) => {
                const detail = open[entry.id];
                const failed = entry.statusCode === null || entry.statusCode >= 400;
                return (
                  <tr key={entry.id} data-testid="log-row">
                    <td className={cellClass}>{date(entry.occurredAt)}</td>
                    <td className={cellClass}>
                      <span className="block">{entry.name ?? tl("unknown")}</span>
                      <span className="block text-xs text-ink-muted">{tl(`directions.${entry.direction}`)}</span>
                    </td>
                    <td className={cellClass}>
                      <code dir="ltr" lang="en" className="break-all text-xs">{entry.method} {entry.target}</code>
                      {detail && detail !== "loading" && (
                        <div className="mt-2 space-y-2">
                          <p className="font-semibold">{tl("request")}</p>
                          <pre dir="ltr" lang="en" className={codeClass}>{prettyJson(detail.requestBody) || tl("noBody")}</pre>
                          <p className="font-semibold">{tl("responseBody")}</p>
                          <pre dir="ltr" lang="en" className={codeClass}>{prettyJson(detail.responseBody) || tl("noBody")}</pre>
                          {entry.correlationId && (
                            <p className="text-xs">{tl("correlation")}: <code dir="ltr" lang="en">{entry.correlationId}</code></p>
                          )}
                        </div>
                      )}
                    </td>
                    <td className={`${cellClass} ${failed ? "text-bad" : "text-ok"}`}>
                      {entry.statusCode !== null ? number(entry.statusCode) : t("webhook.noAnswer")}
                      {entry.error && <code dir="ltr" lang="en" className="block text-xs">{entry.error}</code>}
                    </td>
                    <td className={cellClass}>{tl("durationMs", { ms: number(entry.durationMs) })}</td>
                    <td className={cellClass}>
                      <button type="button" onClick={() => toggle(entry.id)} aria-expanded={Boolean(detail)} className={secondaryButton}>
                        {detail === "loading" ? t("loading") : detail ? tl("hide") : tl("details")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <nav className="flex flex-wrap items-center gap-3" aria-label={tl("listLabel")}>
        <button type="button" disabled={page.page <= 1} onClick={() => load(page.page - 1)} className={secondaryButton}>
          {t("previous")}
        </button>
        <span>{t("pageOf", { page: number(page.page), pages: number(pages) })}</span>
        <button type="button" disabled={page.page >= pages} onClick={() => load(page.page + 1)} className={secondaryButton}>
          {t("next")}
        </button>
      </nav>
    </div>
  );
}
