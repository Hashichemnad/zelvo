"use client";

import { useId } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { nameIn, reportIntervals, type RecordType, type ReportFilterValues, type ReportName } from "@/lib/reports";

/**
 * The filter of a dashboard (task F2): period, record types, groups and interval. A plain GET
 * form, so the filter stays in the address and the page is built on the server.
 */
export function ReportFilters({ values, types, groups, showInterval = true }: {
  values: ReportFilterValues;
  /** Record types the person may see. */
  types: RecordType[];
  /** Groups to choose from (all, or the ones the person leads). */
  groups: { id: string; name: ReportName }[];
  showInterval?: boolean;
}) {
  const t = useTranslations("reports.filters");
  const tTypes = useTranslations("reports.types");
  const tIntervals = useTranslations("reports.intervals");
  const locale = useLocale();
  const id = useId();
  return (
    <form method="get" aria-label={t("label")} className="space-y-3 rounded-lg border border-line bg-surface p-4" data-testid="report-filters">
      <div className="flex flex-wrap items-end gap-3">
        <div className="space-y-1">
          <label htmlFor={`${id}-from`} className="block text-sm font-semibold">{t("from")}</label>
          <input id={`${id}-from`} type="date" name="from" defaultValue={values.from} className={fieldInput} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-to`} className="block text-sm font-semibold">{t("to")}</label>
          <input id={`${id}-to`} type="date" name="to" defaultValue={values.to} className={fieldInput} />
        </div>
        {showInterval && (
          <div className="space-y-1">
            <label htmlFor={`${id}-interval`} className="block text-sm font-semibold">{t("interval")}</label>
            <select id={`${id}-interval`} name="interval" defaultValue={values.interval} className={fieldInput}>
              <option value="">{t("intervalAuto")}</option>
              {reportIntervals.map((i) => <option key={i} value={i}>{tIntervals(i)}</option>)}
            </select>
          </div>
        )}
      </div>
      <p className="text-sm text-ink-muted" id={`${id}-hint`}>{t("hint")}</p>
      <div className="grid gap-3 md:grid-cols-2">
        {types.length > 1 && (
          <fieldset className="space-y-1" aria-describedby={`${id}-hint`}>
            <legend className="text-sm font-semibold">{t("types")}</legend>
            <div className="flex flex-wrap gap-3">
              {types.map((type) => (
                <label key={type} className="inline-flex items-center gap-2 text-sm">
                  <input type="checkbox" name="types" value={type} defaultChecked={values.types.includes(type)} />
                  {tTypes(type)}
                </label>
              ))}
            </div>
          </fieldset>
        )}
        {groups.length > 1 && (
          <fieldset className="space-y-1" aria-describedby={`${id}-hint`}>
            <legend className="text-sm font-semibold">{t("groups")}</legend>
            <div className="flex max-h-32 flex-wrap gap-3 overflow-y-auto">
              {groups.map((group) => (
                <label key={group.id} className="inline-flex items-center gap-2 text-sm">
                  <input type="checkbox" name="groups" value={group.id} defaultChecked={values.groups.includes(group.id)} />
                  <bdi>{nameIn(group.name, locale)}</bdi>
                </label>
              ))}
            </div>
          </fieldset>
        )}
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <button type="submit" className={primaryAction}>{t("show")}</button>
        <a href="?" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("reset")}</a>
      </div>
    </form>
  );
}
