"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Localized, useSaver } from "@/components/directory/common";
import { StateBadge } from "@/components/incidents/StateBadge";
import type { SaveStatus } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { postJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { IncidentSettings, Locale } from "@/lib/incidents";
import { journeyStepOf, journeySteps, type HappinessSettings, type Rating, type VoiceComment, type VoiceRecord, type VoiceSettings } from "@/lib/voice";
import { HappinessMeter } from "./HappinessMeter";

/** The result of the last action, read out by screen readers; API problem codes get their own text. */
export function VoiceStatusLine({ status }: { status: SaveStatus }) {
  const t = useTranslations("voice");
  const tCommon = useTranslations("common");
  let text = "";
  if (status.kind === "saving") text = t("saving");
  else if (status.kind === "saved") text = t("saved");
  else if (status.kind === "failed") {
    const code = status.code;
    text = code === "rate-limited" ? tCommon("rateLimited") : code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("failed");
  }
  return <p role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ok"}>{text}</p>;
}

/** Records in a table: number, type, title, state and when; staff also see requester and assignee. */
export function VoiceTable({ records, basePath, staff, display }: { records: VoiceRecord[]; basePath: string; staff: boolean; display: DateDisplayOptions }) {
  const t = useTranslations("voice");
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse" data-testid="voice-table">
        <thead>
          <tr>
            <th scope="col" className={cell}>{t("fields.number")}</th>
            <th scope="col" className={cell}>{t("fields.type")}</th>
            <th scope="col" className={cell}>{t("fields.title")}</th>
            <th scope="col" className={cell}>{t("fields.state")}</th>
            {staff && <th scope="col" className={cell}>{t("fields.requester")}</th>}
            {staff && <th scope="col" className={cell}>{t("fields.assignee")}</th>}
            <th scope="col" className={cell}>{t("fields.created")}</th>
          </tr>
        </thead>
        <tbody>
          {records.map((r) => (
            <tr key={r.id}>
              <td className={cell}><Link href={`${basePath}/${r.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{r.number}</Link></td>
              <td className={cell}>{t(`types.${r.type}`)}</td>
              <td className={cell}><span dir="auto">{r.title}</span></td>
              <td className={cell}><StateBadge state={r.state} /></td>
              {staff && <td className={cell}>{r.anonymous ? t("anonymous") : r.requester?.displayName}</td>}
              {staff && <td className={cell}>{r.assignee?.displayName ?? t("nobody")}</td>}
              <td className={cell}>{formatDateTime(new Date(r.createdAt), display)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Where the record is: received, being handled, resolved, closed (text, not colour alone). */
export function VoiceProgress({ record }: { record: VoiceRecord }) {
  const t = useTranslations("voice.progress");
  const current = journeyStepOf(record.state);
  const percent = Math.round((current / (journeySteps.length - 1)) * 100);
  return (
    <section aria-labelledby="voice-progress" className="space-y-2" data-testid="voice-progress">
      <h2 id="voice-progress" className="text-xl font-semibold">{t("heading")}</h2>
      <div role="progressbar" aria-labelledby="voice-progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
        aria-valuetext={t("stepOf", { current: current + 1, total: journeySteps.length, name: t(journeySteps[current]!) })}
        className="h-3 w-full overflow-hidden rounded-full bg-surface-muted">
        <div className="h-full bg-brand" style={{ width: `${percent}%` }} />
      </div>
      <ol className="grid gap-2 sm:grid-flow-col sm:auto-cols-fr">
        {journeySteps.map((step, i) => (
          <li key={step} aria-current={i === current ? "step" : undefined}
            className={`rounded-md border p-2 text-sm ${i === current ? "border-brand font-semibold" : i < current ? "border-ok" : "border-line text-ink-muted"}`}>
            <span className="sr-only">{i < current ? t("done") : i === current ? t("current") : t("upcoming")}: </span>
            {t(step)}
          </li>
        ))}
      </ol>
    </section>
  );
}

/** Facts: type, state, who and when, service, the type's closure details and the resolution. */
export function VoiceFacts({ record, display, settings, incidentSettings, staff }: {
  record: VoiceRecord; display: DateDisplayOptions; settings: VoiceSettings | null; incidentSettings: IncidentSettings | null; staff: boolean;
}) {
  const t = useTranslations("voice");
  const locale = useLocale() as Locale;
  const reason = record.onHoldReason ? incidentSettings?.onHoldReasons.find((r) => r.key === record.onHoldReason) : null;
  const severity = record.severity ? settings?.severities.find((s) => s.level === record.severity) : null;
  const cause = record.rootCauseKey ? settings?.rootCauses.find((r) => r.key === record.rootCauseKey) : null;
  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : "");
  const linkBase = (type: string) => (type === "ServiceRequest" ? (staff ? "/requests" : "/my-requests") : staff ? "/customer-voice" : "/my-voice");
  return (
    <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
      <div><dt className="font-semibold">{t("fields.type")}</dt><dd>{t(`types.${record.type}`)}</dd></div>
      <div><dt className="font-semibold">{t("fields.state")}</dt><dd><StateBadge state={record.state} />{reason && <span className="ms-2"><Localized text={reason.name} locale={locale} /></span>}</dd></div>
      {staff && <div><dt className="font-semibold">{t("fields.requester")}</dt><dd>{record.anonymous ? t("anonymous") : record.requester?.displayName}</dd></div>}
      {!staff && <div><dt className="font-semibold">{t("fields.team")}</dt><dd>{record.group ? <Localized text={record.group.name} locale={locale} /> : t("noTeamYet")}</dd></div>}
      {staff && <div><dt className="font-semibold">{t("fields.assignee")}</dt><dd>{record.assignee?.displayName ?? t("nobody")}{record.group && <> · <Localized text={record.group.name} locale={locale} /></>}</dd></div>}
      <div><dt className="font-semibold">{t("fields.created")}</dt><dd>{date(record.createdAt)}</dd></div>
      {record.resolvedAt && <div><dt className="font-semibold">{t("fields.resolved")}</dt><dd>{date(record.resolvedAt)}</dd></div>}
      {record.category && <div><dt className="font-semibold">{t("fields.category")}</dt><dd>{textOf(record.category.path, locale) || textOf(record.category.name, locale)}</dd></div>}
      {record.service && <div><dt className="font-semibold">{t("fields.service")}</dt><dd><Localized text={record.service.name} locale={locale} /></dd></div>}
      {record.type === "Complaint" && severity && <div><dt className="font-semibold">{t("fields.severity")}</dt><dd>{textOf(severity.name, locale)}</dd></div>}
      {record.type === "Complaint" && staff && <div><dt className="font-semibold">{t("fields.rootCause")}</dt><dd>{cause ? textOf(cause.name, locale) : t("notSet")}</dd></div>}
      {record.confirmation && <div><dt className="font-semibold">{t("fields.confirmation")}</dt><dd>{t(`confirmations.${record.confirmation}`)}</dd></div>}
      {record.review && <div><dt className="font-semibold">{t("fields.review")}</dt><dd>{t(`reviews.${record.review}`)}</dd></div>}
      {record.convertedTo && (
        <div><dt className="font-semibold">{t("fields.convertedTo")}</dt>
          <dd><Link href={`${linkBase(record.convertedTo.type)}/${record.convertedTo.id}`} className="font-mono text-brand underline" dir="ltr">{record.convertedTo.number}</Link></dd></div>
      )}
      {record.convertedFrom && (
        <div><dt className="font-semibold">{t("fields.convertedFrom")}</dt>
          <dd><Link href={`${linkBase(record.convertedFrom.type)}/${record.convertedFrom.id}`} className="font-mono text-brand underline" dir="ltr">{record.convertedFrom.number}</Link></dd></div>
      )}
      {record.description && <div className="sm:col-span-2"><dt className="font-semibold">{t("fields.description")}</dt><dd className="whitespace-pre-line" dir="auto">{record.description}</dd></div>}
      {record.reviewFeedback && <div className="sm:col-span-2"><dt className="font-semibold">{t("fields.feedback")}</dt><dd className="whitespace-pre-line" dir="auto">{record.reviewFeedback}</dd></div>}
      {record.resolutionNotes && record.type !== "Suggestion" && (
        <div className="sm:col-span-2"><dt className="font-semibold">{t(record.type === "Inquiry" ? "fields.answer" : "fields.resolution")}</dt><dd className="whitespace-pre-line" dir="auto">{record.resolutionNotes}</dd></div>
      )}
    </dl>
  );
}

/** The conversation, oldest first, with a reply box (requesters and staff; staff also write internal notes). */
export function VoiceConversation({ comments, display, staff, closed, send }: {
  comments: VoiceComment[]; display: DateDisplayOptions; staff: boolean; closed: boolean;
  send: (body: string, internal: boolean) => Promise<unknown>;
}) {
  const t = useTranslations("voice.conversation");
  const id = useId();
  const router = useRouter();
  const { status, run, busy } = useSaver();
  const [body, setBody] = useState("");
  const [internal, setInternal] = useState(false);
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3" data-testid="voice-conversation">
      <h2 id={`${id}-heading`} className="text-xl font-semibold">{t("heading")}</h2>
      {comments.length === 0 ? <p className="text-ink-muted">{t("empty")}</p> : (
        <ol className="space-y-2">
          {comments.map((c) => (
            <li key={c.id} className={`rounded-lg border p-3 ${c.visibility === "Internal" ? "border-warn bg-surface-muted" : c.byStaff ? "border-brand" : "border-line"}`}>
              <p className="text-sm text-ink-muted">
                <span className="font-semibold text-ink">{c.author?.displayName ?? t("you")}</span>
                {c.visibility === "Internal" && <span className="ms-2 font-semibold">{t("internal")}</span>}
                <span className="ms-2">{formatDateTime(new Date(c.createdAt), display)}</span>
              </p>
              <p className="whitespace-pre-line" lang={c.language} dir="auto">{c.body}</p>
            </li>
          ))}
        </ol>
      )}
      {!closed && (
        <form className="space-y-2" onSubmit={(e) => {
          e.preventDefault();
          if (!body.trim()) return;
          void run(async () => { await send(body.trim(), internal); setBody(""); router.refresh(); });
        }}>
          <label htmlFor={`${id}-body`} className="block font-semibold">{t("reply")}</label>
          <textarea id={`${id}-body`} value={body} onChange={(e) => setBody(e.target.value)} rows={3} maxLength={20000} dir="auto" required className={fieldInput} />
          {staff && (
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} className="h-5 w-5" />
              {t("internalNote")}
            </label>
          )}
          <button type="submit" className={primaryAction} disabled={busy || !body.trim()}>{t(internal ? "addNote" : "send")}</button>
          <VoiceStatusLine status={status} />
        </form>
      )}
    </section>
  );
}

/** The requester accepts a resolved complaint's resolution or rejects it with a reason. */
export function ConfirmResolution({ send }: { send: (accept: boolean, comment: string | null) => Promise<unknown> }) {
  const t = useTranslations("voice.confirm");
  const id = useId();
  const router = useRouter();
  const { status, run, busy } = useSaver();
  const [rejecting, setRejecting] = useState(false);
  const [comment, setComment] = useState("");
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border-2 border-brand p-4" data-testid="confirm-resolution">
      <h2 id={`${id}-heading`} className="text-xl font-semibold">{t("heading")}</h2>
      <p>{t("intro")}</p>
      {!rejecting ? (
        <div className="flex flex-wrap gap-2">
          <button type="button" className={primaryAction} disabled={busy} onClick={() => void run(async () => { await send(true, null); router.refresh(); })}>{t("accept")}</button>
          <button type="button" className={fieldButton} disabled={busy} onClick={() => setRejecting(true)}>{t("reject")}</button>
        </div>
      ) : (
        <form className="space-y-2" onSubmit={(e) => { e.preventDefault(); if (comment.trim()) void run(async () => { await send(false, comment.trim()); router.refresh(); }); }}>
          <label htmlFor={`${id}-why`} className="block font-semibold">{t("why")}</label>
          <textarea id={`${id}-why`} value={comment} onChange={(e) => setComment(e.target.value)} rows={3} required maxLength={4000} dir="auto" className={fieldInput} />
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={primaryAction} disabled={busy || !comment.trim()}>{t("sendRejection")}</button>
            <button type="button" className={fieldButton} onClick={() => setRejecting(false)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <VoiceStatusLine status={status} />
    </section>
  );
}

/** The happiness meter of a signed-in requester's ticket of any record type. */
export function TicketHappiness({ ticketId, settings, rating, csrfToken }: { ticketId: string; settings: HappinessSettings; rating: Rating | null; csrfToken: string }) {
  return (
    <HappinessMeter settings={settings} kind="ticket" rating={rating}
      send={(score, comment) => postJson<Rating>(`/v1/tickets/${ticketId}/happiness`, { score, comment: comment || undefined }, csrfToken)} />
  );
}

/** The general happiness rating in the portal, for people and visitors. */
export function PortalHappiness({ settings, csrfToken }: { settings: HappinessSettings; csrfToken: string | null }) {
  return (
    <HappinessMeter settings={settings} kind="portal"
      send={(score, comment, device) => postJson<Rating>("/v1/happiness/portal", { score, comment: comment || undefined, deviceId: csrfToken ? undefined : device }, csrfToken ?? "")} />
  );
}

/** The requester reopens a resolved inquiry. */
export function ReopenInquiry({ recordId, etag, csrfToken }: { recordId: string; etag: string; csrfToken: string }) {
  const t = useTranslations("voice");
  const router = useRouter();
  const { status, run, busy } = useSaver();
  return (
    <div className="flex flex-wrap items-center gap-3">
      <button type="button" className={fieldButton} disabled={busy}
        onClick={() => void run(async () => { await postJson(`/v1/customer-voice/${recordId}/transitions`, { state: "Reopened" }, csrfToken, { "If-Match": etag }); router.refresh(); })}>
        {t("reopen")}
      </button>
      <VoiceStatusLine status={status} />
    </div>
  );
}

/** The conversation of a record for signed-in people (requester or staff). */
export function RecordConversation({ recordId, comments, display, staff, closed, csrfToken }: {
  recordId: string; comments: VoiceComment[]; display: DateDisplayOptions; staff: boolean; closed: boolean; csrfToken: string;
}) {
  const locale = useLocale();
  return (
    <VoiceConversation comments={comments} display={display} staff={staff} closed={closed}
      send={(body, internal) => postJson(`/v1/customer-voice/${recordId}/comments`, { body, visibility: internal ? "Internal" : "Public", language: locale }, csrfToken)} />
  );
}

/** The requester's confirmation of their resolved complaint. */
export function RecordConfirmation({ recordId, etag, csrfToken }: { recordId: string; etag: string; csrfToken: string }) {
  return (
    <ConfirmResolution send={(accept, comment) => postJson(`/v1/customer-voice/${recordId}/confirmation`, { accept, comment: comment ?? undefined }, csrfToken, { "If-Match": etag })} />
  );
}
