"use client";

import { useRef } from "react";
import { useTranslations } from "next-intl";
import { useMediaQuery } from "@/lib/pwa/hooks";

const photoTypes = ["jpg", "jpeg", "png", "webp", "heic"];
const videoTypes = ["mp4", "mov", "webm"];

/** Whether the file types allowed here include photos and videos a phone camera makes. */
export function cameraKinds(allowedExtensions: readonly string[] | null | undefined): { photo: boolean; video: boolean } {
  const allowed = (allowedExtensions ?? []).map((e) => e.toLowerCase().replace(/^\./, ""));
  if (allowed.length === 0) return { photo: true, video: true };
  return { photo: photoTypes.some((x) => allowed.includes(x)), video: videoTypes.some((x) => allowed.includes(x)) };
}

/**
 * Task J1 (module 19): "Take a photo" and "Record a video" straight from the device's camera,
 * beside a file picker. The files go through the same checked and scanned upload as any other file
 * (the caller's `onFile`). Shown on touch devices with a camera app; on computers the file picker
 * is enough. Only the kinds the place accepts are offered (videos when the tenant allows them).
 */
export function CameraButtons({ allowed, onFile, disabled, className = "" }: {
  /** File extensions accepted here, or empty for any. */
  allowed?: readonly string[] | null;
  onFile: (file: File) => void;
  disabled?: boolean;
  className?: string;
}) {
  const t = useTranslations("app.camera");
  const photo = useRef<HTMLInputElement>(null);
  const video = useRef<HTMLInputElement>(null);
  const touch = useMediaQuery("(pointer: coarse)");
  const kinds = cameraKinds(allowed);

  if (!touch || (!kinds.photo && !kinds.video)) return null;

  const pick = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (file) onFile(file);
  };
  const button = "rounded-md border border-line px-3 py-2 text-sm font-semibold disabled:opacity-60";
  return (
    <div className={`flex flex-wrap gap-2 ${className}`} data-testid="camera-buttons">
      {kinds.photo && (
        <>
          <button type="button" className={button} disabled={disabled} onClick={() => photo.current?.click()}>
            {t("photo")}
          </button>
          <input ref={photo} type="file" accept="image/*" capture="environment" hidden tabIndex={-1} aria-hidden="true" onChange={pick} />
        </>
      )}
      {kinds.video && (
        <>
          <button type="button" className={button} disabled={disabled} onClick={() => video.current?.click()}>
            {t("video")}
          </button>
          <input ref={video} type="file" accept="video/*" capture="environment" hidden tabIndex={-1} aria-hidden="true" onChange={pick} />
        </>
      )}
    </div>
  );
}
