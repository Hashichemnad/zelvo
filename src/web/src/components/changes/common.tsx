"use client";

import { useTranslations } from "next-intl";
import type { SaveStatus } from "@/components/directory/common";
import type { ChangeRiskLevel, ChangeStage } from "@/lib/changes";

export { useSaver, type SaveStatus } from "@/components/directory/common";

/** The result of the last change to a change, read out by screen readers. */
export function ChangeStatusLine({ status, id }: { status: SaveStatus; id?: string }) {
  const t = useTranslations("changes.status");
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

const stageClasses: Record<ChangeStage, string> = {
  New: "border-line text-ink",
  Authorisation: "border-warn text-warn",
  Scheduled: "border-brand text-brand",
  Implementation: "border-brand text-brand",
  Review: "border-ok text-ok",
  Closed: "border-line text-ink-muted",
};

/** A change's stage as a badge with its name (never colour alone). */
export function ChangeStageBadge({ stage }: { stage: ChangeStage }) {
  const t = useTranslations("changes.stages");
  return <span className={`inline-block rounded-full border px-2 text-xs font-semibold ${stageClasses[stage]}`}>{t(stage)}</span>;
}

const riskClasses: Record<ChangeRiskLevel, string> = {
  Low: "border-ok text-ok",
  Medium: "border-warn text-warn",
  High: "border-bad text-bad",
};

/** A risk level as a badge with its name; "not assessed" when there is none. */
export function RiskBadge({ level }: { level: ChangeRiskLevel | null }) {
  const t = useTranslations("changes.risk");
  return level
    ? <span className={`inline-block rounded-full border px-2 text-xs font-semibold ${riskClasses[level]}`}>{t(`levels.${level}`)}</span>
    : <span className="text-ink-muted">{t("notAssessed")}</span>;
}
