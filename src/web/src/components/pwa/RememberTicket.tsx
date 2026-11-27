"use client";

import { useEffect } from "react";
import { rememberTicket, type OfflineTicket } from "@/lib/pwa/offline-store";

/**
 * Task J1 (decision 13): keeps the requester's own ticket on the device as they see it now, for the
 * offline page. Only requester pages render it, never staff pages, and only while the tenant
 * allows offline data.
 */
export function RememberTicket({ ticket, enabled }: { ticket: Omit<OfflineTicket, "viewedAt">; enabled: boolean }) {
  const snapshot = JSON.stringify(ticket);
  useEffect(() => {
    if (!enabled) return;
    void rememberTicket(JSON.parse(snapshot) as Omit<OfflineTicket, "viewedAt">).catch(() => undefined);
  }, [snapshot, enabled]);
  return null;
}
