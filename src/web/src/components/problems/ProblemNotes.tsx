"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { ProblemComment, TimelineEntry } from "@/lib/problems";
import { Link } from "@/i18n/navigation";
import { ProblemStatusLine, useSaver } from "./common";

/** A problem's internal notes (problems have no requester to reply to). */
export function ProblemNotes({ problemId, initial, canWrite, csrfToken, display }: {
  problemId: string;
  initial: ProblemComment[];
  canWrite: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("problems.notes");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [notes, setNotes] = useState(initial);
  const [body, setBody] = useState("");

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-notes">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {notes.length === 0 ? <p>{t("none")}</p> : (
        <ol className="space-y-2">
          {notes.map((n) => (
            <li key={n.id} className="rounded-md bg-surface-muted p-2">
              <p className="text-sm text-ink-muted">{t("by", { name: n.author.displayName ?? "—", date: formatDateTime(new Date(n.createdAt), display) })}</p>
              <p className="whitespace-pre-wrap" lang={n.language} dir="auto">{n.body}</p>
            </li>
          ))}
        </ol>
      )}
      {canWrite && (
        <form className="space-y-2" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const note = await postJson<ProblemComment>(`/v1/problems/${problemId}/comments`, { body }, csrfToken);
            setNotes((current) => [...current, note]);
            setBody("");
          });
        }}>
          <label htmlFor={`${id}-body`} className="block font-semibold">{t("add")}</label>
          <textarea id={`${id}-body`} value={body} onChange={(e) => setBody(e.target.value)} rows={3} maxLength={20000} dir="auto" className={inputClass} />
          <button type="submit" className={buttonClass} disabled={busy || !body.trim()}>{t("submit")}</button>
        </form>
      )}
      <ProblemStatusLine status={status} />
    </section>
  );
}

/** The timeline of a problem and its incidents, oldest first. */
export function ProblemTimeline({ entries, display }: { entries: TimelineEntry[]; display: DateDisplayOptions }) {
  const t = useTranslations("problems.timeline");
  const id = useId();
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-timeline">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {entries.length === 0 ? <p>{t("none")}</p> : (
        <ol className="space-y-1 border-s-2 border-line ps-4">
          {entries.map((e, i) => {
            // Message keys cannot hold dots: problem.known-error is problem_known_error.
            const label = t(`kinds.${e.kind.replace(/[.-]/g, "_")}`, { number: e.ticket.number, reference: e.reference ?? "" });
            return (
            <li key={i}>
              <time dateTime={e.at} className="text-sm text-ink-muted">{formatDateTime(new Date(e.at), display)}</time>{" "}
              {e.ticket.type === "Incident" || e.ticket.type === "Change" ? (
                <Link href={e.ticket.type === "Change" ? `/changes/${e.ticket.id}` : `/incidents/${e.ticket.id}`} className="text-brand underline underline-offset-4">{label}</Link>
              ) : (
                <span>{label}</span>
              )}
            </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}
