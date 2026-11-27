"use client";

import { useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, getJson } from "@/lib/client-api";
import { normaliseTag, tagPattern, type AssetSummary } from "@/lib/assets";
import { primaryButton, secondaryButton } from "@/components/settings/integrations/ui";
import { fieldInput } from "@/components/forms/ui";

/**
 * Reads the asset tag of a label (QR code or Code 128) from a picture (decision 16 of 2026-11-26:
 * a ZXing-based library). On phones the picture comes from the camera app (`capture`), so the
 * page itself never needs camera access (`Permissions-Policy: camera=()` stays); the library is
 * loaded only when a picture is chosen.
 */
export async function decodeLabel(file: Blob): Promise<string | null> {
  const [{ BrowserMultiFormatReader }, { BarcodeFormat, DecodeHintType }] = await Promise.all([import("@zxing/browser"), import("@zxing/library")]);
  const hints = new Map<number, unknown>([
    [DecodeHintType.POSSIBLE_FORMATS, [BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128]],
    [DecodeHintType.TRY_HARDER, true],
  ]);
  const url = URL.createObjectURL(file);
  try {
    const result = await new BrowserMultiFormatReader(hints as Map<never, unknown>).decodeFromImageUrl(url);
    return result.getText();
  } catch {
    return null;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Scan an asset label (task G3): take a picture of the label with the phone's camera (or choose
 * one), or type the tag, which is the equal for keyboard and screen-reader users. The asset found
 * is passed to <paramref name="onFound"/>: the scan page opens it, a ticket adds it.
 */
export function AssetScanner({ onFound, label, decode = decodeLabel }: { onFound: (asset: AssetSummary) => void; label?: string; decode?: (file: Blob) => Promise<string | null> }) {
  const t = useTranslations("assets.scan");
  const id = useId();
  const camera = useRef<HTMLInputElement>(null);
  const [tag, setTag] = useState("");
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function find(text: string) {
    const wanted = normaliseTag(text);
    if (!tagPattern.test(wanted)) {
      setStatus({ ok: false, text: t("notATag") });
      return;
    }
    setBusy(true);
    try {
      const asset = await getJson<AssetSummary>(`/v1/assets/lookup?${new URLSearchParams({ tag: wanted })}`);
      setStatus({ ok: true, text: t("found", { tag: asset.tag, name: asset.name }) });
      onFound(asset);
    } catch (error) {
      setStatus({ ok: false, text: error instanceof ApiError && error.status === 404 ? t("unknown", { tag: wanted }) : t("failed") });
    } finally {
      setBusy(false);
    }
  }

  async function picture(file: File | undefined) {
    if (!file) return;
    setBusy(true);
    setStatus({ ok: true, text: t("reading") });
    const text = await decode(file);
    setBusy(false);
    if (!text) {
      setStatus({ ok: false, text: t("unreadable") });
      return;
    }
    setTag(text);
    await find(text);
  }

  return (
    <div className="space-y-3" data-testid="asset-scanner">
      <div className="flex flex-wrap items-center gap-2">
        <input ref={camera} id={`${id}-camera`} type="file" accept="image/*" capture="environment" hidden tabIndex={-1} aria-hidden="true"
          onChange={(e) => {
            void picture(e.target.files?.[0]);
            e.target.value = "";
          }} />
        <button type="button" className={primaryButton} disabled={busy} onClick={() => camera.current?.click()}>{label ?? t("takePicture")}</button>
      </div>
      <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => { e.preventDefault(); void find(tag); }}>
        <div className="space-y-1">
          <label htmlFor={`${id}-tag`} className="block text-sm font-semibold">{t("typeTag")}</label>
          <input id={`${id}-tag`} dir="ltr" value={tag} maxLength={32} autoComplete="off" onChange={(e) => setTag(e.target.value.toUpperCase())} className={`${fieldInput} max-w-48 font-mono`} />
        </div>
        <button type="submit" className={secondaryButton} disabled={busy || !tag.trim()}>{t("find")}</button>
      </form>
      <p role="status" aria-live="polite" className={status?.ok === false ? "font-semibold text-bad" : "text-ok"}>{status?.text ?? ""}</p>
    </div>
  );
}
