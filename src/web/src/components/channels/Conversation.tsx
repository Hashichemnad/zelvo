"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { TicketMessage } from "@/lib/channels";

/**
 * An incident's public conversation (task E2): messages of the requester and staff from the portal,
 * email and chat, oldest first, and a box to reply. Staff replies reach the requester by email; for
 * staff the delivery of each reply is shown.
 */
export function Conversation({
  incidentId,
  initial,
  staff,
  canWrite,
  emailTicket,
  csrfToken,
  display,
}: {
  incidentId: string;
  initial: TicketMessage[];
  staff: boolean;
  canWrite: boolean;
  /** The ticket came in by email: staff replies go from its mailbox. */
  emailTicket: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("channels.conversation");
  const id = useId();
  const [messages, setMessages] = useState(initial);
  const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  async function send(event: FormEvent) {
    event.preventDefault();
    if (!body.trim()) return;
    setBusy(true);
    setError(null);
    setSent(false);
    try {
      await postJson(`/v1/incidents/${incidentId}/messages`, { body }, csrfToken);
      setBody("");
      setMessages(await getJson<TicketMessage[]>(`/v1/incidents/${incidentId}/messages`));
      setSent(true);
    } catch (refused) {
      setError(refused instanceof ApiError && refused.code === "ticket-closed" ? t("closed") : t("failed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4" data-testid="conversation">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("title")}</h2>
      {messages.length === 0 ? (
        <p className="text-ink-muted">{t("empty")}</p>
      ) : (
        <ol className="space-y-3">
          {messages.map((message) => (
            <li
              key={message.id}
              className={`max-w-3xl rounded-md border p-3 ${message.authorKind === "Staff" ? "ms-8 border-brand/40 bg-surface-muted" : "me-8 border-line"}`}
              data-testid="conversation-message"
            >
              <p className="text-sm text-ink-muted">
                <span className="font-semibold text-ink">{message.author?.displayName ?? t(`authors.${message.authorKind}`)}</span>
                {" · "}
                {t(`via.${message.channel === "Email" || message.channel === "Chat" ? message.channel : "Portal"}`)}
                {" · "}
                <time dateTime={message.createdAt}>{formatDateTime(new Date(message.createdAt), display)}</time>
                {staff && message.delivery && message.delivery !== "None" && (
                  <>
                    {" · "}
                    <span className={message.delivery === "Failed" ? "text-bad" : undefined}>{t(`delivery.${message.delivery}`)}</span>
                  </>
                )}
              </p>
              <p dir="auto" className="mt-1 whitespace-pre-wrap break-words">{message.body}</p>
              {message.attachments.length > 0 && (
                <p className="mt-1 text-sm">
                  {t("files", { count: message.attachments.length })}: {message.attachments.map((a) => a.fileName).join(", ")}
                </p>
              )}
            </li>
          ))}
        </ol>
      )}
      {canWrite && (
        <form onSubmit={send} className="max-w-3xl space-y-2" noValidate>
          <label htmlFor={`${id}-body`} className="font-semibold">{staff ? t("replyLabel") : t("answerLabel")}</label>
          <textarea
            id={`${id}-body`}
            dir="auto"
            rows={4}
            maxLength={50000}
            value={body}
            onChange={(e) => setBody(e.target.value)}
            className="block w-full rounded-md border border-line bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
            aria-describedby={`${id}-help`}
          />
          <p id={`${id}-help`} className="text-sm text-ink-muted">{staff ? (emailTicket ? t("replyHelpEmail") : t("replyHelp")) : t("answerHelp")}</p>
          {error && <p role="alert" className="text-bad">{error}</p>}
          {sent && <p role="status">{t("sent")}</p>}
          <button
            type="submit"
            disabled={busy || !body.trim()}
            className="rounded-md bg-brand px-4 py-2 font-semibold text-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60"
          >
            {staff ? t("reply") : t("answer")}
          </button>
        </form>
      )}
    </section>
  );
}
