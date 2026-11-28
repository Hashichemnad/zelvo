"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Localized } from "@/components/directory/common";
import { secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import type { WhatIf } from "@/lib/changes";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { ChangeStatusLine, RiskBadge, useSaver } from "./common";

/**
 * The what-if simulation of a change (task H5): the blast radius from the CMDB, similar past
 * changes with their outcomes, and the predicted risk beside the questionnaire's level, with the
 * reasons; optionally explained by the AI, which only explains. Shown on the change page and to
 * approvers; once the change is approved it shows the simulation kept with it.
 */
export function ChangeWhatIf({ initial, aiAvailable, csrfToken, display, compact = false }: {
  initial: WhatIf;
  aiAvailable: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
  compact?: boolean;
}) {
  const t = useTranslations("changes.whatIf");
  const tOutcomes = useTranslations("changes.outcomes");
  const locale = useLocale() as Locale;
  const id = useId();
  const { status, run, busy } = useSaver();
  const [whatIf, setWhatIf] = useState(initial);
  const p = whatIf.predicted;
  const blast = whatIf.blast;
  const departments = blast.impact.consumers.filter((c) => c.node.kind === "Department");
  const groups = blast.impact.consumers.filter((c) => c.node.kind === "CustomerGroup");

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="change-what-if">
      <div className="space-y-1">
        <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
        <p className="text-sm text-ink-muted">{whatIf.kept ? t("kept", { date: formatDateTime(new Date(whatIf.computedAt), display) }) : t("live")}</p>
      </div>

      <div className="grid gap-4 md:grid-cols-2">
        <div className="space-y-2" data-testid="what-if-predicted">
          <h3 className="font-semibold">{t("predicted")}</h3>
          <p><RiskBadge level={p.level} /> <span>{t("chance", { chance: p.failureChance })}</span></p>
          <p className="text-sm">{t("questionnaire")} <RiskBadge level={p.questionnaire} /></p>
          {!p.history ? <p className="text-sm text-ink-muted">{t("noHistory", { rate: p.baseRate })}</p> : (
            <ul className="space-y-1 text-sm">
              {p.reasons.map((r) => (
                <li key={r.cohort}>{t(`cohorts.${r.cohort}`, { changes: r.changes, failed: r.failed, problems: r.withProblems, rate: r.failureRate })}</li>
              ))}
              <li className="text-ink-muted">{t("baseRate", { rate: p.baseRate })}</li>
            </ul>
          )}
          <p className="text-sm text-ink-muted">{t("notADecision")}</p>
        </div>
        <div className="space-y-2" data-testid="what-if-blast">
          <h3 className="font-semibold">{t("blast")}</h3>
          {blast.items.length === 0 ? <p className="text-sm">{t("noItems")}</p> : (
            <ul className="space-y-1 text-sm">
              <li>{t("items", { count: blast.items.length, reached: blast.impact.items.length })}</li>
              <li>{t("services", { count: blast.impact.businessServices.length })}
                {blast.impact.businessServices.length > 0 && <>: {blast.impact.businessServices.map((s, i) => (
                  <span key={s.node.id}>{i > 0 && ", "}<Link href={`/cmdb/${s.node.id}`} className="text-brand underline"><bdi>{s.node.name}</bdi></Link></span>
                ))}</>}
              </li>
              <li>{t("people", { count: blast.peopleCount, assets: blast.assetUsers })}</li>
              {departments.length > 0 && <li>{t("departments")} {departments.map((d, i) => <span key={d.node.id}>{i > 0 && ", "}<Localized text={d.node.localizedName} locale={locale} /></span>)}</li>}
              {groups.length > 0 && <li>{t("customerGroups")} {groups.map((g, i) => <span key={g.node.id}>{i > 0 && ", "}<Localized text={g.node.localizedName} locale={locale} /></span>)}</li>}
              {blast.impact.truncated && <li className="text-warn">{t("truncated")}</li>}
            </ul>
          )}
        </div>
      </div>

      {!compact && (
        <div className="space-y-2" data-testid="what-if-similar">
          <h3 className="font-semibold">{t("similar")}</h3>
          {whatIf.similar.length === 0 ? <p className="text-sm">{t("noSimilar")}</p> : (
            <ul className="space-y-2">
              {whatIf.similar.map((s) => (
                <li key={s.id} className="rounded-md bg-surface-muted p-2 text-sm">
                  <Link href={`/changes/${s.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{s.number}</Link>{" "}
                  <span dir="auto">{s.title}</span>{" "}
                  <span className={s.outcome === "Successful" ? "font-semibold text-ok" : "font-semibold text-warn"}>{tOutcomes(s.outcome)}</span>
                  <span className="block text-ink-muted">{t("why")} {s.reasons.map((r) => t(`reasons.${r}`)).join(", ")}</span>
                  {s.reviewNotes && <span className="block whitespace-pre-wrap" dir="auto">{s.reviewNotes}</span>}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <div className="space-y-2" data-testid="what-if-explanation">
        <h3 className="font-semibold">{t("explanation")}</h3>
        {whatIf.explanation ? (
          <p className="whitespace-pre-wrap" lang={whatIf.explanation.language} dir="auto">{whatIf.explanation.text}</p>
        ) : <p className="text-sm text-ink-muted">{aiAvailable ? t("noExplanation") : t("aiOff")}</p>}
        {aiAvailable && (
          <div role="group" aria-label={t("explain")} className="flex flex-wrap gap-2">
            {(["en", "ar"] as const).map((language) => (
              <button key={language} type="button" className={secondaryButtonClass} disabled={busy}
                onClick={() => void run(async () => setWhatIf(await postJson<WhatIf>(`/v1/changes/${whatIf.changeId}/what-if/explain`, { language }, csrfToken)))}>
                {t(`explainIn.${language}`)}
              </button>
            ))}
          </div>
        )}
        <p className="text-sm text-ink-muted">{t("aiNote")}</p>
      </div>
      <ChangeStatusLine status={status} />
    </section>
  );
}
