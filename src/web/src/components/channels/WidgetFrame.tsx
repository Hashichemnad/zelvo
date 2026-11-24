"use client";

import type { PublicChatWidget } from "@/lib/channels";
import { ChatWindow } from "./ChatWindow";

/**
 * The chat inside the widget frame on another website. "Close" asks the embedding page (the loader
 * script) to hide the frame; nothing else is shared with that page.
 */
export function WidgetFrame({ widget, locale, origin, poweredBy, productName = null }: { widget: PublicChatWidget; locale: string; origin: string | null; poweredBy: string; productName?: string | null }) {
  function close() {
    if (origin && window.parent !== window) {
      window.parent.postMessage({ type: "zelvo-chat:close" }, origin);
    }
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex-1 overflow-hidden">
        <ChatWindow widget={widget} locale={locale} signedIn={false} origin={origin} onClose={origin ? close : undefined} />
      </div>
      <p className="border-t border-line px-4 py-2 text-center text-xs text-ink-muted" data-testid="powered-by">{productName && <><span dir="auto">{productName}</span> · </>}{poweredBy}</p>
    </div>
  );
}
