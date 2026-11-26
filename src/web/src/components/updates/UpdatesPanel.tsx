"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { ApiError, deleteRequest, getJson, postForm, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type CalendarSystem, type DigitSystem } from "@/lib/format";
import { isActive, localToIso, megabytes, packageProblemKey, type UpdateInstall, type UpdatesState } from "@/lib/updates";

/**
 * Settings → Updates (module 13, task F4): the installed version and the updater, importing a
 * signed package (upload, or a file carried in on USB from the inbox) with every check explained,
 * installing it now or in a maintenance window with a notice for users, the install's steps as
 * they happen (the page keeps asking while Zelvo restarts) and the history.
 */
export function UpdatesPanel({ initial, csrfToken, calendar, digits, timeZone }: {
  initial: UpdatesState;
  csrfToken: string;
  calendar: CalendarSystem;
  digits: DigitSystem;
  timeZone: string;
}) {
  const t = useTranslations("updates");
  const locale = useLocale();
  const id = useId();
  const [state, setState] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [schedule, setSchedule] = useState<Record<string, { when: string; notice: string }>>({});
  const [unreachable, setUnreachable] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const when = (iso: string | null) => (iso ? formatDateTime(new Date(iso), { locale, calendar, digits, timeZone }) : "—");
  const n = (v: number) => formatNumber(v, locale, digits);
  const active = state.installs.find(isActive);

  // While an install is scheduled or running the screen follows it; Zelvo may be down for a while.
  useEffect(() => {
    if (!active) return;
    const timer = setInterval(() => {
      getJson<UpdatesState>("/v1/updates").then((next) => { setState(next); setUnreachable(false); }).catch(() => setUnreachable(true));
    }, 3000);
    return () => clearInterval(timer);
  }, [active]);

  async function refresh() {
    setState(await getJson<UpdatesState>("/v1/updates"));
  }

  function refused(error: unknown) {
    const code = error instanceof ApiError ? error.code : null;
    setMessage({ ok: false, text: t(`problems.${packageProblemKey(code)}`) });
  }

  async function run(action: () => Promise<void>, ok: string) {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      await refresh();
      setMessage({ ok: true, text: ok });
    } catch (error) {
      refused(error);
    } finally {
      setBusy(false);
    }
  }

  async function upload(file: File) {
    const form = new FormData();
    form.append("file", file, file.name);
    await run(async () => {
      const { status, body } = await postForm<{ code?: string }>("/v1/updates/packages", form, csrfToken, [413, 422]);
      if (status !== 201) throw new ApiError("upload", status, body.code ?? null);
    }, t("imported"));
    if (fileInput.current) fileInput.current.value = "";
  }

  function install(packageId: string, now: boolean) {
    const chosen = schedule[packageId];
    const body = now ? {} : { scheduledFor: localToIso(chosen?.when ?? ""), noticeFrom: localToIso(chosen?.notice ?? "") };
    if (!now && !body.scheduledFor) {
      setMessage({ ok: false, text: t("chooseTime") });
      return;
    }
    if (!window.confirm(t(now ? "confirmNow" : "confirmSchedule"))) return;
    void run(async () => { await postJson(`/v1/updates/packages/${packageId}/install`, body, csrfToken); }, t(now ? "started" : "scheduled"));
  }

  return (
    <div className="space-y-8" data-testid="updates-panel">
      <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t("installedVersion")}</dt>
        <dd><bdi dir="ltr" data-testid="installed-version">{state.installedVersion}</bdi></dd>
        <dt className="font-semibold">{t("updater")}</dt>
        <dd className={state.updaterOnline ? "text-ok" : "text-warn"} data-testid="updater-status">
          {state.updaterOnline ? t("updaterOnline") : t("updaterOffline")}
        </dd>
      </dl>

      {message && <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-danger"} data-testid="updates-message">{message.text}</p>}
      {unreachable && <p role="status" className="text-warn">{t("restarting")}</p>}

      {active && <InstallProgress install={active} when={when} n={n} onCancel={active.state === "Scheduled"
        ? () => void run(async () => { await deleteRequest(`/v1/updates/installs/${active.id}`, csrfToken); }, t("cancelled")) : undefined} busy={busy} />}

      <section aria-labelledby={`${id}-import`} className="space-y-3">
        <h3 id={`${id}-import`} className="text-xl font-bold">{t("importTitle")}</h3>
        <p className="max-w-2xl text-sm">{t("importIntro")}</p>
        <label htmlFor={`${id}-file`} className="block font-semibold">{t("uploadLabel", { size: n(megabytes(state.maxPackageBytes)) })}</label>
        <input id={`${id}-file`} ref={fileInput} type="file" accept=".zpkg" disabled={busy} data-testid="package-file"
          className="block text-sm file:me-3 file:rounded-md file:border file:border-line file:bg-surface file:px-3 file:py-2"
          onChange={(e) => { const file = e.target.files?.[0]; if (file) void upload(file); }} />
        <h4 className="font-semibold">{t("inboxTitle")}</h4>
        {state.inbox.length === 0 ? <p className="text-sm text-ink-muted">{t("inboxEmpty")}</p> : (
          <ul className="space-y-2" data-testid="inbox">
            {state.inbox.map((f) => (
              <li key={f.fileName} className="flex flex-wrap items-center gap-3">
                <bdi dir="ltr">{f.fileName}</bdi>
                <span className="text-sm text-ink-muted">{t("megabytes", { size: n(megabytes(f.sizeBytes)) })}</span>
                <button type="button" className={fieldButton} disabled={busy}
                  onClick={() => void run(async () => { await postJson(`/v1/updates/inbox/${encodeURIComponent(f.fileName)}/import`, undefined, csrfToken); }, t("imported"))}>
                  {t("importFile")}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby={`${id}-packages`} className="space-y-3">
        <h3 id={`${id}-packages`} className="text-xl font-bold">{t("packagesTitle")}</h3>
        {state.packages.length === 0 ? <p className="text-sm text-ink-muted">{t("noPackages")}</p> : state.packages.map((p) => (
          <article key={p.id} className="space-y-2 rounded-lg border border-line p-4" data-testid={`package-${p.version}`} aria-labelledby={`${id}-pkg-${p.id}`}>
            <h4 id={`${id}-pkg-${p.id}`} className="text-lg font-bold">
              <bdi dir="ltr">{p.version}</bdi> · {t(`types.${p.type}`)} · {t(`packageStatus.${p.status}`)}
            </h4>
            <p className="whitespace-pre-line">{(locale === "ar" ? p.releaseNotes.ar ?? p.releaseNotes.en : p.releaseNotes.en ?? p.releaseNotes.ar) ?? ""}</p>
            <p className="text-sm text-ink-muted">
              {t("importedAt", { when: when(p.importedAt) })} · {t("megabytes", { size: n(megabytes(p.sizeBytes)) })} · {t("expected", { minutes: n(p.expectedMinutes) })}
            </p>
            {p.status === "Imported" && !p.installable && <p className="text-warn" role="note">{t(`problems.${packageProblemKey(p.problem)}`)}</p>}
            {p.status === "Imported" && p.installable && !active && (
              <div className="flex flex-wrap items-end gap-3">
                <button type="button" className={primaryAction} disabled={busy} onClick={() => install(p.id, true)} data-testid="install-now">{t("installNow")}</button>
                <div className="space-y-1">
                  <label htmlFor={`${id}-when-${p.id}`} className="block text-sm">{t("windowLabel")}</label>
                  <input id={`${id}-when-${p.id}`} type="datetime-local" className={fieldInput} value={schedule[p.id]?.when ?? ""}
                    onChange={(e) => setSchedule({ ...schedule, [p.id]: { notice: schedule[p.id]?.notice ?? "", when: e.target.value } })} />
                </div>
                <div className="space-y-1">
                  <label htmlFor={`${id}-notice-${p.id}`} className="block text-sm">{t("noticeLabel")}</label>
                  <input id={`${id}-notice-${p.id}`} type="datetime-local" className={fieldInput} value={schedule[p.id]?.notice ?? ""}
                    onChange={(e) => setSchedule({ ...schedule, [p.id]: { when: schedule[p.id]?.when ?? "", notice: e.target.value } })} />
                </div>
                <button type="button" className={fieldButton} disabled={busy} onClick={() => install(p.id, false)} data-testid="install-schedule">{t("schedule")}</button>
              </div>
            )}
            {p.status === "Imported" && !active && (
              <button type="button" className="text-sm font-semibold text-danger underline-offset-4 hover:underline" disabled={busy}
                onClick={() => { if (window.confirm(t("confirmRemove"))) void run(async () => { await deleteRequest(`/v1/updates/packages/${p.id}`, csrfToken); }, t("removed")); }}>
                {t("remove")}
              </button>
            )}
          </article>
        ))}
      </section>

      <section aria-labelledby={`${id}-history`} className="space-y-2">
        <h3 id={`${id}-history`} className="text-xl font-bold">{t("historyTitle")}</h3>
        {state.installs.length === 0 ? <p className="text-sm text-ink-muted">{t("noHistory")}</p> : (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b border-line">
                  <th scope="col" className="py-2 text-start">{t("columns.versions")}</th>
                  <th scope="col" className="py-2 text-start">{t("columns.state")}</th>
                  <th scope="col" className="py-2 text-start">{t("columns.when")}</th>
                </tr>
              </thead>
              <tbody>
                {state.installs.map((i) => (
                  <tr key={i.id} className="border-b border-line align-top">
                    <td className="py-2"><bdi dir="ltr">{i.fromVersion} → {i.toVersion}</bdi></td>
                    <td className="py-2">{t(`states.${i.state}`)}{i.error && <span className="block text-ink-muted"><bdi dir="ltr">{i.error}</bdi></span>}</td>
                    <td className="py-2">{when(i.finishedAt ?? i.startedAt ?? i.scheduledFor)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="text-sm text-ink-muted">{t("auditNote")}</p>
      </section>
    </div>
  );
}

function InstallProgress({ install, when, n, onCancel, busy }: {
  install: UpdateInstall;
  when: (iso: string | null) => string;
  n: (v: number) => string;
  onCancel?: () => void;
  busy: boolean;
}) {
  const t = useTranslations("updates");
  const id = useId();
  return (
    <section aria-labelledby={`${id}-progress`} className="space-y-3 rounded-lg border-2 border-brand p-4" data-testid="install-progress">
      <h3 id={`${id}-progress`} className="text-xl font-bold">
        {t(install.state === "Scheduled" ? "scheduledTitle" : "runningTitle", { version: install.toVersion })}
      </h3>
      {install.state === "Scheduled" && (
        <p>{t("scheduledFor", { when: when(install.scheduledFor), notice: when(install.noticeFrom), minutes: n(install.expectedMinutes) })}</p>
      )}
      {install.steps.length > 0 && (
        <ol className="space-y-1" aria-live="polite">
          {install.steps.map((s) => (
            <li key={s.key} className={s.state === "Failed" ? "text-danger" : s.state === "Done" ? "text-ok" : ""}>
              {t(`steps.${s.key}`)}: {t(`stepStates.${s.state}`)}
            </li>
          ))}
        </ol>
      )}
      {onCancel && <button type="button" className={fieldButton} disabled={busy} onClick={onCancel} data-testid="install-cancel">{t("cancel")}</button>}
    </section>
  );
}
