"use client";

import { useRouter } from "@/i18n/navigation";
import { AssetScanner } from "./AssetScanner";

/** The scan page's scanner: a label read (or typed) opens its asset. */
export function ScanToOpen() {
  const router = useRouter();
  return <AssetScanner onFound={(asset) => router.push(`/assets/${asset.id}`)} />;
}
