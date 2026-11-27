"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import { deleteRequest, getJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale, type TicketState } from "@/lib/incidents";
import type { ProblemArticle, ProblemIncident } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

/** The incidents linked to a problem: opened, linked by number and unlinked by people with problems.edit. */
export function ProblemIncidents({ problemId, initial, canEdit, csrfToken, display }: {
  problemId: string;
  initial: ProblemIncident[];
  canEdit: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("problems.incidents");
  const tStates = useTranslations("incidents.states");
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [links, setLinks] = useState(initial);
  const [number, setNumber] = useState("");
  const [missing, setMissing] = useState(false);

  async function add() {
    setMissing(false);
    const found = await getJson<{ items: { id: string; number: string }[] }>(`/v1/incidents?number=${encodeURIComponent(number.trim())}&pageSize=1`);
    const incident = found.items[0];
    if (!incident) {
      setMissing(true);
      return;
    }
    await run(async () => {
      setLinks(await postJson<ProblemIncident[]>(`/v1/problems/${problemId}/incidents`, { incidentIds: [incident.id] }, csrfToken));
      setNumber("");
      router.refresh();
    });
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-incidents">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {links.length === 0 ? <p>{t("none")}</p> : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("heading")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("number")}</th>
                <th scope="col" className={cellClass}>{t("titleColumn")}</th>
                <th scope="col" className={cellClass}>{t("state")}</th>
                <th scope="col" className={cellClass}>{t("assignee")}</th>
                <th scope="col" className={cellClass}>{t("linked")}</th>
                {canEdit && <td className={cellClass} />}
              </tr>
            </thead>
            <tbody>
              {links.map((l) => (
                <tr key={l.id}>
                  <td className={cellClass}>
                    <Link href={`/incidents/${l.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{l.number}</Link>
                  </td>
                  <td className={cellClass} dir="auto">{l.title}</td>
                  <td className={cellClass}>{tStates(l.state as TicketState)}</td>
                  <td className={cellClass}>{l.assignee?.displayName ?? "—"}</td>
                  <td className={cellClass}>{formatDateTime(new Date(l.linkedAt), display)}</td>
                  {canEdit && (
                    <td className={cellClass}>
                      <button type="button" className={smallButton} disabled={busy}
                        onClick={() => void run(async () => {
                          await deleteRequest(`/v1/problems/${problemId}/incidents/${l.id}`, csrfToken);
                          setLinks((current) => current.filter((c) => c.id !== l.id));
                          router.refresh();
                        })}>
                        {t("remove", { number: l.number })}
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canEdit && (
        <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => (e.preventDefault(), void add())}>
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("add")}</span>
            <input value={number} onChange={(e) => setNumber(e.target.value)} maxLength={30} dir="ltr" className={`${inputClass} w-48`} />
          </label>
          <button type="submit" className={secondaryButtonClass} disabled={busy || !number.trim()}>{t("addButton")}</button>
        </form>
      )}
      {missing && <p role="alert">{t("notFound")}</p>}
      <ProblemStatusLine status={status} />
    </section>
  );
}

/** The knowledge articles linked to a problem. */
export function ProblemArticles({ problemId, initial, canEdit, csrfToken }: { problemId: string; initial: ProblemArticle[]; canEdit: boolean; csrfToken: string }) {
  const t = useTranslations("problems.articles");
  const tStates = useTranslations("problems.knownError.articleStates");
  const locale = useLocale() as Locale;
  const id = useId();
  const { status, run, busy } = useSaver();
  const [links, setLinks] = useState(initial);
  const [number, setNumber] = useState("");

  async function add() {
    setLinks(await postJson<ProblemArticle[]>(`/v1/problems/${problemId}/articles`, { number: number.trim() }, csrfToken));
    setNumber("");
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-articles">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {links.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-1">
          {links.map((a) => {
            const title = inLanguage(a.title, locale);
            return (
              <li key={a.id} className="flex flex-wrap items-center gap-2">
                <Link href={`/knowledge/${a.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                  <span dir="ltr" className="font-mono">{a.number}</span> <span lang={title.lang} dir={title.dir}>{title.value}</span>
                </Link>
                <span className="text-sm text-ink-muted">{tStates(a.state)}</span>
                {canEdit && (
                  <button type="button" className={smallButton} disabled={busy}
                    onClick={() => void run(async () => {
                      await deleteRequest(`/v1/problems/${problemId}/articles/${a.id}`, csrfToken);
                      setLinks((current) => current.filter((c) => c.id !== a.id));
                    })}>
                    {t("remove", { number: a.number })}
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {canEdit && (
        <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => (e.preventDefault(), void run(add))}>
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("add")}</span>
            <input value={number} onChange={(e) => setNumber(e.target.value)} maxLength={30} dir="ltr" className={`${inputClass} w-48`} />
          </label>
          <button type="submit" className={secondaryButtonClass} disabled={busy || !number.trim()}>{t("addButton")}</button>
        </form>
      )}
      <ProblemStatusLine status={status} />
    </section>
  );
}
