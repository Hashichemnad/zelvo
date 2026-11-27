"use client";

import { useTranslations } from "next-intl";
import type { SaveStatus } from "@/components/directory/common";
import type { ProblemStage } from "@/lib/problems";

export { useSaver, type SaveStatus } from "@/components/directory/common";

/** The result of the last change to a problem, read out by screen readers. */
export function ProblemStatusLine({ status, id }: { status: SaveStatus; id?: string }) {
  const t = useTranslations("problems.status");
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

const stageClasses: Record<ProblemStage, string> = {
  New: "border-line text-ink",
  Assessment: "border-brand text-brand",
  RootCauseAnalysis: "border-warn text-warn",
  KnownError: "border-warn text-warn",
  Resolved: "border-ok text-ok",
  Closed: "border-line text-ink-muted",
};

/** A problem's stage as a badge with its name (never colour alone). */
export function ProblemStageBadge({ stage }: { stage: ProblemStage }) {
  const t = useTranslations("problems.stages");
  return <span className={`inline-block rounded-full border px-2 text-xs font-semibold ${stageClasses[stage]}`}>{t(stage)}</span>;
}
