"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ExportPdfButton } from "@/components/ExportPdfButton";
import { cellClass, tableClass } from "@/components/settings/integrations/ui";
import { daysUntil, formatDay, type AssetSummary, type LabelLayout } from "@/lib/assets";
import { textIn } from "@/lib/cmdb";
import type { DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { AssetStateBadge } from "./common";

/** A warranty end with "Ends soon" or "Ended" when it does. */
export function WarrantyEnd({ endsOn, warningDays, display }: { endsOn: string | null; warningDays: number; display: DateDisplayOptions }) {
  const t = useTranslations("assets.list");
  if (!endsOn) return <>—</>;
  const days = daysUntil(endsOn);
  return (
    <>
      {formatDay(endsOn, display.locale, display.calendar, display.digits)}
      {days < 0 ? <span className="block text-xs font-semibold text-bad">{t("ended")}</span> : days <= warningDays ? <span className="block text-xs font-semibold text-warn">{t("endsSoon")}</span> : null}
    </>
  );
}

/**
 * The asset list (task G3) with a checkbox per asset to print labels: a PDF sheet of each tag as
 * a QR code and Code 128 (small 3 × 8 or large 2 × 5 labels on A4), made by the worker.
 */
export function AssetTable({ items, csrfToken, display, warningDays }: { items: AssetSummary[]; csrfToken: string; display: DateDisplayOptions; warningDays: number }) {
  const t = useTranslations("assets.list");
  const locale = useLocale() as Locale;
  const [chosen, setChosen] = useState<string[]>([]);
  const [layout, setLayout] = useState<LabelLayout>("Small");
  const all = items.length > 0 && chosen.length === items.length;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3 rounded-md border border-line p-3" role="group" aria-label={t("labels")}>
        <span className="text-sm">{t("chosen", { count: chosen.length })}</span>
        <label className="flex items-center gap-2 text-sm">
          <span>{t("layout")}</span>
          <select value={layout} onChange={(e) => setLayout(e.target.value as LabelLayout)} className="rounded-md border border-line bg-surface px-2 py-1">
            <option value="Small">{t("layoutSmall")}</option>
            <option value="Large">{t("layoutLarge")}</option>
          </select>
        </label>
        {chosen.length > 0 ? (
          <ExportPdfButton path="/assets/labels" csrfToken={csrfToken} query={new URLSearchParams({ ids: chosen.join(","), layout }).toString()} />
        ) : (
          <span className="text-sm text-ink-muted">{t("chooseForLabels")}</span>
        )}
      </div>
      <div className="overflow-x-auto">
        <table className={tableClass} data-testid="asset-table">
          <caption className="sr-only">{t("title")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>
                <input type="checkbox" checked={all} aria-label={t("chooseAll")} className="size-5 accent-brand"
                  onChange={(e) => setChosen(e.target.checked ? items.map((i) => i.id) : [])} />
              </th>
              <th scope="col" className={cellClass}>{t("tag")}</th>
              <th scope="col" className={cellClass}>{t("name")}</th>
              <th scope="col" className={cellClass}>{t("state")}</th>
              <th scope="col" className={cellClass}>{t("assignedTo")}</th>
              <th scope="col" className={cellClass}>{t("warranty")}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((a) => (
              <tr key={a.id}>
                <td className={cellClass}>
                  <input type="checkbox" checked={chosen.includes(a.id)} aria-label={t("choose", { tag: a.tag })} className="size-5 accent-brand"
                    onChange={(e) => setChosen((c) => (e.target.checked ? [...c, a.id] : c.filter((x) => x !== a.id)))} />
                </td>
                <td className={cellClass}>
                  <Link href={`/assets/${a.id}`} className="font-mono font-semibold text-brand underline-offset-4 hover:underline" dir="ltr">{a.tag}</Link>
                </td>
                <td className={cellClass}>
                  <span dir="auto">{a.name}</span>
                  <span className="block text-xs text-ink-muted">{textIn(a.class?.path, locale)}</span>
                </td>
                <td className={cellClass}><AssetStateBadge state={a.state} /></td>
                <td className={cellClass} dir="auto">{a.assignedTo?.displayName ?? "—"}</td>
                <td className={cellClass}><WarrantyEnd endsOn={a.warrantyEndsOn} warningDays={warningDays} display={display} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
