import en from "../../../messages/en.json";
import ar from "../../../messages/ar.json";
import { authConfigFor } from "@/lib/auth/config";
import type { NextRequest } from "next/server";

/**
 * The script other websites embed (task E2):
 * `<script src="https://zelvo…/chat-widget.js" data-widget="…" data-language="ar" async></script>`.
 * It adds a chat button and, when pressed, a frame with the widget page from this server, so the
 * website never sees the chat. Which websites may frame it is set per widget in Settings → Live chat.
 */
export async function GET(request: NextRequest) {
  const appUrl = (await authConfigFor(request)).appUrl.origin;
  const labels = {
    en: { open: en.channels.chat.open, close: en.channels.chat.close, dialog: en.channels.chat.dialog },
    ar: { open: ar.channels.chat.open, close: ar.channels.chat.close, dialog: ar.channels.chat.dialog },
  };
  const script = `(function () {
  var me = document.currentScript;
  if (!me) return;
  var widget = me.getAttribute("data-widget") || "";
  if (!/^[0-9a-f-]{36}$/i.test(widget)) return;
  var lang = me.getAttribute("data-language") === "ar" ? "ar" : "en";
  var text = ${JSON.stringify(labels)}[lang];
  var app = ${JSON.stringify(appUrl)};
  var side = lang === "ar" ? "left" : "right";
  var frame = null;
  var button = document.createElement("button");
  button.type = "button";
  button.textContent = text.open;
  button.setAttribute("aria-expanded", "false");
  button.style.cssText = "position:fixed;bottom:16px;" + side + ":16px;z-index:2147483646;padding:12px 20px;border:0;border-radius:999px;background:#0f4c81;color:#fff;font:600 15px system-ui,sans-serif;cursor:pointer;box-shadow:0 4px 12px rgba(0,0,0,.25)";
  function toggle(open) {
    if (open && !frame) {
      frame = document.createElement("iframe");
      frame.title = text.dialog;
      frame.src = app + "/chat-widget/" + lang + "/" + widget + "?origin=" + encodeURIComponent(location.origin);
      frame.style.cssText = "position:fixed;bottom:76px;" + side + ":16px;z-index:2147483647;width:360px;max-width:calc(100vw - 32px);height:520px;max-height:calc(100vh - 100px);border:1px solid #d1d5db;border-radius:12px;background:#fff;box-shadow:0 8px 24px rgba(0,0,0,.2)";
      frame.setAttribute("allow", "");
      document.body.appendChild(frame);
    }
    if (frame) frame.style.display = open ? "block" : "none";
    button.textContent = open ? text.close : text.open;
    button.setAttribute("aria-expanded", open ? "true" : "false");
  }
  button.addEventListener("click", function () { toggle(!frame || frame.style.display === "none"); });
  window.addEventListener("message", function (event) {
    if (event.origin === app && event.data && event.data.type === "zelvo-chat:close") toggle(false);
  });
  document.body.appendChild(button);
})();
`;
  return new Response(script, {
    headers: {
      "Content-Type": "text/javascript; charset=utf-8",
      "Cache-Control": "public, max-age=300",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
