"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import { textOf, toInput } from "@/lib/directory";
import { formatLongDate, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { isTime, sortPeriods, weekdays, type BusinessCalendar, type CalendarTemplate, type Period, type Weekday } from "@/lib/sla";
import { BilingualInputs, Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "@/components/directory/common";

/** Dates of a holiday in the user's calendar and digits, read as a local date (no time zone shift). */
function holidayDate(value: string, display: DateDisplayOptions): string {
  return formatLongDate(new Date(`${value}T12:00:00Z`), { ...display, timeZone: "UTC" });
}

/** Weekly working periods: per day, any number of periods (a Friday half-day is just a shorter one). */
function WeekEditor({ periods, onChange, idPrefix }: { periods: Period[]; onChange: (periods: Period[]) => void; idPrefix: string }) {
  const t = useTranslations("sla.calendars");
  const tDays = useTranslations("sla.days");
  return (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{t("week")}</legend>
      <p className="text-sm text-ink-muted">{t("weekHint")}</p>
      <div className="overflow-x-auto">
        <table className="w-full border-collapse text-start text-sm">
          <thead>
            <tr>
              <th scope="col" className="py-1 text-start">{t("day")}</th>
              <th scope="col" className="py-1 text-start">{t("periods")}</th>
            </tr>
          </thead>
          <tbody>
            {weekdays.map((day: Weekday) => {
              const own = periods.map((p, index) => ({ p, index })).filter(({ p }) => p.day === day);
              return (
                <tr key={day} className="border-t border-line align-top">
                  <th scope="row" className="py-2 pe-3 text-start font-semibold">{tDays(day)}</th>
                  <td className="space-y-2 py-2">
                    {own.length === 0 && <p className="text-ink-muted">{t("dayOff")}</p>}
                    {own.map(({ p, index }) => (
                      <div key={index} className="flex flex-wrap items-end gap-2">
                        <div>
                          <label htmlFor={`${idPrefix}-${index}-start`} className="block text-xs">{t("start")}</label>
                          <input id={`${idPrefix}-${index}-start`} dir="ltr" inputMode="numeric" pattern="\d{2}:\d{2}" placeholder="07:30" value={p.start} maxLength={5}
                            aria-invalid={!isTime(p.start)} className={`${inputClass} w-24`}
                            onChange={(e) => onChange(periods.map((x, i) => (i === index ? { ...x, start: e.target.value } : x)))} />
                        </div>
                        <div>
                          <label htmlFor={`${idPrefix}-${index}-end`} className="block text-xs">{t("end")}</label>
                          <input id={`${idPrefix}-${index}-end`} dir="ltr" inputMode="numeric" pattern="\d{2}:\d{2}" placeholder="15:30" value={p.end} maxLength={5}
                            aria-invalid={!isTime(p.end)} className={`${inputClass} w-24`}
                            onChange={(e) => onChange(periods.map((x, i) => (i === index ? { ...x, end: e.target.value } : x)))} />
                        </div>
                        <button type="button" className={smallButton} onClick={() => onChange(periods.filter((_, i) => i !== index))}>
                          {t("removePeriod", { day: tDays(day) })}
                        </button>
                      </div>
                    ))}
                    <button type="button" className={smallButton} onClick={() => onChange([...periods, { day, start: "08:00", end: "16:00" }])}>
                      {t("addPeriod", { day: tDays(day) })}
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </fieldset>
  );
}

/** Add or change a calendar: name, time zone, default flag and the week. */
function CalendarForm({ calendar, templates, onSubmit, onCancel, busy }: {
  calendar: BusinessCalendar | null;
  templates: CalendarTemplate[];
  onSubmit: (input: { name: { en: string | null; ar: string | null }; timeZone: string; isDefault: boolean; periods: Period[] }) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const t = useTranslations("sla.calendars");
  const id = useId();
  const [name, setName] = useState({ en: calendar?.name.en ?? "", ar: calendar?.name.ar ?? "" });
  const [timeZone, setTimeZone] = useState(calendar?.timeZone ?? "Asia/Dubai");
  const [isDefault, setIsDefault] = useState(calendar?.isDefault ?? false);
  const [periods, setPeriods] = useState<Period[]>(calendar?.periods ?? templates.find((x) => x.key === "uae-government")?.periods ?? []);
  const valid = periods.length > 0 && periods.every((p) => isTime(p.start) && isTime(p.end) && p.start < p.end) && (name.en.trim() || name.ar.trim());
  return (
    <form
      className="space-y-4 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      data-testid="calendar-form"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({ name: toInput(name.en, name.ar), timeZone: timeZone.trim(), isDefault, periods: sortPeriods(periods) });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{calendar ? t("editCalendar") : t("addCalendar")}</h3>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} maxLength={150} />
      <div className="flex flex-wrap items-end gap-4">
        <div className="space-y-1">
          <label htmlFor={`${id}-tz`} className="block font-semibold">{t("timeZone")}</label>
          <input id={`${id}-tz`} dir="ltr" value={timeZone} maxLength={64} onChange={(e) => setTimeZone(e.target.value)} aria-describedby={`${id}-tz-hint`} className={inputClass} />
          <p id={`${id}-tz-hint`} className="text-sm text-ink-muted">{t("timeZoneHint")}</p>
        </div>
        <label className="flex items-center gap-2 pb-6">
          <input type="checkbox" checked={isDefault} disabled={calendar?.isDefault} onChange={(e) => setIsDefault(e.target.checked)} />
          {t("isDefault")}
        </label>
      </div>
      <div className="space-y-1">
        <p className="font-semibold" id={`${id}-templates`}>{t("templates")}</p>
        <div className="flex flex-wrap gap-2" role="group" aria-labelledby={`${id}-templates`}>
          {templates.map((template) => (
            <button key={template.key} type="button" className={smallButton} onClick={() => setPeriods(template.periods)}>
              {t(`template.${template.key}`)}
            </button>
          ))}
        </div>
      </div>
      <WeekEditor periods={periods} onChange={setPeriods} idPrefix={`${id}-period`} />
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || !valid}>{t("saveCalendar")}</button>
        <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

/** Public holidays of one calendar: add (one day or a range) and remove. */
function Holidays({ calendar, onChange, csrfToken, canManage, display }: {
  calendar: BusinessCalendar;
  onChange: (calendar: BusinessCalendar) => void;
  csrfToken: string;
  canManage: boolean;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("sla.calendars");
  const locale = useLocale() as Locale;
  const id = useId();
  const [name, setName] = useState({ en: "", ar: "" });
  const [startDate, setStartDate] = useState("");
  const [endDate, setEndDate] = useState("");
  const saver = useSaver();
  return (
    <section aria-labelledby={`${id}-holidays`} className="space-y-2">
      <h4 id={`${id}-holidays`} className="font-semibold">{t("holidays")}</h4>
      {calendar.holidays.length === 0 ? (
        <p className="text-sm">{t("noHolidays")}</p>
      ) : (
        <ul className="space-y-1 text-sm">
          {calendar.holidays.map((h) => (
            <li key={h.id} className="flex flex-wrap items-center gap-2">
              <span className="font-semibold"><Localized text={h.name} locale={locale} /></span>
              <span>
                {h.startDate === h.endDate
                  ? holidayDate(h.startDate, display)
                  : t("range", { from: holidayDate(h.startDate, display), to: holidayDate(h.endDate, display) })}
              </span>
              {canManage && (
                <button type="button" className={smallButton} disabled={saver.busy}
                  onClick={() => void saver.run(async () => {
                    await deleteRequest(`/v1/sla/calendars/${calendar.id}/holidays/${h.id}`, csrfToken);
                    onChange(await getJson<BusinessCalendar>(`/v1/sla/calendars/${calendar.id}`));
                  })}>
                  {t("removeHoliday", { name: textOf(h.name, locale) })}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canManage && (
        <form
          className="space-y-2 rounded-md border border-line p-3"
          aria-label={t("addHoliday")}
          onSubmit={(e) => {
            e.preventDefault();
            void saver.run(async () => {
              onChange(await postJson<BusinessCalendar>(`/v1/sla/calendars/${calendar.id}/holidays`,
                { startDate, endDate: endDate || null, name: toInput(name.en, name.ar) }, csrfToken));
              setName({ en: "", ar: "" });
              setStartDate("");
              setEndDate("");
            });
          }}
        >
          <BilingualInputs id={`${id}-name`} label={t("holidayName")} en={name.en} ar={name.ar} onChange={setName} maxLength={150} />
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1">
              <label htmlFor={`${id}-start`} className="block text-sm">{t("firstDay")}</label>
              <input id={`${id}-start`} type="date" dir="ltr" required value={startDate} onChange={(e) => setStartDate(e.target.value)} className={inputClass} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-end`} className="block text-sm">{t("lastDay")}</label>
              <input id={`${id}-end`} type="date" dir="ltr" value={endDate} min={startDate} onChange={(e) => setEndDate(e.target.value)} className={inputClass} />
            </div>
            <button type="submit" className={secondaryButton} disabled={saver.busy || !startDate || (!name.en.trim() && !name.ar.trim())}>{t("addHoliday")}</button>
          </div>
          <p className="text-sm text-ink-muted">{t("holidayHint")}</p>
        </form>
      )}
      <SaveStatusLine status={saver.status} />
    </section>
  );
}

/**
 * Settings → Business calendars (module 8, GOV-7): working hours per day with UAE templates,
 * the time zone, the tenant default, and public holidays.
 */
export function CalendarsPanel({ initial, templates, csrfToken, canManage, display }: {
  initial: BusinessCalendar[];
  templates: CalendarTemplate[];
  csrfToken: string;
  canManage: boolean;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("sla.calendars");
  const tDays = useTranslations("sla.days");
  const locale = useLocale() as Locale;
  const [calendars, setCalendars] = useState(initial);
  const [editing, setEditing] = useState<BusinessCalendar | "new" | null>(null);
  const saver = useSaver();
  const reload = async () => setCalendars(await getJson<BusinessCalendar[]>("/v1/sla/calendars"));
  const replace = (calendar: BusinessCalendar) => setCalendars(calendars.map((c) => (c.id === calendar.id ? calendar : c)));

  return (
    <div className="space-y-6">
      {calendars.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ul className="space-y-4" data-testid="calendars">
          {calendars.map((calendar) => {
            const name = textOf(calendar.name, locale);
            return (
              <li key={calendar.id} className="space-y-3 rounded-lg border border-line p-4">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="text-lg font-bold"><Localized text={calendar.name} locale={locale} /></h3>
                  {calendar.isDefault && <span className="rounded-full border border-brand px-2 text-xs font-semibold text-brand">{t("default")}</span>}
                  <span className="text-sm text-ink-muted" dir="ltr">{calendar.timeZone}</span>
                  {canManage && (
                    <span className="ms-auto flex flex-wrap gap-1">
                      <button type="button" className={smallButton} onClick={() => setEditing(calendar)}>{t("edit", { name })}</button>
                      {!calendar.isDefault && (
                        <button type="button" className={smallButton} disabled={saver.busy}
                          onClick={() => void saver.run(async () => {
                            await deleteRequest(`/v1/sla/calendars/${calendar.id}`, csrfToken);
                            await reload();
                          })}>
                          {t("delete", { name })}
                        </button>
                      )}
                    </span>
                  )}
                </div>
                <p className="text-sm">
                  {weekdays.map((day) => {
                    const own = calendar.periods.filter((p) => p.day === day);
                    return own.length === 0 ? null : (
                      <span key={day} className="me-3 inline-block">
                        <span className="font-semibold">{tDays(day)}</span> <span dir="ltr">{own.map((p) => `${p.start}–${p.end}`).join(", ")}</span>
                      </span>
                    );
                  })}
                </p>
                <p className="text-sm text-ink-muted">{t("usedBy", { groups: calendar.groupCount, policies: calendar.policyCount })}</p>
                <Holidays calendar={calendar} onChange={replace} csrfToken={csrfToken} canManage={canManage} display={display} />
              </li>
            );
          })}
        </ul>
      )}
      {canManage &&
        (editing ? (
          <CalendarForm
            key={editing === "new" ? "new" : editing.id}
            calendar={editing === "new" ? null : editing}
            templates={templates}
            busy={saver.busy}
            onCancel={() => setEditing(null)}
            onSubmit={(input) =>
              void saver.run(async () => {
                if (editing === "new") await postJson("/v1/sla/calendars", input, csrfToken);
                else await putJson(`/v1/sla/calendars/${editing.id}`, input, csrfToken);
                await reload();
                setEditing(null);
              })
            }
          />
        ) : (
          <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("addCalendar")}</button>
        ))}
      <SaveStatusLine status={saver.status} />
    </div>
  );
}
