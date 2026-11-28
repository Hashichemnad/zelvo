"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Localized, primaryButton, smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { StateBadge } from "@/components/incidents/StateBadge";
import { Link, useRouter } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { suggestionTitle, type ProblemDetectionRun, type ProblemSuggestion } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

/** Incidents shown before "Show all". */
const shownIncidents = 8;

/**
 * One suggested problem (task H2): why Zelvo suggests it (similar incidents with the AI's reason
 * and confidence, or a trend against its usual level), the incidents named, and for problem
 * managers "Raise the problem" (title can be changed) or "Dismiss" with a reason.
 */
export function SuggestionCard({ suggestion: s, display, csrfToken }: { suggestion: ProblemSuggestion; display: DateDisplayOptions; csrfToken: string }) {
  const t = useTranslations("problems.suggestions");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [title, setTitle] = useState(suggestionTitle(s, locale));
  const [reason, setReason] = useState("");
  const headingLanguage = s.kind === "SimilarIncidents" && s.language ? s.language : locale;
  const number = (n: number) => formatNumber(n, display.locale, display.digits);
  const date = (iso: string) => formatDateTime(new Date(iso), display);
  const subject = s.configurationItem ? `${s.configurationItem.number} ${s.configurationItem.name}` : null;

  return (
    <article aria-labelledby={`${id}-title`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-suggestion">
      <div className="flex flex-wrap items-center gap-2">
        <span className="inline-block rounded-full border border-brand px-2 text-xs font-semibold text-brand">{t(`kinds.${s.kind}`)}</span>
        {s.dimension && <span className="inline-block rounded-full border border-line px-2 text-xs font-semibold">{t(`dimensions.${s.dimension}`)}</span>}
      </div>
      <h2 id={`${id}-title`} className="text-xl font-bold" lang={headingLanguage} dir="auto">{suggestionTitle(s, locale)}</h2>
      {s.kind === "SimilarIncidents" ? (
        <>
          {s.reason && <p lang={s.language ?? undefined} dir="auto">{s.reason}</p>}
          <p>
            {t("similarFacts", { count: s.incidentCount, from: date(s.windowStart), to: date(s.windowEnd) })}
            {s.confidence !== null && <> {t("confidence", { value: number(s.confidence) })}</>}
          </p>
          <p className="text-sm text-ink-muted">{t("aiNote")}</p>
        </>
      ) : (
        <>
          <p>
            {t("trendSubject")}{" "}
            {subject ? <span dir="auto">{subject}</span> : s.category ? <span><Localized text={s.category.path} locale={locale} /></span> : "—"}
          </p>
          <p>{t("trendFacts", { count: s.incidentCount, recent: number(s.incidentCount), usual: number(s.baseline ?? 0) })}</p>
        </>
      )}

      {s.incidents.length > 0 && (
        <div>
          <h3 className="font-semibold">{t("incidents", { count: s.incidentCount })}</h3>
          <IncidentList suggestion={s} display={display} />
        </div>
      )}

      {s.status === "Accepted" && s.problem && (
        <p>
          {t.rich("accepted", {
            name: s.decidedBy?.displayName ?? "—",
            date: s.decidedAt ? date(s.decidedAt) : "—",
            link: () => <Link href={`/problems/${s.problem!.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{s.problem!.number}</Link>,
          })}
        </p>
      )}
      {s.status === "Dismissed" && (
        <p>
          {t("dismissed", { name: s.decidedBy?.displayName ?? "—", date: s.decidedAt ? date(s.decidedAt) : "—" })}{" "}
          <span dir="auto">{s.dismissReason}</span>
        </p>
      )}

      {s.status === "Open" && (
        <div className="grid gap-4 lg:grid-cols-2">
          <form className="space-y-2" aria-label={t("acceptLabel")} onSubmit={async (e) => {
            e.preventDefault();
            const created = await run(() => postJson<{ id: string }>(`/v1/problem-suggestions/${s.id}/accept`, { title: title.trim() || undefined }, csrfToken, { "If-Match": s.etag }));
            if (created) router.push(`/problems/${created.id}`);
          }}>
            <label className="block space-y-1">
              <span className="block font-semibold">{t("problemTitle")}</span>
              <input value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={300} dir="auto" className={fieldInput} />
            </label>
            <button type="submit" className={primaryButton} disabled={busy || !title.trim()}>{t("accept")}</button>
          </form>
          <form className="space-y-2" aria-label={t("dismissLabel")} onSubmit={async (e) => {
            e.preventDefault();
            const done = await run(() => postJson(`/v1/problem-suggestions/${s.id}/dismiss`, { reason: reason.trim() }, csrfToken, { "If-Match": s.etag }));
            if (done) router.refresh();
          }}>
            <label className="block space-y-1">
              <span className="block font-semibold">{t("dismissReason")}</span>
              <textarea value={reason} onChange={(e) => setReason(e.target.value)} required maxLength={1000} rows={2} dir="auto" className={fieldInput} />
            </label>
            <p className="text-sm text-ink-muted">{t("dismissHelp")}</p>
            <button type="submit" className={smallButton} disabled={busy || !reason.trim()}>{t("dismiss")}</button>
          </form>
          <div className="lg:col-span-2"><ProblemStatusLine status={status} /></div>
        </div>
      )}
    </article>
  );
}

function IncidentList({ suggestion: s, display }: { suggestion: ProblemSuggestion; display: DateDisplayOptions }) {
  const t = useTranslations("problems.suggestions");
  const [all, setAll] = useState(false);
  const shown = all ? s.incidents : s.incidents.slice(0, shownIncidents);
  return (
    <>
      <ul className="mt-1 space-y-1">
        {shown.map((i) => (
          <li key={i.id} className="flex flex-wrap items-center gap-2">
            <Link href={`/incidents/${i.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{i.number}</Link>
            <span dir="auto" className="min-w-0 break-words">{i.title}</span>
            <StateBadge state={i.state} />
            <time dateTime={i.createdAt} className="text-sm text-ink-muted">{formatDateTime(new Date(i.createdAt), display)}</time>
          </li>
        ))}
      </ul>
      {s.incidents.length > shownIncidents && (
        <button type="button" className={`${smallButton} mt-2`} aria-expanded={all} onClick={() => setAll(!all)}>
          {all ? t("showFewer") : t("showAll", { count: s.incidents.length })}
        </button>
      )}
    </>
  );
}

/** "Look now": runs detection for the tenant at once and reloads the queue. */
export function DetectNowButton({ csrfToken }: { csrfToken: string }) {
  const t = useTranslations("problems.suggestions");
  const router = useRouter();
  const { status, run, busy } = useSaver();
  const [result, setResult] = useState<ProblemDetectionRun | null>(null);
  return (
    <div className="space-y-1">
      <button type="button" className={smallButton} disabled={busy} onClick={async () => {
        const found = await run(() => postJson<ProblemDetectionRun>("/v1/problem-suggestions/detect", {}, csrfToken));
        if (found) {
          setResult(found);
          router.refresh();
        }
      }}>{t("detectNow")}</button>
      {result ? (
        <p role="status" aria-live="polite">
          {t("detected", { count: result.similar + result.trends })}
          {result.similarSkipped === "ai-off" && <> {t("similarOff")}</>}
          {result.similarSkipped === "limit-reached" && <> {t("similarLimit")}</>}
        </p>
      ) : (
        <ProblemStatusLine status={status} />
      )}
    </div>
  );
}
