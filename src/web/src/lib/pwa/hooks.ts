"use client";

import { useSyncExternalStore } from "react";

function subscribeOnline(onChange: () => void): () => void {
  window.addEventListener("online", onChange);
  window.addEventListener("offline", onChange);
  return () => {
    window.removeEventListener("online", onChange);
    window.removeEventListener("offline", onChange);
  };
}

/** Whether the device has a connection (task J1); true while rendering on the server. */
export function useOnline(): boolean {
  return useSyncExternalStore(subscribeOnline, () => navigator.onLine, () => true);
}

/** Whether a media query matches (task J1), for example "(pointer: coarse)"; false on the server. */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const list = window.matchMedia?.(query);
      list?.addEventListener?.("change", onChange);
      return () => list?.removeEventListener?.("change", onChange);
    },
    () => window.matchMedia?.(query).matches === true,
    () => false,
  );
}
