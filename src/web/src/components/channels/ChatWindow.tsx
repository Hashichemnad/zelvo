"use client";

import { useCallback, useEffect, useId, useRef, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { useChatSignals } from "@/lib/chat-signals";
import { mergeMessages, textIn, type ChatMessage, type PublicChatWidget, type VisitorChat } from "@/lib/channels";

type Stage = "start" | "offline" | "left" | "chat";

interface Stored {
  sessionId: string;
  token: string | null;
}

const storageKey = (widgetId: string) => `zelvo-chat:${widgetId}`;

function readStored(widgetId: string): Stored | null {
  try {
    const raw = sessionStorage.getItem(storageKey(widgetId));
    return raw ? (JSON.parse(raw) as Stored) : null;
  } catch {
    return null;
  }
}

function writeStored(widgetId: string, value: Stored | null) {
  try {
    if (value) sessionStorage.setItem(storageKey(widgetId), JSON.stringify(value));
    else sessionStorage.removeItem(storageKey(widgetId));
  } catch {
    // Storage may be blocked (third-party frames): the chat lasts for this page only.
  }
}

/**
 * A visitor's live chat window (task E2): on the portal for signed-in people (no details asked)
 * and in the widget frame of other websites, where visitors give their name and email first. With
 * nobody online, visitors leave a message, which becomes a ticket.
 */
export function ChatWindow({
  widget,
  locale,
  signedIn,
  csrfToken,
  origin,
  onClose,
}: {
  widget: PublicChatWidget;
  locale: string;
  /** Signed in on the portal: the session's account is the visitor. */
  signedIn: boolean;
  csrfToken?: string | null;
  /** The embedding website's origin (widget frame only). */
  origin?: string | null;
  onClose?: () => void;
}) {
  const t = useTranslations("channels.chat");
  const id = useId();
  const [stage, setStage] = useState<Stage>(widget.online ? "start" : "offline");
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [text, setText] = useState("");
  const [session, setSession] = useState<Stored | null>(null);
  const [chat, setChat] = useState<VisitorChat | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [ticketNumber, setTicketNumber] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const list = useRef<HTMLOListElement>(null);

  const headers = useCallback(
    (stored: Stored | null): Record<string, string> => {
      const result: Record<string, string> = { "Content-Type": "application/json", Accept: "application/json" };
      if (csrfToken) result["X-CSRF-Token"] = csrfToken;
      if (stored?.token) result["X-Chat-Token"] = stored.token;
      return result;
    },
    [csrfToken],
  );

  const load = useCallback(
    async (stored: Stored | null = session) => {
      if (!stored) return;
      const last = messages.at(-1);
    const after = last ? `?after=${last.sequence}` : "";
      const response = await fetch(`/bff/api/v1/chat/sessions/${stored.sessionId}${after}`, { headers: headers(stored), cache: "no-store" });
      if (response.status === 404) {
        writeStored(widget.id, null);
        setSession(null);
        setStage(widget.online ? "start" : "offline");
        return;
      }
      if (!response.ok) return;
      const loaded = (await response.json()) as VisitorChat;
      setChat(loaded);
      setMessages((current) => mergeMessages(current, loaded.messages));
    },
    [session, messages, headers, widget.id, widget.online],
  );

  // A chat of this visit carries on after a page reload.
  useEffect(() => {
    const stored = readStored(widget.id);
    if (!stored) return;
    let cancelled = false;
    fetch(`/bff/api/v1/chat/sessions/${stored.sessionId}`, { headers: headers(stored), cache: "no-store" })
      .then((response) => (response.ok ? (response.json() as Promise<VisitorChat>) : null))
      .then((loaded) => {
        if (cancelled) return;
        if (!loaded) {
          writeStored(widget.id, null);
          return;
        }
        setSession(stored);
        setStage("chat");
        setChat(loaded);
        setMessages(loaded.messages);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [widget.id, headers]);

  useEffect(() => {
    const last = list.current?.lastElementChild as HTMLElement | null | undefined;
    if (typeof last?.scrollIntoView === "function") last.scrollIntoView({ block: "nearest" });
  }, [messages.length]);

  useChatSignals({
    sessionId: session?.sessionId,
    token: session?.token,
    csrfToken,
    onChange: () => void load(),
    enabled: stage === "chat" && chat?.state !== "Ended",
  });

  async function start(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/bff/api/v1/chat/widgets/${widget.id}/sessions`, {
        method: "POST",
        headers: headers(null),
        body: JSON.stringify({ name: signedIn ? null : name.trim(), email: signedIn ? null : email.trim(), language: locale, message: text.trim() || null, origin }),
      });
      const body = (await response.json().catch(() => ({}))) as { sessionId?: string; token?: string | null; code?: string };
      if (!response.ok || !body.sessionId) {
        if (body.code === "chat-offline") {
          setStage("offline");
          return;
        }
        setError(body.code === "rate-limited" ? t("rateLimited") : response.status === 400 ? t("invalid") : t("failed"));
        return;
      }
      const stored = { sessionId: body.sessionId, token: body.token ?? null };
      writeStored(widget.id, stored);
      setSession(stored);
      setText("");
      setStage("chat");
      await load(stored);
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  async function leave(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/bff/api/v1/chat/widgets/${widget.id}/offline-messages`, {
        method: "POST",
        headers: headers(null),
        body: JSON.stringify({ name: signedIn ? null : name.trim(), email: signedIn ? null : email.trim(), language: locale, message: text.trim(), origin }),
      });
      const body = (await response.json().catch(() => ({}))) as { number?: string; code?: string };
      if (!response.ok || !body.number) {
        setError(body.code === "rate-limited" ? t("rateLimited") : response.status === 400 ? t("invalid") : t("failed"));
        return;
      }
      setTicketNumber(body.number);
      setStage("left");
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  async function write(event: FormEvent) {
    event.preventDefault();
    if (!session || !text.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/bff/api/v1/chat/sessions/${session.sessionId}/messages`, {
        method: "POST",
        headers: headers(session),
        body: JSON.stringify({ body: text.trim() }),
      });
      if (!response.ok) {
        setError(response.status === 429 ? t("rateLimited") : t("failed"));
        return;
      }
      setText("");
      await load();
    } finally {
      setBusy(false);
    }
  }

  async function end() {
    if (!session) return;
    await fetch(`/bff/api/v1/chat/sessions/${session.sessionId}/end`, { method: "POST", headers: headers(session) });
    writeStored(widget.id, null);
    await load();
  }

  function restart() {
    writeStored(widget.id, null);
    setSession(null);
    setChat(null);
    setMessages([]);
    setStage(widget.online ? "start" : "offline");
  }

  const greeting = textIn(widget.greeting, locale);
  const detailsNeeded = !signedIn;

  return (
    <section aria-labelledby={`${id}-title`} className="flex h-full flex-col gap-3 bg-surface p-4 text-ink" data-testid="chat-window">
      <header className="flex items-center justify-between gap-2">
        <h2 id={`${id}-title`} className="text-lg font-semibold">{textIn(widget.name, locale)}</h2>
        {onClose && (
          <button type="button" onClick={onClose} className="rounded px-2 py-1 font-semibold text-brand focus-visible:outline-2 focus-visible:outline-brand">
            {t("close")}
          </button>
        )}
      </header>

      {(stage === "start" || stage === "offline") && (
        <form onSubmit={stage === "start" ? start : leave} className="space-y-3" noValidate>
          <p>{stage === "start" ? greeting || t("startIntro") : t("offlineIntro")}</p>
          {detailsNeeded && (
            <>
              <div>
                <label htmlFor={`${id}-name`} className="font-semibold">{t("name")}</label>
                <input id={`${id}-name`} autoComplete="name" required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
              </div>
              <div>
                <label htmlFor={`${id}-email`} className="font-semibold">{t("email")}</label>
                <input id={`${id}-email`} type="email" dir="ltr" autoComplete="email" required maxLength={254} value={email} onChange={(e) => setEmail(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" aria-describedby={`${id}-email-help`} />
                <p id={`${id}-email-help`} className="text-sm text-ink-muted">{t("emailHelp")}</p>
              </div>
            </>
          )}
          <div>
            <label htmlFor={`${id}-first`} className="font-semibold">{stage === "start" ? t("firstMessage") : t("offlineMessage")}</label>
            <textarea id={`${id}-first`} dir="auto" rows={3} maxLength={4000} required={stage === "offline"} value={text} onChange={(e) => setText(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
          </div>
          {error && <p role="alert" className="text-bad">{error}</p>}
          <button
            type="submit"
            disabled={busy || (detailsNeeded && (!name.trim() || !email.trim())) || (stage === "offline" && text.trim().length < 3)}
            className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60"
          >
            {stage === "start" ? t("start") : t("send")}
          </button>
        </form>
      )}

      {stage === "left" && (
        <div role="status" className="space-y-2">
          <p>{t("left", { number: ticketNumber ?? "" })}</p>
          <button type="button" onClick={restart} className="font-semibold text-brand underline">{t("again")}</button>
        </div>
      )}

      {stage === "chat" && (
        <>
          <p role="status" className="text-sm text-ink-muted">
            {chat?.state === "Active" ? t("with", { agent: chat.agentName ?? "" }) : chat?.state === "Ended" ? t("ended") : t("waiting")}
          </p>
          <ol ref={list} className="flex-1 space-y-2 overflow-y-auto" aria-live="polite" aria-label={t("messages")}>
            {messages.map((message) => (
              <li key={message.id} className={message.sender === "System" ? "text-center text-sm text-ink-muted" : message.sender === "Visitor" ? "ms-8 rounded-md bg-surface-muted p-2" : "me-8 rounded-md border border-line p-2"}>
                {message.sender === "System" ? (
                  t(`events.${message.event === "accepted" || message.event === "transferred" || message.event === "ended" || message.event === "ticket" ? message.event : "other"}`, {
                    agent: message.authorName ?? "",
                    number: message.body,
                  })
                ) : (
                  <>
                    <span className="block text-xs font-semibold">{message.sender === "Visitor" ? t("you") : message.authorName ?? t("agent")}</span>
                    <span dir="auto" className="whitespace-pre-wrap break-words">{message.body}</span>
                  </>
                )}
              </li>
            ))}
          </ol>
          {chat?.state === "Ended" ? (
            <button type="button" onClick={restart} className="font-semibold text-brand underline">{t("again")}</button>
          ) : (
            <form onSubmit={write} className="space-y-2" noValidate>
              <label htmlFor={`${id}-text`} className="sr-only">{t("yourMessage")}</label>
              <textarea id={`${id}-text`} dir="auto" rows={2} maxLength={4000} value={text} onChange={(e) => setText(e.target.value)} placeholder={t("yourMessage")} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
              {error && <p role="alert" className="text-bad">{error}</p>}
              <div className="flex gap-2">
                <button type="submit" disabled={busy || !text.trim()} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60">{t("send")}</button>
                <button type="button" onClick={end} className="rounded-md border border-line px-4 py-2 font-semibold">{t("end")}</button>
              </div>
            </form>
          )}
        </>
      )}
    </section>
  );
}
