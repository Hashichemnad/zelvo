"use client";

import { useCallback, useEffect, useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { useChatSignals } from "@/lib/chat-signals";
import { ApiError, getJson, postJson, putJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { mergeMessages, textIn, type AgentChat, type ChatMessage, type ChatQueue, type ChatSummary } from "@/lib/channels";

interface GroupDetail {
  id: string;
  name: { en: string | null; ar: string | null };
  members: { userId: string; displayName: string | null; availableForAssignment: boolean }[];
}

/**
 * The agent's live chats (task E2): chats waiting in their groups, the chats they answer (up to the
 * group's limit) and the open chat with transfer, end and "make a ticket". The hub says when
 * something changed; the lists reload through the API.
 */
export function AgentChats({
  initial,
  csrfToken,
  canCreateTickets,
  display,
}: {
  initial: ChatQueue;
  csrfToken: string;
  canCreateTickets: boolean;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("channels.agent");
  const [queue, setQueue] = useState(initial);
  const [openId, setOpenId] = useState<string | null>(initial.mine[0]?.id ?? null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      setQueue(await getJson<ChatQueue>("/v1/chat/queue"));
    } catch {
      // The next signal or poll tries again.
    }
  }, []);

  useChatSignals({ csrfToken, onChange: () => void reload() });

  async function accept(chat: ChatSummary) {
    setError(null);
    try {
      await postJson(`/v1/chat/sessions/${chat.id}/accept`, {}, csrfToken);
      setOpenId(chat.id);
    } catch (refused) {
      const code = refused instanceof ApiError ? refused.code : null;
      setError(t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other"));
    }
    await reload();
  }

  async function setAvailable(available: boolean) {
    setError(null);
    try {
      await putJson("/v1/me/availability", { available }, csrfToken);
    } catch {
      setError(t("errors.other"));
    }
    await reload();
  }

  return (
    <div className="grid gap-6 lg:grid-cols-[18rem_1fr]" data-testid="agent-chats">
      <div className="space-y-6">
        <p className="text-sm">
          {queue.available ? t("available", { limit: queue.limit }) : t("unavailableNote")}{" "}
          <button type="button" className="font-semibold text-brand underline" onClick={() => setAvailable(!queue.available)}>
            {queue.available ? t("goAway") : t("goAvailable")}
          </button>
        </p>
        {error && <p role="alert" className="text-bad">{error}</p>}
        <ChatList title={t("waiting")} empty={t("noneWaiting")} chats={queue.waiting} display={display}
          action={(chat) => (
            <button type="button" className="rounded-md bg-brand px-3 py-1 text-sm font-semibold text-surface disabled:opacity-60" disabled={!queue.available} onClick={() => accept(chat)}>
              {t("accept")}<span className="sr-only">: {chat.visitorName}</span>
            </button>
          )}
        />
        <ChatList title={t("mine", { count: queue.mine.length, limit: queue.limit })} empty={t("noneMine")} chats={queue.mine} display={display} selected={openId}
          action={(chat) => (
            <button type="button" className="text-sm font-semibold text-brand underline" aria-pressed={openId === chat.id} onClick={() => setOpenId(chat.id)}>
              {t("open")}<span className="sr-only">: {chat.visitorName}</span>
            </button>
          )}
        />
        <ChatList title={t("recent")} empty={t("noneRecent")} chats={queue.recent} display={display} selected={openId}
          action={(chat) => (
            <button type="button" className="text-sm font-semibold text-brand underline" onClick={() => setOpenId(chat.id)}>
              {t("view")}<span className="sr-only">: {chat.visitorName}</span>
            </button>
          )}
        />
      </div>
      {openId ? (
        <ChatPanel key={openId} sessionId={openId} csrfToken={csrfToken} canCreateTickets={canCreateTickets} display={display} onChanged={reload} />
      ) : (
        <p className="text-ink-muted">{t("pick")}</p>
      )}
    </div>
  );
}

function ChatList({
  title,
  empty,
  chats,
  display,
  selected,
  action,
}: {
  title: string;
  empty: string;
  chats: ChatSummary[];
  display: DateDisplayOptions;
  selected?: string | null;
  action: (chat: ChatSummary) => React.ReactNode;
}) {
  const locale = useLocale();
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-2">
      <h2 id={id} className="font-semibold">{title}</h2>
      {chats.length === 0 ? (
        <p className="text-sm text-ink-muted">{empty}</p>
      ) : (
        <ul className="space-y-2">
          {chats.map((chat) => (
            <li key={chat.id} className={`rounded-md border p-2 ${selected === chat.id ? "border-brand" : "border-line"}`} data-testid="chat-summary">
              <p className="font-semibold" dir="auto">{chat.visitorName}</p>
              <p className="text-xs text-ink-muted">
                {textIn(chat.group, locale)} · <time dateTime={chat.startedAt}>{formatDateTime(new Date(chat.startedAt), display)}</time>
              </p>
              {chat.lastMessage && <p className="truncate text-sm" dir="auto">{chat.lastMessage}</p>}
              <div className="mt-1">{action(chat)}</div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function ChatPanel({
  sessionId,
  csrfToken,
  canCreateTickets,
  display,
  onChanged,
}: {
  sessionId: string;
  csrfToken: string;
  canCreateTickets: boolean;
  display: DateDisplayOptions;
  onChanged: () => Promise<void>;
}) {
  const t = useTranslations("channels.agent");
  const tChat = useTranslations("channels.chat");
  const locale = useLocale();
  const id = useId();
  const [chat, setChat] = useState<AgentChat | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [text, setText] = useState("");
  const [title, setTitle] = useState<string | null>(null);
  const [group, setGroup] = useState<GroupDetail | null>(null);
  const [groups, setGroups] = useState<{ id: string; name: { en: string | null; ar: string | null } }[]>([]);
  const [target, setTarget] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    const last = messages.at(-1);
    const after = last ? `?after=${last.sequence}` : "";
    try {
      const loaded = await getJson<AgentChat>(`/v1/chat/sessions/${sessionId}/agent${after}`);
      setChat(loaded);
      setMessages((current) => mergeMessages(current, loaded.messages));
    } catch {
      // Gone from the caller's groups (transferred): the list reloads.
    }
  }, [sessionId, messages]);

  useEffect(() => {
    let cancelled = false;
    getJson<AgentChat>(`/v1/chat/sessions/${sessionId}/agent`)
      .then((loaded) => {
        if (cancelled) return;
        setChat(loaded);
        setMessages(loaded.messages);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  const groupId = chat?.group.id;
  useEffect(() => {
    if (!groupId) return;
    getJson<GroupDetail>(`/v1/groups/${groupId}`).then(setGroup).catch(() => setGroup(null));
    getJson<{ items: { id: string; name: { en: string | null; ar: string | null }; active: boolean }[] } | { id: string; name: { en: string | null; ar: string | null }; active: boolean }[]>("/v1/groups")
      .then((found) => setGroups((Array.isArray(found) ? found : found.items).filter((g) => g.active)))
      .catch(() => setGroups([]));
  }, [groupId]);

  useChatSignals({ sessionId, csrfToken, onChange: () => void load(), enabled: chat?.state !== "Ended" });

  async function act(action: () => Promise<unknown>) {
    setError(null);
    try {
      await action();
      await load();
      await onChanged();
    } catch (refused) {
      const code = refused instanceof ApiError ? refused.code : null;
      setError(t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other"));
    }
  }

  async function write(event: FormEvent) {
    event.preventDefault();
    if (!text.trim()) return;
    await act(() => postJson(`/v1/chat/sessions/${sessionId}/agent-messages`, { body: text.trim() }, csrfToken));
    setText("");
  }

  if (!chat) {
    return <p role="status">{t("loading")}</p>;
  }
  const active = chat.state === "Active";
  // The title starts as the visitor's first line.
  const ticketTitle = title ?? (messages.find((m) => m.sender === "Visitor")?.body.split("\n")[0] ?? "").slice(0, 120);

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4 rounded-md border border-line p-4" data-testid="chat-panel">
      <header className="space-y-1">
        <h2 id={`${id}-title`} className="text-xl font-semibold" dir="auto">{chat.visitorName}</h2>
        <p className="text-sm text-ink-muted">
          <span dir="ltr">{chat.visitorEmail}</span> · {t(`languages.${chat.language === "ar" ? "ar" : "en"}`)} · {textIn(chat.group.name, locale)}
          {chat.origin && <> · <span dir="ltr">{chat.origin}</span></>}
        </p>
        <p className="text-sm">{t(`states.${chat.state}`)}</p>
        {chat.ticketId && (
          <p>
            <Link href={`/incidents/${chat.ticketId}`} className="font-semibold text-brand underline">{t("ticket", { number: chat.ticketNumber ?? "" })}</Link>
          </p>
        )}
      </header>
      <ol className="max-h-96 space-y-2 overflow-y-auto" aria-live="polite" aria-label={tChat("messages")}>
        {messages.map((message) => (
          <li key={message.id} className={message.sender === "System" ? "text-center text-sm text-ink-muted" : message.sender === "Agent" ? "ms-8 rounded-md bg-surface-muted p-2" : "me-8 rounded-md border border-line p-2"}>
            {message.sender === "System" ? (
              tChat(`events.${["accepted", "transferred", "ended", "ticket"].includes(message.event ?? "") ? message.event : "other"}`, { agent: message.authorName ?? "", number: message.body })
            ) : (
              <>
                <span className="block text-xs font-semibold">
                  {message.sender === "Visitor" ? chat.visitorName : message.authorName ?? tChat("agent")} ·{" "}
                  <time dateTime={message.createdAt}>{formatDateTime(new Date(message.createdAt), display)}</time>
                </span>
                <span dir="auto" className="whitespace-pre-wrap break-words">{message.body}</span>
              </>
            )}
          </li>
        ))}
      </ol>
      {error && <p role="alert" className="text-bad">{error}</p>}
      {active && (
        <form onSubmit={write} className="space-y-2" noValidate>
          <label htmlFor={`${id}-text`} className="font-semibold">{t("answer")}</label>
          <textarea id={`${id}-text`} dir="auto" rows={3} maxLength={4000} value={text} onChange={(e) => setText(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" />
          <button type="submit" disabled={!text.trim()} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface disabled:opacity-60">{tChat("send")}</button>
        </form>
      )}
      {chat.state !== "Ended" && (
        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label htmlFor={`${id}-transfer`} className="block text-sm font-semibold">{t("transferTo")}</label>
            <select id={`${id}-transfer`} value={target} onChange={(e) => setTarget(e.target.value)} className="rounded-md border border-line bg-surface px-3 py-2">
              <option value="">{t("choose")}</option>
              <optgroup label={t("colleagues")}>
                {(group?.members ?? []).filter((m) => m.userId !== chat.agent?.id && m.availableForAssignment).map((m) => (
                  <option key={m.userId} value={`user:${m.userId}`}>{m.displayName ?? m.userId}</option>
                ))}
              </optgroup>
              <optgroup label={t("groups")}>
                {groups.filter((g) => g.id !== chat.group.id).map((g) => (
                  <option key={g.id} value={`group:${g.id}`}>{textIn(g.name, locale)}</option>
                ))}
              </optgroup>
            </select>
          </div>
          <button
            type="button"
            disabled={!target}
            className="rounded-md border border-line px-4 py-2 font-semibold disabled:opacity-60"
            onClick={() => {
              const [kind, value] = target.split(":");
              void act(() => postJson(`/v1/chat/sessions/${sessionId}/transfer`, kind === "user" ? { userId: value } : { groupId: value }, csrfToken));
              setTarget("");
            }}
          >
            {t("transfer")}
          </button>
          <button type="button" className="rounded-md border border-bad px-4 py-2 font-semibold text-bad" onClick={() => act(() => postJson(`/v1/chat/sessions/${sessionId}/close`, {}, csrfToken))}>
            {t("end")}
          </button>
        </div>
      )}
      {!chat.ticketId && canCreateTickets && (
        <form
          className="space-y-2 border-t border-line pt-4"
          onSubmit={(event) => {
            event.preventDefault();
            void act(() => postJson(`/v1/chat/sessions/${sessionId}/ticket`, { title: ticketTitle.trim() || null }, csrfToken));
          }}
        >
          <label htmlFor={`${id}-title-input`} className="font-semibold">{t("ticketTitle")}</label>
          <input id={`${id}-title-input`} dir="auto" maxLength={300} value={ticketTitle} onChange={(e) => setTitle(e.target.value)} className="block w-full rounded-md border border-line bg-surface px-3 py-2" aria-describedby={`${id}-ticket-help`} />
          <p id={`${id}-ticket-help`} className="text-sm text-ink-muted">{t("ticketHelp")}</p>
          <button type="submit" className="rounded-md border border-line px-4 py-2 font-semibold">{t("makeTicket")}</button>
        </form>
      )}
    </section>
  );
}
