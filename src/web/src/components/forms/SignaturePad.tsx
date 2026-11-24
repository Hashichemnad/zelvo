"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { fieldButton } from "./ui";

/**
 * Draw a signature with a mouse, finger or stylus (product owner decision 2026-11-24). The drawing
 * is saved as a PNG, uploaded and scanned like any file. People who cannot draw type their name
 * instead (the other choice of the signature field), so keyboard and screen-reader users can sign.
 */
export function SignaturePad({ id, describedBy, disabled, onSave }: { id: string; describedBy?: string; disabled?: boolean; onSave: (png: Blob) => Promise<void> }) {
  const t = useTranslations("forms.renderer");
  const canvas = useRef<HTMLCanvasElement>(null);
  const drawing = useRef(false);
  const [hasInk, setHasInk] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    const c = canvas.current;
    const context = c?.getContext("2d");
    if (!c || !context) return;
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, c.width, c.height);
    context.lineWidth = 2.5;
    context.lineCap = "round";
    context.strokeStyle = "#111827";
  }, []);

  function point(event: React.PointerEvent<HTMLCanvasElement>) {
    const c = canvas.current!;
    const rect = c.getBoundingClientRect();
    return { x: ((event.clientX - rect.left) * c.width) / rect.width, y: ((event.clientY - rect.top) * c.height) / rect.height };
  }

  function clear() {
    const c = canvas.current;
    const context = c?.getContext("2d");
    if (!c || !context) return;
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, c.width, c.height);
    setHasInk(false);
  }

  return (
    <div className="space-y-2">
      <canvas
        id={id}
        ref={canvas}
        width={600}
        height={200}
        role="img"
        aria-label={t("signatureArea")}
        aria-describedby={describedBy}
        className="block h-auto w-full max-w-xl touch-none rounded-md border border-line bg-white"
        onPointerDown={(e) => {
          if (disabled) return;
          const context = canvas.current?.getContext("2d");
          if (!context) return;
          drawing.current = true;
          e.currentTarget.setPointerCapture?.(e.pointerId);
          const { x, y } = point(e);
          context.beginPath();
          context.moveTo(x, y);
        }}
        onPointerMove={(e) => {
          if (!drawing.current) return;
          const context = canvas.current?.getContext("2d");
          if (!context) return;
          const { x, y } = point(e);
          context.lineTo(x, y);
          context.stroke();
          setHasInk(true);
        }}
        onPointerUp={() => {
          drawing.current = false;
        }}
        onPointerLeave={() => {
          drawing.current = false;
        }}
      />
      <div className="flex flex-wrap gap-2">
        <button type="button" className={fieldButton} disabled={disabled || !hasInk} onClick={clear}>
          {t("signatureClear")}
        </button>
        <button
          type="button"
          className={fieldButton}
          disabled={disabled || !hasInk || saving}
          onClick={() => {
            setSaving(true);
            canvas.current?.toBlob(async (blob) => {
              if (blob) await onSave(blob);
              setSaving(false);
            }, "image/png");
          }}
        >
          {t("signatureUse")}
        </button>
      </div>
      <p className="text-sm text-ink-muted">{t("signatureDrawHint")}</p>
    </div>
  );
}
