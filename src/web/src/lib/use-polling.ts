"use client";

import { useEffect, useRef } from "react";

/**
 * Calls `callback` every `ms` milliseconds while `enabled` and the page is visible (task E2: chats
 * reload through the API; visitors have no live connection, agents poll only while it is down).
 */
export function usePolling(callback: () => void, ms: number, enabled = true): void {
  const latest = useRef(callback);
  useEffect(() => {
    latest.current = callback;
  });
  useEffect(() => {
    if (!enabled) return;
    const timer = setInterval(() => {
      if (typeof document === "undefined" || document.visibilityState !== "hidden") latest.current();
    }, ms);
    return () => clearInterval(timer);
  }, [ms, enabled]);
}
