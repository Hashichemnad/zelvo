"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { aiFeatureKeys, featureLabelKey, formatUsd, type AiCall, type AiCallDetail, type AiCallOutcome } from "@/lib/ai";
import { ApiError, getJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Page } from "@/lib/integrations";
import { cellClass, codeClass, inputClass, secondaryButton, tableClass } from "../integrations/ui";
import { errorKey } from "./problems";

const pageSize = 25;
const outcomes: readonly AiCallOutcome[] = ["Succeeded", "Failed", "Rejected", "LimitReached"];

/** Settings → AI → AI log: every AI call, with the masked prompt and answer on demand (kept 30 days). */
export function AiLogPanel({ initial, display }: { initial: Page<AiCall>; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.log");
  const tNames = useTranslations("settings.ai.featureNames");
  const tErrors = useTranslations("settings.ai.errors");
  const tAi = useTranslations("settings.ai");
  const [page, setPage] = useState(initial);
  const [feature, setFeature] = useState("");
  const [outcome, setOutcome] = useState("");
  const [injection, setInjection] = useState(false);
  const [open, setOpen] = useState<Record<number, AiCallDetail | "loading">>({});
  const [error, setError] = useState<string | null>(null);
  const date = (value: string) => formatDateTime(new Date(value), display);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const pages = Math.max(1, Math.ceil(page.totalCount / pageSize));

  async function load(n: number, f = feature, o = outcome, i = injection) {
    setError(null);
    const query = new URLSearchParams({ page: String(n), pageSize: String(pageSize) });
    if (f) query.set("feature", f);
    if (o) query.set("outcome", o);
    if (i) query.set("injectionSuspected", "true");
    try {
      setPage(await getJson<Page<AiCall>>(`/v1/ai/calls?${query}`));
      setOpen({});
    } catch (refused) {
      setError(refused instanceof ApiError && refused.rateLimited ? tAi("rateLimited") : tAi("failed"));
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
      const detail = await getJson<AiCallDetail>(`/v1/ai/calls/${id}`);
      setOpen((current) => ({ ...current, [id]: detail }));
    } catch {
      setError(tAi("failed"));
    }
  }

  return (
    <div className="space-y-4" data-testid="ai-log">
      <p className="max-w-3xl">{t("intro")}</p>
      <div className="flex flex-wrap items-end gap-4">
        <div>
          <label htmlFor="ai-log-feature" className="font-semibold">{t("feature")}</label>
          <select id="ai-log-feature" value={feature} onChange={(e) => { setFeature(e.target.value); void load(1, e.target.value); }} className={inputClass}>
            <option value="">{t("all")}</option>
            {[...aiFeatureKeys, "test"].map((f) => (
              <option key={f} value={f}>{tNames(f)}</option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="ai-log-outcome" className="font-semibold">{t("outcome")}</label>
          <select id="ai-log-outcome" value={outcome} onChange={(e) => { setOutcome(e.target.value); void load(1, feature, e.target.value); }} className={inputClass}>
            <option value="">{t("all")}</option>
            {outcomes.map((o) => (
              <option key={o} value={o}>{t(`outcomes.${o}`)}</option>
            ))}
          </select>
        </div>
        <label className="flex items-center gap-2 pb-2">
          <input type="checkbox" checked={injection} onChange={(e) => { setInjection(e.target.checked); void load(1, feature, outcome, e.target.checked); }} />
          {t("injectionOnly")}
        </label>
      </div>
      <p role="status" aria-live="polite" className="text-bad">{error ?? ""}</p>

      {page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={t("listLabel")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("when")}</th>
                <th scope="col" className={cellClass}>{t("feature")}</th>
                <th scope="col" className={cellClass}>{t("model")}</th>
                <th scope="col" className={cellClass}>{t("outcome")}</th>
                <th scope="col" className={cellClass}>{t("usage")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{t("details")}</span></th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((call) => {
                const detail = open[call.id];
                return (
                  <tr key={call.id} data-testid="ai-log-row">
                    <td className={cellClass}>
                      {date(call.occurredAt)}
                      <span className="block text-xs text-ink-muted">{call.userName ?? t("system")}</span>
                    </td>
                    <td className={cellClass}>
                      {tNames(featureLabelKey(call.feature))}
                      {call.injectionSuspected && <span className="block text-xs font-semibold text-bad">{t("injection")}</span>}
                      {call.maskedValues > 0 && <span className="block text-xs text-ink-muted">{t("masked", { count: call.maskedValues })}</span>}
                    </td>
                    <td className={cellClass}>
                      <span className="block">{call.providerName ?? "—"}</span>
                      <code dir="ltr" lang="en" className="text-xs">{call.model}</code>
                      {detail && detail !== "loading" && (
                        <div className="mt-2 space-y-2">
                          {detail.textExpired ? (
                            <p>{t("expired")}</p>
                          ) : (
                            <>
                              <p className="font-semibold">{t("prompt")}</p>
                              <pre dir="auto" className={codeClass}>{detail.prompt}</pre>
                              <p className="font-semibold">{t("response")}</p>
                              <pre dir="auto" className={codeClass}>{detail.response ?? t("noResponse")}</pre>
                            </>
                          )}
                          {call.correlationId && <p className="text-xs">{t("correlation")}: <code dir="ltr" lang="en">{call.correlationId}</code></p>}
                        </div>
                      )}
                    </td>
                    <td className={`${cellClass} ${call.outcome === "Succeeded" ? "text-ok" : "text-bad"}`}>
                      {t(`outcomes.${call.outcome}`)}
                      {call.errorCode && <span className="block text-xs">{tErrors(errorKey(call.errorCode))}</span>}
                    </td>
                    <td className={cellClass}>
                      {t("tokens", { input: number(call.inputTokens), output: number(call.outputTokens) })}
                      <span className="block text-xs">{call.costUsd === null ? t("noPrice") : formatUsd(call.costUsd, display.locale, display.digits, 4)}</span>
                      <span className="block text-xs text-ink-muted">{t("durationMs", { ms: number(call.durationMs) })}</span>
                    </td>
                    <td className={cellClass}>
                      <button type="button" onClick={() => toggle(call.id)} aria-expanded={Boolean(detail)} className={secondaryButton}>
                        {detail === "loading" ? t("loading") : detail ? t("hide") : t("details")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <nav className="flex flex-wrap items-center gap-3" aria-label={t("pages")}>
        <button type="button" disabled={page.page <= 1} onClick={() => load(page.page - 1)} className={secondaryButton}>{t("previous")}</button>
        <span>{t("pageOf", { page: number(page.page), pages: number(pages) })}</span>
        <button type="button" disabled={page.page >= pages} onClick={() => load(page.page + 1)} className={secondaryButton}>{t("next")}</button>
      </nav>
    </div>
  );
}
