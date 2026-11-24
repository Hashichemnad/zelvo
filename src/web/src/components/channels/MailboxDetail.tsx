"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { errorCodeOf, type InboundEmail, type Mailbox, type OutboundEmail, type Page } from "@/lib/channels";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import { cellClass, dangerButton, secondaryButton, tableClass } from "../settings/integrations/ui";
import { MailboxForm } from "./MailboxForm";

/** One mailbox: its settings, test and sign-in, and the logs of emails read and sent (task E2). */
export function MailboxDetail({
  initial,
  log,
  outgoing,
  csrfToken,
  categories,
  groups,
  display,
  signIn,
}: {
  initial: Mailbox;
  log: Page<InboundEmail>;
  outgoing: Page<OutboundEmail>;
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  display: DateDisplayOptions;
  /** Result of an administrator's sign-in just finished: "ok" or "failed". */
  signIn: string | null;
}) {
  const t = useTranslations("channels.mailboxes");
  const router = useRouter();
  const [mailbox, setMailbox] = useState(initial);
  const [entries, setEntries] = useState(log);
  const [sent, setSent] = useState(outgoing);
  const [message, setMessage] = useState<{ text: string; bad: boolean } | null>(
    signIn === "ok" ? { text: t("signedIn"), bad: false } : signIn ? { text: t("signInFailed"), bad: true } : null,
  );

  async function reload() {
    const [m, l, o] = await Promise.all([
      getJson<Mailbox>(`/v1/mailboxes/${mailbox.id}`),
      getJson<Page<InboundEmail>>(`/v1/mailboxes/${mailbox.id}/log`),
      getJson<Page<OutboundEmail>>(`/v1/mailboxes/${mailbox.id}/outgoing`),
    ]);
    setMailbox(m);
    setEntries(l);
    setSent(o);
  }

  async function test() {
    setMessage(null);
    try {
      const result = await postJson<{ succeeded: boolean; unread: number | null; code: string | null }>(`/v1/mailboxes/${mailbox.id}/test`, {}, csrfToken);
      setMessage(
        result.succeeded
          ? { text: t("testOk", { count: result.unread ?? 0 }), bad: false }
          : { text: t.has(`errors.${result.code}`) ? t(`errors.${result.code}`) : t("errors.other"), bad: true },
      );
    } catch {
      setMessage({ text: t("failed"), bad: true });
    }
  }

  async function run(action: () => Promise<unknown>, done?: string) {
    setMessage(null);
    try {
      await action();
      await reload();
      if (done) setMessage({ text: done, bad: false });
    } catch (refused) {
      setMessage({ text: refused instanceof ApiError && refused.code === "sign-in-not-used" ? t("errors.other") : t("failed"), bad: true });
    }
  }

  async function startSignIn() {
    setMessage(null);
    try {
      const { url } = await postJson<{ url: string }>(`/v1/mailboxes/${mailbox.id}/sign-in`, {}, csrfToken);
      window.location.assign(url);
    } catch {
      setMessage({ text: t("signInNeedsSecret"), bad: true });
    }
  }

  async function remove() {
    if (!window.confirm(t("deleteConfirm", { name: mailbox.name }))) return;
    const response = await fetch(`/bff/api/v1/mailboxes/${mailbox.id}`, { method: "DELETE", headers: { "X-CSRF-Token": csrfToken } });
    if (response.ok) router.push("/settings/mailboxes");
    else setMessage({ text: t("failed"), bad: true });
  }

  const code = errorCodeOf(mailbox.lastError);
  return (
    <div className="space-y-8" data-testid="mailbox-detail">
      <section className="space-y-3">
        <p>
          {t(`statuses.${mailbox.status}`)} ·{" "}
          {mailbox.lastCheckedAt ? t("lastReadAt", { at: formatDateTime(new Date(mailbox.lastCheckedAt), display) }) : t("never")}
          {mailbox.signedInAs && <> · {t("signedInAs")} <span dir="ltr">{mailbox.signedInAs}</span></>}
        </p>
        {mailbox.lastCheckSucceeded === false && (
          <p role="status" className="text-bad">{t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other")}</p>
        )}
        {message && <p role={message.bad ? "alert" : "status"} className={message.bad ? "text-bad" : undefined}>{message.text}</p>}
        <div className="flex flex-wrap gap-2">
          <button type="button" className={secondaryButton} onClick={test}>{t("test")}</button>
          <button type="button" className={secondaryButton} onClick={() => run(() => postJson(`/v1/mailboxes/${mailbox.id}/check`, {}, csrfToken), t("checkQueued"))}>{t("checkNow")}</button>
          {mailbox.auth === "OAuthSignIn" && <button type="button" className={secondaryButton} onClick={startSignIn}>{t(`signIn.${mailbox.provider === "Gmail" ? "Gmail" : "Microsoft365"}`)}</button>}
          <button type="button" className={secondaryButton} onClick={() => run(() => patchJson(`/v1/mailboxes/${mailbox.id}`, { status: mailbox.status === "Active" ? "Disabled" : "Active" }, csrfToken))}>
            {mailbox.status === "Active" ? t("disable") : t("enable")}
          </button>
          <button type="button" className={dangerButton} onClick={remove}>{t("delete")}</button>
        </div>
      </section>

      <section className="space-y-3" aria-labelledby="mailbox-settings">
        <h2 id="mailbox-settings" className="text-xl font-semibold">{t("settingsTitle")}</h2>
        <MailboxForm mailbox={mailbox} csrfToken={csrfToken} categories={categories} groups={groups} onSaved={setMailbox} />
      </section>

      <section className="space-y-3" aria-labelledby="mailbox-log">
        <h2 id="mailbox-log" className="text-xl font-semibold">{t("logTitle")}</h2>
        <p className="text-sm text-ink-muted">{t("logIntro")}</p>
        {entries.items.length === 0 ? (
          <p>{t("logEmpty")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className={tableClass}>
              <caption className="sr-only">{t("logTitle")}</caption>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("columns.received")}</th>
                  <th scope="col" className={cellClass}>{t("columns.from")}</th>
                  <th scope="col" className={cellClass}>{t("columns.subject")}</th>
                  <th scope="col" className={cellClass}>{t("columns.outcome")}</th>
                </tr>
              </thead>
              <tbody>
                {entries.items.map((e) => {
                  const reason = e.reason?.startsWith("files-skipped") ? "files-skipped" : e.reason;
                  return (
                    <tr key={e.id} data-testid="mailbox-log-row">
                      <td className={cellClass}>{formatDateTime(new Date(e.receivedAt), display)}</td>
                      <td className={cellClass}><span dir="ltr">{e.from ?? "—"}</span></td>
                      <td className={cellClass}><span dir="auto">{e.subject ?? ""}</span></td>
                      <td className={cellClass}>
                        {t(`outcomes.${e.outcome}`)}
                        {reason && <span className="block text-sm text-ink-muted">{t.has(`reasons.${reason}`) ? t(`reasons.${reason}`) : reason}</span>}
                        {e.ticketId && <Link href={`/incidents/${e.ticketId}`} className="block text-sm text-brand underline" dir="ltr">{e.ticketNumber}</Link>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="space-y-3" aria-labelledby="mailbox-outgoing">
        <h2 id="mailbox-outgoing" className="text-xl font-semibold">{t("outgoingTitle")}</h2>
        {sent.items.length === 0 ? (
          <p>{t("outgoingEmpty")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className={tableClass}>
              <caption className="sr-only">{t("outgoingTitle")}</caption>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("columns.created")}</th>
                  <th scope="col" className={cellClass}>{t("columns.to")}</th>
                  <th scope="col" className={cellClass}>{t("columns.subject")}</th>
                  <th scope="col" className={cellClass}>{t("columns.status")}</th>
                </tr>
              </thead>
              <tbody>
                {sent.items.map((e) => (
                  <tr key={e.id}>
                    <td className={cellClass}>{formatDateTime(new Date(e.createdAt), display)}</td>
                    <td className={cellClass}><span dir="ltr">{e.to}</span></td>
                    <td className={cellClass}>
                      <span dir="auto">{e.subject}</span>
                      <span className="block text-sm text-ink-muted">{e.reply ? t("kinds.reply") : t("kinds.notification")}</span>
                    </td>
                    <td className={cellClass}>
                      {t(`sendStatuses.${e.status}`)}
                      {e.lastError && <span className="block text-sm text-bad" dir="ltr">{e.lastError}</span>}
                      {e.status === "Failed" && (
                        <button type="button" className="block text-sm font-semibold text-brand underline" onClick={() => run(() => postJson(`/v1/mailboxes/${mailbox.id}/outgoing/${e.id}/resend`, {}, csrfToken))}>
                          {t("resend")}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
