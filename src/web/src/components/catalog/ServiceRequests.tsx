"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Localized, useSaver } from "@/components/directory/common";
import { StateBadge } from "@/components/incidents/StateBadge";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { postJson, putJson, getJson } from "@/lib/client-api";
import { textOf, type GroupSummary } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { IncidentSettings, Locale, Person, TicketState } from "@/lib/incidents";
import type { ServiceRequest, ServiceRequestAttachment } from "@/lib/catalog";
import { CatalogStatusLine } from "./common";

/** Service requests in a table: number, service, state and when; staff also see requester and assignee. */
export function RequestTable({ requests, basePath, staff, display }: { requests: ServiceRequest[]; basePath: string; staff: boolean; display: DateDisplayOptions }) {
  const t = useTranslations("catalog.requests");
  const locale = useLocale() as Locale;
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  return (
    <table className="w-full border-collapse" data-testid="request-table">
      <thead>
        <tr>
          <th scope="col" className={cell}>{t("number")}</th>
          <th scope="col" className={cell}>{t("service")}</th>
          <th scope="col" className={cell}>{t("state")}</th>
          {staff && <th scope="col" className={cell}>{t("requester")}</th>}
          {staff && <th scope="col" className={cell}>{t("assignee")}</th>}
          <th scope="col" className={cell}>{t("created")}</th>
        </tr>
      </thead>
      <tbody>
        {requests.map((r) => (
          <tr key={r.id}>
            <td className={cell}><Link href={`${basePath}/${r.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{r.number}</Link></td>
            <td className={cell}>{r.service ? <Localized text={r.service.name} locale={locale} /> : <span dir="auto">{r.title}</span>}</td>
            <td className={cell}><StateBadge state={r.state} /></td>
            {staff && <td className={cell}>{r.requester.displayName}</td>}
            {staff && <td className={cell}>{r.assignee?.displayName ?? t("nobody")}</td>}
            <td className={cell}>{formatDateTime(new Date(r.createdAt), display)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The service's steps as the requester's journey, with the current one marked (text, not colour alone). */
export function RequestProgress({ request }: { request: ServiceRequest }) {
  const t = useTranslations("catalog.requests");
  const locale = useLocale() as Locale;
  if (request.steps.length === 0) return null;
  const current = request.currentStep;
  const percent = request.steps.length > 1 && current >= 0 ? Math.round((current / (request.steps.length - 1)) * 100) : current >= 0 ? 100 : 0;
  return (
    <section aria-labelledby="request-progress" className="space-y-2" data-testid="request-progress">
      <h2 id="request-progress" className="text-xl font-semibold">{t("progress")}</h2>
      <div role="progressbar" aria-labelledby="request-progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
        aria-valuetext={current >= 0 ? t("stepOf", { current: current + 1, total: request.steps.length, name: textOf(request.steps[current]!.name, locale) }) : t("notStarted")}
        className="h-3 w-full overflow-hidden rounded-full bg-surface-muted">
        <div className="h-full bg-brand" style={{ width: `${percent}%` }} />
      </div>
      <ol className="grid gap-2 sm:grid-flow-col sm:auto-cols-fr">
        {request.steps.map((s, i) => (
          <li key={i} aria-current={i === current ? "step" : undefined} className={`rounded-md border p-2 text-sm ${i === current ? "border-brand font-semibold" : i < current ? "border-ok" : "border-line text-ink-muted"}`}>
            <span className="sr-only">{i < current ? t("done") : i === current ? t("current") : t("upcoming")}: </span>
            <Localized text={s.name} locale={locale} />
          </li>
        ))}
      </ol>
    </section>
  );
}

/** Facts of a request: service, state, requester (and who made it for them), dates, resolution. */
export function RequestFacts({ request, display, settings, basePath }: { request: ServiceRequest; display: DateDisplayOptions; settings: IncidentSettings | null; basePath: string }) {
  const t = useTranslations("catalog.requests");
  const locale = useLocale() as Locale;
  const reason = request.onHoldReason ? settings?.onHoldReasons.find((r) => r.key === request.onHoldReason) : null;
  return (
    <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
      <div><dt className="font-semibold">{t("state")}</dt><dd><StateBadge state={request.state} />{reason && <span className="ms-2"><Localized text={reason.name} locale={locale} /></span>}</dd></div>
      {request.service && (
        <div>
          <dt className="font-semibold">{t("service")}</dt>
          <dd>{basePath === "/requests"
            ? <><Localized text={request.service.name} locale={locale} /> <span className="font-mono text-sm" dir="ltr">{request.service.code}</span></>
            : <Link href={`/catalog/${request.service.id}`} className="text-brand underline"><Localized text={request.service.name} locale={locale} /></Link>}</dd>
        </div>
      )}
      <div><dt className="font-semibold">{t("requester")}</dt><dd>{request.requester.displayName}</dd></div>
      {request.requestedBy && <div><dt className="font-semibold">{t("requestedBy")}</dt><dd>{request.requestedBy.displayName}</dd></div>}
      {basePath !== "/requests" && (
        <div><dt className="font-semibold">{t("team")}</dt><dd>{request.group ? <Localized text={request.group.name} locale={locale} /> : t("noTeamYet")}</dd></div>
      )}
      {request.assignee !== undefined && request.group !== undefined && basePath === "/requests" && (
        <div><dt className="font-semibold">{t("assignee")}</dt><dd>{request.assignee?.displayName ?? t("nobody")}{request.group && <> · <Localized text={request.group.name} locale={locale} /></>}</dd></div>
      )}
      <div><dt className="font-semibold">{t("created")}</dt><dd>{formatDateTime(new Date(request.createdAt), display)}</dd></div>
      {request.resolvedAt && <div><dt className="font-semibold">{t("resolved")}</dt><dd>{formatDateTime(new Date(request.resolvedAt), display)}</dd></div>}
      {request.description && <div className="sm:col-span-2"><dt className="font-semibold">{t("description")}</dt><dd className="whitespace-pre-line" dir="auto">{request.description}</dd></div>}
      {request.resolutionNotes && <div className="sm:col-span-2"><dt className="font-semibold">{t("resolutionNotes")}</dt><dd className="whitespace-pre-line" dir="auto">{request.resolutionNotes}</dd></div>}
    </dl>
  );
}

/** Documents and files sent with the request, each downloadable once scanned clean. */
export function RequestAttachments({ requestId, attachments, documentNames }: { requestId: string; attachments: ServiceRequestAttachment[]; documentNames: Record<string, string> }) {
  const t = useTranslations("catalog.requests");
  if (attachments.length === 0) return null;
  return (
    <section aria-labelledby="request-files" className="space-y-2">
      <h2 id="request-files" className="text-xl font-semibold">{t("files")}</h2>
      <ul className="space-y-1">
        {attachments.map((a) => (
          <li key={a.id}>
            {a.scanStatus === "Clean" || a.scanStatus === "NotScanned"
              ? <a href={`/bff/api/v1/service-requests/${requestId}/attachments/${a.id}/content`} className="text-brand underline">{a.fileName}</a>
              : <span>{a.fileName} <span className="text-sm text-ink-muted">({t(a.scanStatus === "Infected" ? "infected" : "scanning")})</span></span>}
            {a.documentKey && <span className="ms-2 text-sm text-ink-muted">{documentNames[a.documentKey] ?? a.documentKey}</span>}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** The requester reopens a resolved request. */
export function ReopenRequestButton({ requestId, etag, csrfToken }: { requestId: string; etag: string; csrfToken: string }) {
  const t = useTranslations("catalog.requests");
  const router = useRouter();
  const { status, run, busy } = useSaver();
  return (
    <div className="flex flex-wrap items-center gap-3">
      <button type="button" className={fieldButton} disabled={busy}
        onClick={() => void run(async () => { await postJson(`/v1/service-requests/${requestId}/transitions`, { state: "Reopened" }, csrfToken, { "If-Match": etag }); router.refresh(); })}>
        {t("reopen")}
      </button>
      <CatalogStatusLine status={status} />
    </div>
  );
}

/** Staff work on a request: assignment (group and agent), "Route again" and state changes. */
export function RequestWorkPanel({ request, settings, groups, assignees, permissions, csrfToken }: {
  request: ServiceRequest; settings: IncidentSettings | null; groups: GroupSummary[]; assignees: Person[] | null; permissions: string[]; csrfToken: string;
}) {
  const t = useTranslations("catalog.requests");
  const tStates = useTranslations("incidents.states");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [groupId, setGroupId] = useState(request.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(request.assignee?.id ?? "");
  const [people, setPeople] = useState<Person[] | null>(assignees);
  const [target, setTarget] = useState<TicketState | "">("");
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const base = `/v1/service-requests/${request.id}`;
  const closed = request.state === "Closed";
  const done = async (action: () => Promise<unknown>) => {
    const result = await run(action);
    if (result !== undefined) router.refresh();
  };
  const reasons = settings?.onHoldReasons.filter((r) => r.active) ?? [];
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="request-work">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("work")}</h2>
      {permissions.includes("requests.assign") && !closed && (
        <form className="space-y-2 rounded-lg border border-line p-4"
          onSubmit={(e) => { e.preventDefault(); void done(() => putJson(`${base}/assignment`, { groupId: groupId || null, assigneeId: assigneeId || null }, csrfToken, { "If-Match": request.etag })); }}>
          <fieldset className="grid gap-2 sm:grid-cols-2">
            <legend className="font-semibold">{t("assignment")}</legend>
            {groups.length > 0 && (
              <div className="space-y-1">
                <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
                <select id={`${id}-group`} value={groupId} className={fieldInput}
                  onChange={(e) => {
                    setGroupId(e.target.value);
                    setAssigneeId("");
                    void getJson<Person[]>(`/v1/service-requests/assignees${e.target.value ? `?${new URLSearchParams({ groupId: e.target.value })}` : ""}`).then(setPeople).catch(() => undefined);
                  }}>
                  <option value="">{t("noGroup")}</option>
                  {groups.filter((g) => g.active || g.id === request.group?.id).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
                </select>
              </div>
            )}
            <div className="space-y-1">
              <label htmlFor={`${id}-assignee`} className="block text-sm">{t("assignee")}</label>
              <select id={`${id}-assignee`} value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={fieldInput}>
                <option value="">{t("nobody")}</option>
                {(people ?? []).map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.id}</option>)}
              </select>
            </div>
          </fieldset>
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryAction} disabled={busy}>{t("saveAssignment")}</button>
            {request.state !== "Resolved" && (
              <button type="button" className={fieldButton} disabled={busy} onClick={() => void done(() => postJson(`${base}/route`, undefined, csrfToken))}>{t("routeAgain")}</button>
            )}
          </div>
        </form>
      )}
      {request.nextStates.length > 0 && (
        <form className="space-y-2 rounded-lg border border-line p-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (!target) return;
            void done(() => postJson(`${base}/transitions`, {
              state: target, onHoldReason: target === "OnHold" ? reason : undefined, resolutionNotes: target === "Resolved" ? notes : undefined,
            }, csrfToken, { "If-Match": request.etag }));
          }}>
          <div className="space-y-1">
            <label htmlFor={`${id}-state`} className="block font-semibold">{t("changeState")}</label>
            <select id={`${id}-state`} value={target} onChange={(e) => setTarget(e.target.value as TicketState | "")} className={fieldInput}>
              <option value="">{t("chooseState")}</option>
              {request.nextStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
            </select>
          </div>
          {target === "OnHold" && (
            <div className="space-y-1">
              <label htmlFor={`${id}-reason`} className="block text-sm">{t("onHoldReason")}</label>
              <select id={`${id}-reason`} value={reason} onChange={(e) => setReason(e.target.value)} required className={fieldInput}>
                <option value="">{t("chooseReason")}</option>
                {reasons.map((r) => <option key={r.key} value={r.key}>{textOf(r.name, locale)}</option>)}
              </select>
            </div>
          )}
          {target === "Resolved" && (
            <div className="space-y-1">
              <label htmlFor={`${id}-notes`} className="block text-sm">{t("resolutionNotes")}</label>
              <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} required rows={3} dir="auto" className={fieldInput} />
            </div>
          )}
          <button type="submit" className={primaryAction} disabled={busy || !target}>{t("apply")}</button>
        </form>
      )}
      <CatalogStatusLine status={status} />
    </section>
  );
}
