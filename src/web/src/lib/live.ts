"use client";

import { useEffect, useRef, useState } from "react";
import { HttpTransportType, HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";

/**
 * Live updates (task B4) over SignalR, through the web app's proxy (/bff/api/v1/live), which adds
 * the session's token: the browser never holds one. Messages carry ids only; components load what
 * changed through the API. One connection per page, shared by every component that listens.
 */

export type LiveStatus = "connecting" | "live" | "offline";

type Handler = (...args: unknown[]) => void;

interface Shared {
  connection: HubConnection;
  users: number;
  statusListeners: Set<(status: LiveStatus) => void>;
  status: LiveStatus;
}

let shared: Shared | null = null;

function setStatus(status: LiveStatus) {
  if (!shared) return;
  shared.status = status;
  for (const listener of shared.statusListeners) listener(status);
}

function acquire(csrfToken: string): Shared {
  if (shared) {
    shared.users += 1;
    return shared;
  }
  const connection = new HubConnectionBuilder()
    .withUrl("/bff/api/v1/live", {
      // WebSockets cannot pass through the proxy; Server-Sent Events first, long polling otherwise.
      transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
      headers: { "X-CSRF-Token": csrfToken },
      withCredentials: true,
    })
    // Keep trying: 0 s, 2 s, 10 s, 30 s, then every minute.
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (retry) => [0, 2000, 10000, 30000][retry.previousRetryCount] ?? 60000 })
    .configureLogging(LogLevel.None)
    .build();
  shared = { connection, users: 1, statusListeners: new Set(), status: "connecting" };
  connection.onreconnecting(() => setStatus("connecting"));
  connection.onreconnected(() => setStatus("live"));
  connection.onclose(() => setStatus("offline"));
  const start = () => {
    connection.start().then(() => setStatus("live")).catch(() => {
      setStatus("offline");
      // The first start does not reconnect on its own; try again in a while.
      setTimeout(() => {
        if (shared?.connection === connection && connection.state === HubConnectionState.Disconnected) start();
      }, 30000);
    });
  };
  start();
  return shared;
}

function release() {
  if (!shared) return;
  shared.users -= 1;
  if (shared.users <= 0) {
    const { connection } = shared;
    shared = null;
    void connection.stop();
  }
}

/**
 * Listens to live messages while the component is shown. `handlers` maps message names
 * (ticketChanged, refresh, notificationsChanged) to callbacks; the latest callbacks are used
 * without reconnecting. Returns the connection status, so screens can say when updates are not live.
 */
export function useLive(csrfToken: string | null, handlers: Record<string, Handler>): LiveStatus {
  const latest = useRef(handlers);
  const [status, setLocalStatus] = useState<LiveStatus>("connecting");
  useEffect(() => {
    latest.current = handlers;
  });
  const names = Object.keys(handlers).sort().join(",");
  useEffect(() => {
    if (!csrfToken || typeof window === "undefined") return;
    const live = acquire(csrfToken);
    const bound = names.split(",").filter(Boolean).map((name) => {
      const handler: Handler = (...args) => latest.current[name]?.(...args);
      live.connection.on(name, handler);
      return [name, handler] as const;
    });
    live.statusListeners.add(setLocalStatus);
    setLocalStatus(live.status);
    return () => {
      for (const [name, handler] of bound) live.connection.off(name, handler);
      live.statusListeners.delete(setLocalStatus);
      release();
    };
  }, [csrfToken, names]);
  return status;
}
