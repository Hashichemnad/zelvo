"use client";

import { useTranslations } from "next-intl";
import type { SaveStatus } from "@/components/directory/common";
import type { ServiceState } from "@/lib/catalog";

const tone: Record<ServiceState, string> = {
  Draft: "border-line text-ink-muted",
  UnderReview: "border-warn text-warn",
  Active: "border-ok text-ok",
  Suspended: "border-warn text-warn",
  Retired: "border-line text-ink-muted",
};

/** A service's state as a labelled badge (text, not colour alone, carries the meaning). */
export function ServiceStateBadge({ state }: { state: ServiceState }) {
  const t = useTranslations("catalog.states");
  return <span className={`inline-block rounded-full border px-2 py-0.5 text-xs font-semibold ${tone[state]}`}>{t(state)}</span>;
}

/** The result of the last catalog action, read out by screen readers; API problem codes get their own text. */
export function CatalogStatusLine({ status, id }: { status: SaveStatus; id?: string }) {
  const t = useTranslations("catalog");
  const tCommon = useTranslations("common");
  let text = "";
  if (status.kind === "saving") text = t("saving");
  else if (status.kind === "saved") text = t("saved");
  else if (status.kind === "failed") {
    const code = status.code;
    text = code === "rate-limited" ? tCommon("rateLimited") : code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("failed");
  }
  return (
    <p id={id} role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ok"}>
      {text}
    </p>
  );
}
