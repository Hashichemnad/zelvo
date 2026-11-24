"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { putJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { BusinessCalendar } from "@/lib/sla";
import { SaveStatusLine, inputClass, primaryButton, useSaver } from "@/components/directory/common";

/** A group's own business hours for SLA clocks (task B3, product owner decision 2026-11-24). */
export function GroupCalendarPicker({ groupId, initial, calendars, csrfToken, canManage }: {
  groupId: string;
  initial: string | null;
  calendars: BusinessCalendar[];
  csrfToken: string;
  canManage: boolean;
}) {
  const t = useTranslations("sla.groupCalendar");
  const locale = useLocale() as Locale;
  const id = useId();
  const [calendarId, setCalendarId] = useState(initial ?? "");
  const saver = useSaver();
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3">
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("title")}</h3>
      <p className="max-w-2xl">{t("intro")}</p>
      <form
        className="flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          void saver.run(() => putJson(`/v1/groups/${groupId}/calendar`, { calendarId: calendarId || null }, csrfToken));
        }}
      >
        <div className="space-y-1">
          <label htmlFor={`${id}-calendar`} className="block text-sm font-semibold">{t("calendar")}</label>
          <select id={`${id}-calendar`} value={calendarId} disabled={!canManage} onChange={(e) => setCalendarId(e.target.value)} className={inputClass}>
            <option value="">{t("none")}</option>
            {calendars.map((c) => (
              <option key={c.id} value={c.id}>{textOf(c.name, locale)}</option>
            ))}
          </select>
        </div>
        {canManage && <button type="submit" className={primaryButton} disabled={saver.busy}>{t("save")}</button>}
      </form>
      <SaveStatusLine status={saver.status} />
    </section>
  );
}
