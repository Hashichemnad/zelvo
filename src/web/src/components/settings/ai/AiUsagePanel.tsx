"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { featureLabelKey, formatUsd, type AiBudget, type AiUsage } from "@/lib/ai";
import { ApiError, getJson, putJson } from "@/lib/client-api";
import { formatLongDate, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { cellClass, inputClass, primaryButton, secondaryButton, tableClass } from "../integrations/ui";

/**
 * Settings → AI → Usage and cost: the month's calls, tokens and cost (headline numbers, a daily
 * cost chart with the same figures in a table), per feature and per model, and the monthly limit
 * (warning at 80 %, AI stops at 100 %).
 */
export function AiUsagePanel({ initial, csrfToken, display }: { initial: AiUsage; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.usage");
  const tNames = useTranslations("settings.ai.featureNames");
  const tAi = useTranslations("settings.ai");
  const [usage, setUsage] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const usd = (value: number) => formatUsd(value, display.locale, display.digits);
  const current = initial.month;

  async function show(month: string) {
    setError(null);
    try {
      setUsage(await getJson<AiUsage>(`/v1/ai/usage?month=${month}`));
    } catch {
      setError(tAi("failed"));
    }
  }

  const monthName = new Intl.DateTimeFormat(`${display.locale}-AE`, {
    calendar: "gregory",
    numberingSystem: display.digits,
    timeZone: "UTC",
    month: "long",
    year: "numeric",
  }).format(new Date(usage.periodStart));

  return (
    <div className="space-y-8" data-testid="ai-usage-panel">
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => show(shiftMonth(usage.month, -1))} className={secondaryButton}>{t("previousMonth")}</button>
        <h3 className="text-lg font-semibold" aria-live="polite">{monthName}</h3>
        <button type="button" onClick={() => show(shiftMonth(usage.month, 1))} disabled={usage.month >= current} className={secondaryButton}>
          {t("nextMonth")}
        </button>
      </div>
      {error && <p role="alert" className="text-bad">{error}</p>}

      {usage.budget && <BudgetSection budget={usage.budget} csrfToken={csrfToken} display={display} onSaved={() => show(usage.month)} />}

      <dl className="grid gap-4 sm:grid-cols-4" data-testid="ai-usage-totals">
        <Stat label={t("cost")} value={usd(usage.totals.costUsd)} />
        <Stat label={t("calls")} value={number(usage.totals.calls)} />
        <Stat label={t("failedCalls")} value={number(usage.totals.failedCalls)} />
        <Stat label={t("tokens")} value={t("tokensValue", { input: number(usage.totals.inputTokens), output: number(usage.totals.outputTokens) })} />
      </dl>
      {usage.unpricedCalls > 0 && <p role="note" className="font-semibold">{t("unpriced", { count: usage.unpricedCalls })}</p>}

      <DailyChart usage={usage} display={display} />

      <section aria-labelledby="ai-usage-features" className="space-y-2">
        <h3 id="ai-usage-features" className="text-lg font-semibold">{t("byFeature")}</h3>
        {usage.features.length === 0 ? (
          <p>{t("none")}</p>
        ) : (
          <TotalsTable
            label={t("byFeature")}
            first={t("feature")}
            rows={usage.features.map((f) => ({ key: f.key, name: featureLabelKey(f.key) === "unknown" ? f.key : tNames(featureLabelKey(f.key)), totals: f }))}
            number={number}
            usd={usd}
          />
        )}
      </section>

      <section aria-labelledby="ai-usage-models" className="space-y-2">
        <h3 id="ai-usage-models" className="text-lg font-semibold">{t("byModel")}</h3>
        {usage.models.length === 0 ? (
          <p>{t("none")}</p>
        ) : (
          <TotalsTable
            label={t("byModel")}
            first={t("model")}
            rows={usage.models.map((m) => ({ key: `${m.providerId}/${m.model}`, name: `${m.providerName ?? "—"} · ${m.model ?? "—"}`, totals: m.totals, unpriced: m.unpricedCalls }))}
            number={number}
            usd={usd}
            unpricedLabel={t("noPrice")}
          />
        )}
      </section>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-line p-4">
      <dt className="text-sm text-ink-muted">{label}</dt>
      <dd className="text-2xl font-bold">{value}</dd>
    </div>
  );
}

function BudgetSection({ budget, csrfToken, display, onSaved }: { budget: AiBudget; csrfToken: string; display: DateDisplayOptions; onSaved: () => Promise<void> }) {
  const t = useTranslations("settings.ai.usage");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [limit, setLimit] = useState(budget.monthlyLimitUsd > 0 ? String(budget.monthlyLimitUsd) : "");
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const usd = (value: number) => formatUsd(value, display.locale, display.digits);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const resets = formatLongDate(new Date(budget.periodEnd), { ...display, timeZone: "UTC" });

  async function submit(event: FormEvent) {
    event.preventDefault();
    const value = limit.trim() === "" ? 0 : Number(limit);
    if (!Number.isFinite(value) || value < 0) {
      setMessage({ ok: false, text: t("limitInvalid") });
      return;
    }
    try {
      await putJson("/v1/ai/limit", { monthlyLimitUsd: value }, csrfToken);
      setMessage({ ok: true, text: t("limitSaved") });
      await onSaved();
    } catch (refused) {
      setMessage({ ok: false, text: refused instanceof ApiError && refused.status === 400 ? t("limitInvalid") : tAi("failed") });
    }
  }

  const percent = Math.min(100, budget.percent ?? 0);
  return (
    <section aria-labelledby={`${id}-title`} className="space-y-3 rounded-md border border-line p-4" data-testid="ai-budget">
      <h3 id={`${id}-title`} className="text-lg font-semibold">{t("limitTitle")}</h3>
      {budget.state === "NoLimit" ? (
        <p>{t("noLimit", { spent: usd(budget.spentUsd) })}</p>
      ) : (
        <>
          <p>{t("spentOf", { spent: usd(budget.spentUsd), limit: usd(budget.monthlyLimitUsd), percent: number(budget.percent ?? 0) })}</p>
          <div
            role="meter"
            aria-label={t("limitTitle")}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
            aria-valuetext={t("percentText", { percent: number(budget.percent ?? 0) })}
            className="h-3 w-full overflow-hidden rounded-full bg-surface-muted"
          >
            <div className={`h-full rounded-full ${budget.state === "Ok" ? "bg-brand" : "bg-bad"}`} style={{ inlineSize: `${percent}%` }} />
          </div>
        </>
      )}
      {budget.state === "Warning" && (
        <p role="alert" className="font-semibold text-bad" data-testid="ai-budget-warning">
          {t("warning", { percent: number(budget.warningPercent) })}
        </p>
      )}
      {budget.state === "Reached" && (
        <p role="alert" className="font-semibold text-bad" data-testid="ai-budget-reached">
          {t("reached", { date: resets })}
        </p>
      )}
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2" noValidate>
        <div>
          <label htmlFor={`${id}-limit`} className="font-semibold">{t("limitLabel")}</label>
          <input id={`${id}-limit`} dir="ltr" inputMode="decimal" value={limit} onChange={(e) => setLimit(e.target.value)} aria-describedby={`${id}-limit-help`} className={inputClass} />
        </div>
        <button type="submit" className={primaryButton}>{t("saveLimit")}</button>
      </form>
      <p id={`${id}-limit-help`} className="text-sm text-ink-muted">{t("limitHelp", { percent: number(budget.warningPercent) })}</p>
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-bad"}>
          {message.text}
        </p>
      )}
    </section>
  );
}

