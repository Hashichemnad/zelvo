"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, useSaver } from "@/components/directory/common";
import { postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { ApprovalDetail, ApprovalProcessView, ApprovalSummary } from "@/lib/workflows";

/** Decisions asked of the signed-in person, newest first. */
export function ApprovalList({ approvals, display }: { approvals: ApprovalSummary[]; display: DateDisplayOptions }) {
  const t = useTranslations("approvals");
  const locale = useLocale() as Locale;
  const cell = "border-b border-line px-2 py-2 text-start align-top";
  if (approvals.length === 0) return <p>{t("none")}</p>;
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse" data-testid="approval-table">
        <thead>
          <tr>
            <th scope="col" className={cell}>{t("request")}</th>
            <th scope="col" className={cell}>{t("service")}</th>
            <th scope="col" className={cell}>{t("requester")}</th>
            <th scope="col" className={cell}>{t("level")}</th>
            <th scope="col" className={cell}>{t("state")}</th>
            <th scope="col" className={cell}>{t("asked")}</th>
          </tr>
        </thead>
        <tbody>
          {approvals.map((a) => (
            <tr key={a.id}>
              <td className={cell}><Link href={`/approvals/${a.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{a.ticket.number}</Link></td>
              <td className={cell}>{a.ticket.service ? <Localized text={a.ticket.service.name} locale={locale} /> : <span dir="auto">{a.ticket.title}</span>}</td>
              <td className={cell}>{a.ticket.requester.displayName}</td>
              <td className={cell}><Localized text={a.levelName} locale={locale} /></td>
              <td className={cell}>{t(`decisionStates.${a.state}`)}</td>
              <td className={cell}>{formatDateTime(new Date(a.createdAt), display)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** An approval process: its levels in order, each with its state and the approvers' decisions. */
export function ApprovalProcessTimeline({ process, display, headingLevel = 3 }: { process: ApprovalProcessView; display: DateDisplayOptions; headingLevel?: 2 | 3 }) {
  const t = useTranslations("approvals");
  const locale = useLocale() as Locale;
  const Heading = headingLevel === 2 ? "h2" : "h3";
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-2 rounded-lg border border-line p-4" data-testid="approval-process">
      <Heading id={id} className="text-lg font-semibold">
        {t(`stages.${process.stage}`)} · {t(`processStates.${process.state}`)}
      </Heading>
      {process.reason && <p dir="auto"><span className="font-semibold">{t("reason")}</span> {process.reason}</p>}
      <ol className="space-y-2">
        {process.levels.map((level) => (
          <li key={level.index} className="rounded-md border border-line p-2" data-testid="approval-level-view">
            <p className="font-semibold">
              {t("levelLine", { number: level.index + 1 })} <Localized text={level.name} locale={locale} />
              <span className="ms-2">— {t(`levelStates.${level.state}`)}</span>
            </p>
            <p className="text-sm text-ink-muted">{t(`modes.${level.mode}`)}{level.fallback && ` · ${t("fallback")}`}</p>
            {level.decisions.length > 0 && (
              <ul className="mt-1 space-y-1 text-sm">
                {level.decisions.map((d) => (
                  <li key={d.id}>
                    {d.approver.displayName ?? "—"}: {t(`decisionStates.${d.state}`)}
                    {d.decidedAt && <span className="ms-1 text-ink-muted">({formatDateTime(new Date(d.decidedAt), display)})</span>}
                    {d.comment && <span className="ms-1" dir="auto">— {d.comment}</span>}
                  </li>
                ))}
              </ul>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}

/** All approval processes of a request (requester and staff views). */
export function RequestApprovals({ processes, display }: { processes: ApprovalProcessView[]; display: DateDisplayOptions }) {
  const t = useTranslations("approvals");
  if (processes.length === 0) return null;
  return (
    <section aria-labelledby="request-approvals" className="space-y-3" data-testid="request-approvals">
      <h2 id="request-approvals" className="text-xl font-semibold">{t("requestApprovals")}</h2>
      {processes.map((p) => <ApprovalProcessTimeline key={p.id} process={p} display={display} />)}
    </section>
  );
}

/** Stops a pending approval (staff, or the requester before assignment). */
export function CancelApprovalButton({ requestId, csrfToken, requester }: { requestId: string; csrfToken: string; requester: boolean }) {
  const t = useTranslations("approvals");
  const router = useRouter();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const { status, run, busy } = useSaver();
  if (!open) {
    return <button type="button" className={secondaryButton} onClick={() => setOpen(true)}>{t(requester ? "withdraw" : "stopApproval")}</button>;
  }
  return (
    <form className="space-y-2" onSubmit={(e) => {
      e.preventDefault();
      void run(async () => { await postJson(`/v1/service-requests/${requestId}/approvals/cancel`, { reason: reason.trim() || null }, csrfToken); router.refresh(); });
    }}>
      <label htmlFor={`${id}-reason`} className="block font-semibold">{t("cancelReason")}</label>
      <textarea id={`${id}-reason`} dir="auto" rows={2} maxLength={2000} className={inputClass} value={reason} onChange={(e) => setReason(e.target.value)} />
      <div className="flex gap-2">
        <button type="submit" className={primaryButton} disabled={busy}>{t(requester ? "withdraw" : "stopApproval")}</button>
        <button type="button" className={secondaryButton} onClick={() => setOpen(false)}>{t("cancel")}</button>
      </div>
      <SaveStatusLine status={status} />
    </form>
  );
}

/**
 * The decision form (task C3). The email's Approve and Reject buttons open this page with
 * `?decision=approve|reject`, so the choice is already made; nothing is recorded until the
 * approver, signed in, confirms. Rejecting needs a reason.
 */
export function ApprovalDecisionForm({ detail, initialChoice, fromEmail, csrfToken }: {
  detail: ApprovalDetail;
  initialChoice: "Approve" | "Reject" | null;
  fromEmail: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("approvals");
  const router = useRouter();
  const id = useId();
  const [choice, setChoice] = useState<"Approve" | "Reject" | null>(initialChoice);
  const [comment, setComment] = useState("");
  const { status, run, busy } = useSaver();
  if (!detail.mayDecide) {
    return <p role="status">{t(`decided.${detail.approval.state}`)}</p>;
  }
  const needsReason = choice === "Reject" && comment.trim().length === 0;
  return (
    <form className="space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} data-testid="approval-decision"
      onSubmit={(e) => {
        e.preventDefault();
        if (!choice || needsReason) return;
        void run(async () => {
          await postJson(`/v1/approvals/${detail.approval.id}/decision`, { decision: choice, comment: comment.trim() || null, via: fromEmail ? "email" : "portal" }, csrfToken);
          router.refresh();
        });
      }}>
      <h2 id={`${id}-heading`} className="text-xl font-semibold">{t("yourDecision")}</h2>
      <fieldset className="space-y-1">
        <legend className="font-semibold">{t("decision")}</legend>
        {(["Approve", "Reject"] as const).map((c) => (
          <label key={c} className="flex items-center gap-2">
            <input type="radio" className="size-5" name={`${id}-choice`} checked={choice === c} onChange={() => setChoice(c)} />
            {t(`choices.${c}`)}
          </label>
        ))}
      </fieldset>
      <div className="space-y-1">
        <label htmlFor={`${id}-comment`} className="block font-semibold">{t(choice === "Reject" ? "reasonRequired" : "comment")}</label>
        <textarea id={`${id}-comment`} dir="auto" rows={3} maxLength={2000} className={inputClass} value={comment} required={choice === "Reject"}
          aria-invalid={needsReason || undefined} aria-describedby={needsReason ? `${id}-needs` : undefined} onChange={(e) => setComment(e.target.value)} />
        {needsReason && <p id={`${id}-needs`} className="text-sm text-bad">{t("giveReason")}</p>}
      </div>
      <button type="submit" className={primaryButton} disabled={busy || !choice || needsReason}>{t("confirm")}</button>
      <SaveStatusLine status={status} />
    </form>
  );
}
