"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PublicChatWidget } from "@/lib/channels";
import { ChatWindow } from "./ChatWindow";

/**
 * The portal's chat button (task E2): shown when the organisation has a widget on the portal.
 * Opens the chat in a panel at the end of the page; signed-in people chat as themselves.
 */
export function PortalChat({ signedIn, csrfToken }: { signedIn: boolean; csrfToken: string | null }) {
  const t = useTranslations("channels.chat");
  const locale = useLocale();
  const [widget, setWidget] = useState<PublicChatWidget | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    let cancelled = false;
    fetch("/bff/api/v1/chat/portal-widget", { headers: { Accept: "application/json" }, cache: "no-store" })
      .then((response) => (response.ok ? (response.json() as Promise<PublicChatWidget>) : null))
      .then((found) => {
        if (!cancelled) setWidget(found);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  if (!widget) return null;
  return (
    <div className="fixed bottom-4 end-4 z-20 flex flex-col items-end gap-2">
      {open && (
        <div role="dialog" aria-modal="false" aria-label={t("dialog")} className="h-[32rem] w-[22rem] max-w-[calc(100vw-2rem)] overflow-hidden rounded-lg border border-line shadow-lg">
          <ChatWindow widget={widget} locale={locale} signedIn={signedIn} csrfToken={csrfToken} onClose={() => setOpen(false)} />
        </div>
      )}
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="rounded-full bg-brand px-5 py-3 font-semibold text-surface shadow-lg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
      >
        {open ? t("close") : t("open")}
      </button>
    </div>
  );
}
