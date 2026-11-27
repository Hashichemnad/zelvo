"use client";

import { useId, useMemo, useState } from "react";
import { useTranslations } from "next-intl";
import { encode } from "uqr";
import { ApiError, putJson } from "@/lib/client-api";
import type { CustomDomain } from "@/lib/branding";

/** The QR code of an address as SVG path data (one square per dark module), with its size in modules. */
export function qrPath(address: string): { size: number; path: string } {
  const qr = encode(address, { ecc: "M", border: 2 });
  let path = "";
  qr.data.forEach((row, y) => {
    row.forEach((dark, x) => {
      if (dark) path += `M${x} ${y}h1v1h-1z`;
    });
  });
  return { size: qr.size, path };
}

/**
 * Settings → Branding → Installable app (module 19, task J1): a QR code that opens the
 * installation's address (or a verified custom domain), so people reach their organisation's own
 * app with its branding; and whether the app keeps data on the device for offline use
 * (decision 13). The app icon is one of the branding images above.
 */
export function InstallableApp({ initial, domains, csrfToken }: {
  initial: { offlineData: boolean; address: string };
  domains: CustomDomain[];
  csrfToken: string;
}) {
  const t = useTranslations("settings.branding.app");
  const id = useId();
  const addresses = useMemo(
    () => [...new Set([initial.address, ...domains.filter((d) => d.status === "Verified").map((d) => `https://${d.host}`)])],
    [initial.address, domains],
  );
  const [address, setAddress] = useState(addresses[0]!);
  const [offlineData, setOfflineData] = useState(initial.offlineData);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const qr = useMemo(() => qrPath(address), [address]);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${qr.size} ${qr.size}" shape-rendering="crispEdges"><rect width="100%" height="100%" fill="#ffffff"/><path fill="#000000" d="${qr.path}"/></svg>`;
  const download = useMemo(() => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`, [svg]);

  async function save(value: boolean) {
    setMessage(null);
    try {
      const saved = await putJson<{ offlineData: boolean }>("/v1/settings/app", { offlineData: value }, csrfToken);
      setOfflineData(saved.offlineData);
      setMessage({ ok: true, text: t("saved") });
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError && error.rateLimited ? t("rateLimited") : t("failed") });
    }
  }

  return (
    <section aria-labelledby={`${id}-title`} className="space-y-4" data-testid="installable-app">
      <h2 id={`${id}-title`} className="text-xl font-semibold">{t("title")}</h2>
      <p className="max-w-3xl text-sm text-ink-muted">{t("intro")}</p>
      <div className="flex flex-wrap items-start gap-6">
        <figure className="space-y-2">
          <svg role="img" aria-labelledby={`${id}-qr`} viewBox={`0 0 ${qr.size} ${qr.size}`} className="h-48 w-48 rounded-md border border-line" shapeRendering="crispEdges" data-testid="app-qr">
            <title id={`${id}-qr`}>{t("qrAlt", { address })}</title>
            <rect width="100%" height="100%" fill="#ffffff" />
            <path fill="#000000" d={qr.path} />
          </svg>
          <figcaption className="text-sm break-all" dir="ltr">{address}</figcaption>
        </figure>
        <div className="space-y-3">
          {addresses.length > 1 && (
            <label className="block space-y-1 text-sm">
              <span className="block font-semibold">{t("address")}</span>
              <select value={address} onChange={(e) => setAddress(e.target.value)} className="block rounded-md border border-line bg-surface px-3 py-2" dir="ltr">
                {addresses.map((a) => <option key={a} value={a}>{a}</option>)}
              </select>
            </label>
          )}
          <p className="max-w-md text-sm">{t("qrHelp")}</p>
          <a href={download} download="app-qr-code.svg" className="inline-block rounded-md border border-line px-4 py-2 font-semibold">
            {t("downloadQr")}
          </a>
        </div>
      </div>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("offlineTitle")}</legend>
        <label className="flex items-start gap-2">
          <input type="checkbox" checked={offlineData} onChange={(e) => void save(e.target.checked)} className="mt-1" aria-describedby={`${id}-offline-help`} />
          <span>{t("offlineData")}</span>
        </label>
        <p id={`${id}-offline-help`} className="max-w-3xl text-sm text-ink-muted">{t("offlineHelp")}</p>
      </fieldset>
      <p role="status" aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ok"}>{message?.text ?? ""}</p>
    </section>
  );
}