/** Cost per day: one series, bars from the baseline, values on hover and focus, and the same numbers in a table. */
function DailyChart({ usage, display }: { usage: AiUsage; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.usage");
  const [showTable, setShowTable] = useState(false);
  const [active, setActive] = useState<number | null>(null);
  const usd = (value: number) => formatUsd(value, display.locale, display.digits);
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const max = Math.max(...usage.days.map((d) => d.costUsd), 0);
  const day = (key: string) => number(Number(key.slice(8)));

  return (
    <section aria-labelledby="ai-usage-daily" className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id="ai-usage-daily" className="text-lg font-semibold">{t("daily")}</h3>
        <button type="button" onClick={() => setShowTable(!showTable)} aria-expanded={showTable} className={secondaryButton}>
          {showTable ? t("hideTable") : t("showTable")}
        </button>
      </div>
      {max === 0 ? (
        <p>{t("noCost")}</p>
      ) : (
        <div className="relative" dir="ltr">
          <div className="flex h-40 items-end gap-0.5 border-b border-line" role="img" aria-label={t("dailyLabel", { max: usd(max) })}>
            {usage.days.map((d, i) => (
              <div
                key={d.key}
                onMouseEnter={() => setActive(i)}
                onMouseLeave={() => setActive(null)}
                className="flex h-full grow basis-0 items-end"
              >
                <div
                  className={`w-full rounded-t ${active === i ? "bg-ink" : "bg-brand"}`}
                  style={{ blockSize: d.costUsd > 0 ? `${Math.max(2, (d.costUsd / max) * 100)}%` : 0 }}
                />
              </div>
            ))}
          </div>
          <div className="flex justify-between text-xs text-ink-muted">
            <span>{day(usage.days[0]!.key)}</span>
            <span>{day(usage.days[usage.days.length - 1]!.key)}</span>
          </div>
          {active !== null && (
            <p className="absolute start-0 top-0 rounded bg-surface px-2 py-1 text-sm shadow" role="status" dir="auto">
              {t("tooltip", { day: day(usage.days[active]!.key), cost: usd(usage.days[active]!.costUsd), calls: number(usage.days[active]!.calls) })}
            </p>
          )}
        </div>
      )}
      {showTable && (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={t("daily")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("day")}</th>
                <th scope="col" className={cellClass}>{t("calls")}</th>
                <th scope="col" className={cellClass}>{t("cost")}</th>
              </tr>
            </thead>
            <tbody>
              {usage.days.filter((d) => d.calls > 0).map((d) => (
                <tr key={d.key}>
                  <td className={cellClass}>{formatLongDate(new Date(`${d.key}T12:00:00Z`), { ...display, timeZone: "UTC" })}</td>
                  <td className={cellClass}>{number(d.calls)}</td>
                  <td className={cellClass}>{usd(d.costUsd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function TotalsTable({
  label,
  first,
  rows,
  number,
  usd,
  unpricedLabel,
}: {
  label: string;
  first: string;
  rows: { key: string; name: string; totals: AiUsage["totals"]; unpriced?: number }[];
  number: (v: number) => string;
  usd: (v: number) => string;
  unpricedLabel?: string;
}) {
  const t = useTranslations("settings.ai.usage");
  return (
    <div className="overflow-x-auto">
      <table className={tableClass} aria-label={label}>
        <thead>
          <tr>
            <th scope="col" className={cellClass}>{first}</th>
            <th scope="col" className={cellClass}>{t("calls")}</th>
            <th scope="col" className={cellClass}>{t("failedCalls")}</th>
            <th scope="col" className={cellClass}>{t("inputTokens")}</th>
            <th scope="col" className={cellClass}>{t("outputTokens")}</th>
            <th scope="col" className={cellClass}>{t("cost")}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.key}>
              <th scope="row" className={`${cellClass} font-semibold`}>{row.name}</th>
              <td className={cellClass}>{number(row.totals.calls)}</td>
              <td className={cellClass}>{number(row.totals.failedCalls)}</td>
              <td className={cellClass}>{number(row.totals.inputTokens)}</td>
              <td className={cellClass}>{number(row.totals.outputTokens)}</td>
              <td className={cellClass}>
                {usd(row.totals.costUsd)}
                {row.unpriced ? <span className="block text-xs text-ink-muted">{unpricedLabel} ({number(row.unpriced)})</span> : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** yyyy-MM moved by a number of months. */
export function shiftMonth(month: string, by: number): string {
  const [year, m] = month.split("-").map(Number);
  const date = new Date(Date.UTC(year!, m! - 1 + by, 1));
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
}
