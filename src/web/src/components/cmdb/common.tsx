"use client";

import { useTranslations } from "next-intl";
import type { SaveStatus } from "@/components/directory/common";
import type { CiFamily, CiStatus } from "@/lib/cmdb";

export { useSaver, type SaveStatus } from "@/components/directory/common";

/** The result of the last CMDB change, read out by screen readers; CMDB codes first, then the directory's. */
export function CmdbStatusLine({ status, id }: { status: SaveStatus; id?: string }) {
  const t = useTranslations("cmdb.status");
  const tDirectory = useTranslations("directory");
  const tCommon = useTranslations("common");
  let text = "";
  if (status.kind === "saving") text = t("saving");
  else if (status.kind === "saved") text = t("saved");
  else if (status.kind === "failed") {
    const code = status.code;
    text =
      code === "rate-limited"
        ? tCommon("rateLimited")
        : code && t.has(`errors.${code}`)
          ? t(`errors.${code}`)
          : code && tDirectory.has(`errors.${code}`)
            ? tDirectory(`errors.${code}`)
            : t("failed");
  }
  return (
    <p id={id} role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ok"}>
      {text}
    </p>
  );
}

/**
 * The shape each family is drawn with on the map and in its legend, so families are told apart
 * without colour (WCAG 1.4.1). Shapes are Cytoscape's names.
 */
export const familyShapes: Record<CiFamily | "People", string> = {
  BusinessService: "round-rectangle",
  Application: "hexagon",
  Software: "diamond",
  Hardware: "rectangle",
  Network: "triangle",
  CloudResource: "barrel",
  People: "ellipse",
};

/** A small symbol per family for lists and the legend (the same idea as the map's shapes). */
export const familySymbols: Record<CiFamily | "People", string> = {
  BusinessService: "▢",
  Application: "⬡",
  Software: "◇",
  Hardware: "■",
  Network: "△",
  CloudResource: "⛁",
  People: "●",
};

/** A family with its symbol, the symbol hidden from screen readers (the name says it). */
export function FamilyLabel({ family }: { family: CiFamily | "People" }) {
  const t = useTranslations("cmdb.families");
  return (
    <span className="inline-flex items-center gap-1">
      <span aria-hidden="true">{familySymbols[family]}</span>
      {t(family)}
    </span>
  );
}

const statusClasses: Record<CiStatus, string> = {
  Planned: "border-line text-ink",
  Active: "border-ok text-ok",
  Maintenance: "border-warn text-warn",
  Retired: "border-line text-ink-muted",
};

/** The item's status as a badge with its name (never colour alone). */
export function CiStatusBadge({ status }: { status: CiStatus }) {
  const t = useTranslations("cmdb.statuses");
  return <span className={`inline-block rounded-full border px-2 text-xs font-semibold ${statusClasses[status]}`}>{t(status)}</span>;
}
