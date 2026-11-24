"use client";

import { useEffect, useRef } from "react";
import { HubConnectionBuilder, HttpTransportType, LogLevel, type HubConnection } from "@microsoft/signalr";

/** How often clients reload when the live chat hub cannot be reached. */
export const pollIntervalMs = 5000;

export interface ChatSignalOptions {
  /** The chat to follow; none: the agent's queues and chats. */
  sessionId?: string | null;
  /** Visitors: the chat's token. */
  token?: string | null;
  /** Signed-in people: the session's CSRF token for the proxy. */
  csrfToken?: string | null;
  /** Called when something changed (and every few seconds without the hub). */
  onChange: () => void;
  /** Off while there is nothing to follow. */
  enabled?: boolean;
}

/**
 * Follows a chat or an agent's queue (task E2): the live chat hub (SignalR, through the web app's
 * proxy, server-sent events or long polling) says when something changed, and `onChange` reloads
 * through the API. Without the hub, the page reloads every few seconds instead.
 */
export function useChatSignals({ sessionId, token, csrfToken, onChange, enabled = true }: ChatSignalOptions) {
  const callback = useRef(onChange);
  useEffect(() => {
    callback.current = onChange;
  }, [onChange]);

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let connection: HubConnection | null = null;
    const timer = setInterval(() => callback.current(), pollIntervalMs);
    const headers: Record<string, string> = {};
    if (csrfToken) headers["X-CSRF-Token"] = csrfToken;

    async function watch(hub: HubConnection) {
      const response = await fetch("/bff/api/v1/chat/watch", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", ...headers },
        body: JSON.stringify(sessionId ? { sessionId, token } : {}),
      });
      if (!response.ok) return;
      const { key } = (await response.json()) as { key: string };
      await hub.invoke("Watch", key);
    }

    async function start() {
      try {
        connection = new HubConnectionBuilder()
          .withUrl("/bff/api/v1/chat/hub", {
            transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            headers,
            withCredentials: true,
          })
          .withAutomaticReconnect()
          .configureLogging(LogLevel.None)
          .build();
        const changed = () => callback.current();
        connection.on("chatChanged", changed);
        connection.on("queueChanged", changed);
        connection.onreconnected(() => {
          if (connection) void watch(connection).catch(() => undefined);
        });
        await connection.start();
        if (stopped) {
          await connection.stop();
          return;
        }
        await watch(connection);
      } catch {
        // Polling carries on.
      }
    }

    void start();
    return () => {
      stopped = true;
      clearInterval(timer);
      void connection?.stop().catch(() => undefined);
    };
  }, [sessionId, token, csrfToken, enabled]);
}
