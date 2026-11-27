"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useOnline } from "@/lib/pwa/hooks";
import { listOutbox, listTickets, removeWork, type OfflineTicket, type OutboxItem } from "@/lib/pwa/offline-store";

/**
 * The offline page (module 19, task J1, decision 13): the requester's own last-viewed tickets,
 * read-only and as last seen, and the work this device will send when the connection is back.
 * Everything comes from the device; nothing here calls the API.
 */
export function OfflineView() {
  const t = useTranslations("app.offline");
  const locale = useLocale();
  const id = useId();
  const online = useOnline();
  const [tickets, setTickets] = useState<OfflineTicket[] | null>(null);
  const [outbox, setOutbox] = useState<OutboxItem[]>([]);

  useEffect(() => {
    Promise.all([listTickets(), listOutbox()])
      .then(([kept, unsent]) => {
        setTickets(kept);
        setOutbox(unsent);
      })
      .catch(() => setTickets([]));
  }, []);

  async function remove(item: OutboxItem) {
    if (item.id === undefined) return;
    await removeWork(item.id);
    setOutbox((current) => current.filter((i) => i.id !== item.id));
  }

  const viewed = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));

  return (
    <div className="space-y-8" data-testid="offline-page">
      <header className="space-y-2">
        <h1 className="text-2xl font-bold">{online ? t("backOnline") : t("title")}</h1>
        <p>{online ? t("backOnlineText") : t("intro")}</p>
        <a href={`/${locale}`} className="inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface">
          {online ? t("goHome") : t("tryAgain")}
        </a>
      </header>

      <section aria-labelledby={`${id}-outbox`} className="space-y-3">
        <h2 id={`${id}-outbox`} className="text-xl font-semibold">{t("unsentTitle")}</h2>
        {outbox.length === 0 ? (
          <p className="text-ink-muted">{t("unsentNone")}</p>
        ) : (
          <ul className="space-y-2" data-testid="offline-outbox">
            {outbox.map((item) => (
              <li key={item.id} className="flex flex-wrap items-start justify-between gap-3 rounded-md border border-line p-3">
                <div className="min-w-0 space-y-1">
                  <p className="font-semibold">{item.kind === "incident" ? t("unsentIncident") : t("unsentReply", { number: item.ticketNumber ?? "" })}</p>
                  <p dir="auto" className="break-words">{item.summary}</p>
                  <p className="text-sm text-ink-muted">{t("keptSince", { when: viewed(item.createdAt) })}</p>
                  {item.failed && <p className="text-sm text-bad">{t("refused")}</p>}
                </div>
                <button type="button" onClick={() => void remove(item)} className="rounded-md border border-line px-3 py-1.5 text-sm font-semibold">
                  {t("discard")}
                </button>
              </li>
            ))}
          </ul>
        )}
        <p className="text-sm text-ink-muted">{t("unsentHelp")}</p>
      </section>

      <section aria-labelledby={`${id}-tickets`} className="space-y-3">
        <h2 id={`${id}-tickets`} className="text-xl font-semibold">{t("ticketsTitle")}</h2>
        {tickets === null ? null : tickets.length === 0 ? (
          <p className="text-ink-muted">{t("ticketsNone")}</p>
        ) : (
          <ul className="space-y-3" data-testid="offline-tickets">
            {tickets.map((ticket) => (
              <li key={ticket.id} className="rounded-md border border-line p-3">
                <details>
                  <summary className="cursor-pointer">
                    <span className="font-semibold">{ticket.number}</span> · <span dir="auto">{ticket.title}</span>
                    <span className="block text-sm text-ink-muted">
                      {ticket.state}
                      {ticket.team ? ` · ${ticket.team}` : ""} · {t("asSeen", { when: viewed(ticket.viewedAt) })}
                    </span>
                  </summary>
                  <div className="mt-3 space-y-3">
                    <p className="text-sm text-ink-muted">{t("readOnly", { updated: ticket.updated })}</p>
                    {ticket.description && <p dir="auto" className="whitespace-pre-wrap break-words">{ticket.description}</p>}
                    {ticket.messages.length > 0 && (
                      <ol className="space-y-2">
                        {ticket.messages.map((m, index) => (
                          <li key={index} className="rounded-md bg-surface-muted p-2">
                            <p className="text-sm font-semibold"><bdi>{m.author}</bdi> · {m.at}</p>
                            <p dir="auto" className="whitespace-pre-wrap break-words">{m.text}</p>
                          </li>
                        ))}
                      </ol>
                    )}
                  </div>
                </details>
              </li>
            ))}
          </ul>
        )}
        <p className="text-sm text-ink-muted">{t("ticketsHelp")}</p>
      </section>
    </div>
  );
}
