"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Localized } from "@/components/directory/common";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import type { RequestTask } from "@/lib/catalog";
import { deleteRequest, postJson } from "@/lib/client-api";
import type { ChangeComment, ChangeConflict, ChangeLink } from "@/lib/changes";
import type { NamedRef } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import { ChangeStageBadge, ChangeStatusLine, useSaver } from "./common";

/** Other changes on the same items in an overlapping window, and blackout periods (task H3), shown before submission and to approvers. */
export function ChangeConflicts({ conflicts, display }: { conflicts: ChangeConflict[]; display: DateDisplayOptions }) {
  const t = useTranslations("changes.conflicts");
  const locale = useLocale() as Locale;
  const id = useId();
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="change-conflicts">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {conflicts.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2">
          {conflicts.map((c, i) => (
            <li key={i} className="rounded-md border border-warn p-2">
              <p className="font-semibold text-warn">{t(`kinds.${c.kind}`)}</p>
              {c.kind === "change" && c.changeId ? (
                <p>
                  <Link href={`/changes/${c.changeId}`} className="font-mono font-semibold text-brand underline" dir="ltr">{c.changeNumber}</Link>{" "}
                  <span dir="auto">{c.changeTitle}</span> {c.changeStage && <ChangeStageBadge stage={c.changeStage} />}
                </p>
              ) : (
                <p>{c.blackoutName ? <Localized text={c.blackoutName} locale={locale} /> : null}</p>
              )}
              <p className="text-sm">{formatDateTime(new Date(c.start), display)} – {formatDateTime(new Date(c.end), display)}</p>
              {c.items.length > 0 && (
                <p className="text-sm">{t("items")}{" "}
                  {c.items.map((item, j) => (
                    <span key={item.id}>{j > 0 && ", "}<Link href={`/cmdb/${item.id}`} className="text-brand underline"><bdi dir="ltr">{item.number}</bdi> <bdi>{item.name}</bdi></Link></span>
                  ))}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** The change's tasks for each team (C4's task model): they wait until implementation starts; tasks that have not opened can be removed. */
export function ChangeTasks({ changeId, initial, groups, canEdit, csrfToken, display }: {
  changeId: string;
  initial: RequestTask[];
  groups: NamedRef[];
  canEdit: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("changes.tasks");
  const tStates = useTranslations("catalog.tasks.states");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [tasks, setTasks] = useState(initial);
  const [en, setEn] = useState("");
  const [ar, setAr] = useState("");
  const [groupId, setGroupId] = useState("");
  const [parallel, setParallel] = useState(false);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="change-tasks">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {tasks.length === 0 ? <p>{t("none")}</p> : (
        <ol className="space-y-2">
          {tasks.map((task) => (
            <li key={task.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md bg-surface-muted p-2">
              <span>
                <span className="font-semibold"><Localized text={task.name} locale={locale} /></span>{" "}
                <span className="text-sm text-ink-muted">
                  {t("stage", { stage: task.stage + 1 })} · {tStates.has(task.state) ? tStates(task.state) : task.state}
                  {task.group && <> · <Localized text={task.group.name} locale={locale} /></>}
                  {task.assignee && <> · {task.assignee.displayName}</>}
                  {task.dueAt && <> · {t("due", { date: formatDateTime(new Date(task.dueAt), display) })}</>}
                </span>
              </span>
              {canEdit && task.state === "Waiting" && (
                <button type="button" className={secondaryButtonClass} disabled={busy}
                  aria-label={t("remove", { name: inLanguage(task.name, locale).value })}
                  onClick={() => void run(async () => {
                    await deleteRequest(`/v1/changes/${changeId}/tasks/${task.id}`, csrfToken);
                    setTasks((current) => current.filter((x) => x.id !== task.id));
                  })}>{t("removeShort")}</button>
              )}
            </li>
          ))}
        </ol>
      )}
      <p className="text-sm text-ink-muted"><Link href="/tasks" className="text-brand underline">{t("myTasks")}</Link></p>
      {canEdit && (
        <form className="space-y-2" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const list = await postJson<RequestTask[]>(`/v1/changes/${changeId}/tasks`,
              { name: { en: en || null, ar: ar || null }, groupId: groupId || null, parallelWithPrevious: parallel }, csrfToken);
            setTasks(list);
            setEn("");
            setAr("");
            router.refresh();
          });
        }}>
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("add")}</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              <label className="space-y-1 text-sm"><span className="block">{t("nameEn")}</span>
                <input value={en} onChange={(e) => setEn(e.target.value)} maxLength={150} lang="en" dir="ltr" className={inputClass} /></label>
              <label className="space-y-1 text-sm"><span className="block">{t("nameAr")}</span>
                <input value={ar} onChange={(e) => setAr(e.target.value)} maxLength={150} lang="ar" dir="rtl" className={inputClass} /></label>
              <label className="space-y-1 text-sm"><span className="block">{t("group")}</span>
                <select value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass}>
                  <option value="">{t("changeGroup")}</option>
                  {groups.map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
                </select></label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={parallel} onChange={(e) => setParallel(e.target.checked)} className="size-5 accent-brand" disabled={tasks.length === 0} />
                {t("parallel")}
              </label>
            </div>
            <button type="submit" className={buttonClass} disabled={busy || (!en.trim() && !ar.trim())}>{t("addButton")}</button>
          </fieldset>
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}

/** Problems the change fixes and incidents it caused, linked by number. */
export function ChangeLinks({ changeId, initial, canEdit, csrfToken }: { changeId: string; initial: ChangeLink[]; canEdit: boolean; csrfToken: string }) {
  const t = useTranslations("changes.links");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [links, setLinks] = useState(initial);
  const [number, setNumber] = useState("");
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="change-links">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {links.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-1">
          {links.map((l) => (
            <li key={l.id} className="flex flex-wrap items-center gap-2">
              <span className="text-sm text-ink-muted">{t(`kinds.${l.kind}`)}</span>
              <Link href={l.type === "Problem" ? `/problems/${l.id}` : `/incidents/${l.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{l.number}</Link>
              <span dir="auto">{l.title}</span>
              {canEdit && (
                <button type="button" className="text-sm font-semibold text-brand underline" disabled={busy} aria-label={t("remove", { number: l.number })}
                  onClick={() => void run(async () => {
                    await deleteRequest(`/v1/changes/${changeId}/links/${l.id}`, csrfToken);
                    setLinks((current) => current.filter((x) => x.id !== l.id));
                  })}>{t("removeShort")}</button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canEdit && (
        <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            setLinks(await postJson<ChangeLink[]>(`/v1/changes/${changeId}/links`, { number }, csrfToken));
            setNumber("");
          });
        }}>
          <label className="space-y-1 text-sm"><span className="block">{t("number")}</span>
            <input value={number} onChange={(e) => setNumber(e.target.value)} maxLength={30} dir="ltr" className={inputClass} placeholder="PRB-000123" /></label>
          <button type="submit" className={secondaryButtonClass} disabled={busy || !number.trim()}>{t("link")}</button>
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}

/** The change's internal notes. */
export function ChangeNotes({ changeId, initial, canWrite, csrfToken, display }: {
  changeId: string;
  initial: ChangeComment[];
  canWrite: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("changes.notes");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [notes, setNotes] = useState(initial);
  const [body, setBody] = useState("");
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="change-notes">
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
            const note = await postJson<ChangeComment>(`/v1/changes/${changeId}/comments`, { body }, csrfToken);
            setNotes((current) => [...current, note]);
            setBody("");
          });
        }}>
          <label htmlFor={`${id}-body`} className="block font-semibold">{t("add")}</label>
          <textarea id={`${id}-body`} value={body} onChange={(e) => setBody(e.target.value)} rows={3} maxLength={20000} dir="auto" className={inputClass} />
          <button type="submit" className={buttonClass} disabled={busy || !body.trim()}>{t("submit")}</button>
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}
