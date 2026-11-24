"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "@/components/directory/common";
import { postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { RequestTask } from "@/lib/catalog";

/** Start and complete buttons of one task; completing asks for optional notes. */
function TaskActions({ task, csrfToken, onChanged }: { task: RequestTask; csrfToken: string; onChanged: (t: RequestTask) => void }) {
  const t = useTranslations("catalog.tasks");
  const locale = useLocale() as Locale;
  const id = useId();
  const [completing, setCompleting] = useState(false);
  const [notes, setNotes] = useState("");
  const { status, run, busy } = useSaver();
  if (!task.mayWork) return null;
  const name = (locale === "ar" ? task.name.ar : task.name.en) ?? task.name.en ?? task.name.ar ?? task.key;
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2">
        {task.state === "Open" && (
          <button type="button" className={smallButton} disabled={busy} aria-label={t("startNamed", { name })}
            onClick={() => void run(async () => onChanged(await postJson<RequestTask>(`/v1/request-tasks/${task.id}/start`, {}, csrfToken)))}>{t("start")}</button>
        )}
        {!completing && (
          <button type="button" className={smallButton} disabled={busy} aria-label={t("completeNamed", { name })} onClick={() => setCompleting(true)}>{t("complete")}</button>
        )}
      </div>
      {completing && (
        <form className="space-y-2" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => { onChanged(await postJson<RequestTask>(`/v1/request-tasks/${task.id}/complete`, { notes: notes.trim() || null }, csrfToken)); setCompleting(false); });
        }}>
          <label htmlFor={`${id}-notes`} className="block text-sm font-semibold">{t("notes")}</label>
          <textarea id={`${id}-notes`} rows={2} dir="auto" maxLength={4000} className={inputClass} value={notes} onChange={(e) => setNotes(e.target.value)} />
          <div className="flex gap-2">
            <button type="submit" className={primaryButton} disabled={busy}>{t("markDone")}</button>
            <button type="button" className={secondaryButton} onClick={() => setCompleting(false)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}

/** Tasks in a table: name, request, group, assignee, state and due time (overdue said in words). */
export function TaskTable({ tasks, display, csrfToken, showRequest, onChanged }: {
  tasks: RequestTask[];
  display: DateDisplayOptions;
  csrfToken: string;
  showRequest: boolean;
  onChanged: (t: RequestTask) => void;
}) {
  const t = useTranslations("catalog.tasks");
  const locale = useLocale() as Locale;
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  if (tasks.length === 0) return <p>{t("none")}</p>;
  return (
    <table className="w-full border-collapse" data-testid="task-table">
      <thead>
        <tr>
          <th scope="col" className={cell}>{t("task")}</th>
          {showRequest && <th scope="col" className={cell}>{t("request")}</th>}
          <th scope="col" className={cell}>{t("group")}</th>
          <th scope="col" className={cell}>{t("assignee")}</th>
          <th scope="col" className={cell}>{t("state")}</th>
          <th scope="col" className={cell}>{t("due")}</th>
          <th scope="col" className={cell}>{t("actions")}</th>
        </tr>
      </thead>
      <tbody>
        {tasks.map((task) => (
          <tr key={task.id} data-testid="task-row">
            <td className={cell}>
              <span className="font-semibold"><Localized text={task.name} locale={locale} /></span>
              {!showRequest && <span className="block text-sm text-ink-muted">{t("stageLine", { number: task.stage + 1 })}</span>}
              {task.notes && <span className="block text-sm" dir="auto">{task.notes}</span>}
            </td>
            {showRequest && (
              <td className={cell}>
                <Link href={`/requests/${task.requestId}`} className="font-mono font-semibold text-brand underline" dir="ltr">{task.requestNumber}</Link>
                <span className="block text-sm" dir="auto">{task.requestTitle}</span>
              </td>
            )}
            <td className={cell}>{task.group ? <Localized text={task.group.name} locale={locale} /> : "—"}</td>
            <td className={cell}>{task.assignee?.displayName ?? t("nobody")}</td>
            <td className={cell}>{t(`states.${task.state}`)}</td>
            <td className={cell}>
              {task.dueAt ? formatDateTime(new Date(task.dueAt), display) : "—"}
              {task.overdue && <span className="block font-semibold text-bad">{t("overdue")}</span>}
            </td>
            <td className={cell}><TaskActions task={task} csrfToken={csrfToken} onChanged={onChanged} /></td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** A request's tasks on the staff request page (task C4); finishing one refreshes the request. */
export function RequestTasksPanel({ tasks, display, csrfToken }: { tasks: RequestTask[]; display: DateDisplayOptions; csrfToken: string }) {
  const t = useTranslations("catalog.tasks");
  const router = useRouter();
  const [list, setList] = useState(tasks);
  if (tasks.length === 0) return null;
  return (
    <section aria-labelledby="request-tasks" className="space-y-2" data-testid="request-tasks">
      <h2 id="request-tasks" className="text-xl font-semibold">{t("title")}</h2>
      <TaskTable tasks={list} display={display} csrfToken={csrfToken} showRequest={false}
        onChanged={(changed) => { setList((l) => l.map((x) => (x.id === changed.id ? changed : x))); router.refresh(); }} />
    </section>
  );
}

/** My tasks (task C4): the caller's own, and their groups' open tasks. */
export function MyTasks({ mine, groups, display, csrfToken }: { mine: RequestTask[]; groups: RequestTask[]; display: DateDisplayOptions; csrfToken: string }) {
  const t = useTranslations("catalog.tasks");
  const [own, setOwn] = useState(mine);
  const [queue, setQueue] = useState(groups);
  const replace = (changed: RequestTask) => {
    setOwn((l) => (l.some((x) => x.id === changed.id) ? l.map((x) => (x.id === changed.id ? changed : x)) : [...l, changed]).filter((x) => x.state === "Open" || x.state === "InProgress"));
    setQueue((l) => l.map((x) => (x.id === changed.id ? changed : x)).filter((x) => x.state === "Open" || x.state === "InProgress"));
  };
  return (
    <div className="space-y-8">
      <section aria-labelledby="tasks-mine" className="space-y-2">
        <h2 id="tasks-mine" className="text-xl font-semibold">{t("mine")}</h2>
        <TaskTable tasks={own} display={display} csrfToken={csrfToken} showRequest onChanged={replace} />
      </section>
      <section aria-labelledby="tasks-groups" className="space-y-2">
        <h2 id="tasks-groups" className="text-xl font-semibold">{t("myGroups")}</h2>
        <TaskTable tasks={queue} display={display} csrfToken={csrfToken} showRequest onChanged={replace} />
      </section>
    </div>
  );
}
