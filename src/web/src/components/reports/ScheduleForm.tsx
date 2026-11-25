"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { useRouter } from "@/i18n/navigation";
import { getJson, postJson, putJson } from "@/lib/client-api";
import {
  nameIn, recordTypes, reportFrequencies, type RecordType, type ReportFrequency, type ReportKind, type ReportName, type ReportPerson,
  type ReportSchedule, type ReportScheduleInput,
} from "@/lib/reports";
import type { ScheduleFormInitial } from "./schedule-defaults";

const kinds: ReportKind[] = ["Overview", "Volumes", "Backlog", "Sla", "Agents", "Happiness"];
const days = [1, 2, 3, 4, 5, 6, 7];

/**
 * A scheduled report (task F2): which dashboard, how often and when, its filter, and who receives
 * it. Everyone who sees reports schedules them for themselves; people with `reports.manage` choose
 * other recipients, who must be able to see reports. Each recipient gets the report built with
 * their own access, in their language.
 */
export function ScheduleForm({ initial, types, groups, canChooseRecipients, csrfToken }: {
  initial: ScheduleFormInitial;
  types: RecordType[];
  groups: { id: string; name: ReportName }[];
  canChooseRecipients: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("reports.schedules");
  const tReports = useTranslations("reports");
  const locale = useLocale();
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [value, setValue] = useState(initial);
  const [problem, setProblem] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [found, setFound] = useState<ReportPerson[] | null>(null);
  const set = <K extends keyof ScheduleFormInitial>(key: K, v: ScheduleFormInitial[K]) => setValue((current) => ({ ...current, [key]: v }));
  const toggle = <T,>(list: T[], item: T) => (list.includes(item) ? list.filter((x) => x !== item) : [...list, item]);
  const shownTypes = recordTypes.filter((r) => types.includes(r));

  async function findPeople() {
    try {
      setFound(await getJson<ReportPerson[]>(`/v1/reports/schedules/recipients?${new URLSearchParams({ search })}`));
    } catch {
      setFound([]);
    }
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!value.name.trim()) {
      setProblem(t("nameRequired"));
      return;
    }
    if (value.recipients.length === 0) {
      setProblem(t("recipientsRequired"));
      return;
    }
    setProblem(null);
    const body: ReportScheduleInput = {
      name: value.name.trim(), report: value.report, frequency: value.frequency,
      dayOfWeek: value.frequency === "Weekly" ? value.dayOfWeek : null,
      dayOfMonth: value.frequency === "Monthly" ? value.dayOfMonth : null,
      time: value.time, types: value.types, groups: value.groups, active: value.active,
      ...(canChooseRecipients ? { recipientIds: value.recipients.map((r) => r.id) } : {}),
    };
    const saved = await run(() => (value.id ? putJson<ReportSchedule>(`/v1/reports/schedules/${value.id}`, body, csrfToken) : postJson<ReportSchedule>("/v1/reports/schedules", body, csrfToken)));
    if (saved) {
      router.push("/reports/schedules");
      router.refresh();
    }
  }

  return (
    <form onSubmit={(e) => void save(e)} className="max-w-3xl space-y-5" data-testid="schedule-form" noValidate>
      <div className="space-y-1">
        <label htmlFor={`${id}-name`} className="block font-semibold">{t("name")}</label>
        <input id={`${id}-name`} value={value.name} maxLength={150} required aria-invalid={problem === t("nameRequired")} onChange={(e) => set("name", e.target.value)} className={fieldInput} />
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-report`} className="block font-semibold">{t("report")}</label>
          <select id={`${id}-report`} value={value.report} onChange={(e) => set("report", e.target.value as ReportKind)} className={fieldInput}>
            {kinds.map((k) => <option key={k} value={k}>{tReports(`dashboards.${k}.title`)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-frequency`} className="block font-semibold">{t("frequency")}</label>
          <select id={`${id}-frequency`} value={value.frequency} onChange={(e) => set("frequency", e.target.value as ReportFrequency)} className={fieldInput} aria-describedby={`${id}-period`}>
            {reportFrequencies.map((f) => <option key={f} value={f}>{t(`frequencies.${f}`)}</option>)}
          </select>
          <p id={`${id}-period`} className="text-sm text-ink-muted">{t(`periods.${value.frequency}`)}</p>
        </div>
        {value.frequency === "Weekly" && (
          <div className="space-y-1">
            <label htmlFor={`${id}-dow`} className="block font-semibold">{t("dayOfWeek")}</label>
            <select id={`${id}-dow`} value={value.dayOfWeek} onChange={(e) => set("dayOfWeek", Number(e.target.value))} className={fieldInput}>
              {days.map((d) => <option key={d} value={d}>{t(`days.${d}`)}</option>)}
            </select>
          </div>
        )}
        {value.frequency === "Monthly" && (
          <div className="space-y-1">
            <label htmlFor={`${id}-dom`} className="block font-semibold">{t("dayOfMonth")}</label>
            <input id={`${id}-dom`} type="number" min={1} max={28} value={value.dayOfMonth} onChange={(e) => set("dayOfMonth", Math.min(28, Math.max(1, Number(e.target.value) || 1)))} className={fieldInput} aria-describedby={`${id}-dom-hint`} />
            <p id={`${id}-dom-hint`} className="text-sm text-ink-muted">{t("dayOfMonthHint")}</p>
          </div>
        )}
        <div className="space-y-1">
          <label htmlFor={`${id}-time`} className="block font-semibold">{t("time")}</label>
          <input id={`${id}-time`} type="time" value={value.time} onChange={(e) => set("time", e.target.value)} className={fieldInput} aria-describedby={`${id}-time-hint`} />
          <p id={`${id}-time-hint`} className="text-sm text-ink-muted">{t("timeHint")}</p>
        </div>
      </div>
      {shownTypes.length > 1 && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{tReports("filters.types")}</legend>
          <div className="flex flex-wrap gap-3">
            {shownTypes.map((type) => (
              <label key={type} className="inline-flex items-center gap-2 text-sm">
                <input type="checkbox" checked={value.types.includes(type)} onChange={() => set("types", toggle(value.types, type))} />
                {tReports(`types.${type}`)}
              </label>
            ))}
          </div>
          <p className="text-sm text-ink-muted">{tReports("filters.hint")}</p>
        </fieldset>
      )}
      {groups.length > 1 && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{tReports("filters.groups")}</legend>
          <div className="flex flex-wrap gap-3">
            {groups.map((group) => (
              <label key={group.id} className="inline-flex items-center gap-2 text-sm">
                <input type="checkbox" checked={value.groups.includes(group.id)} onChange={() => set("groups", toggle(value.groups, group.id))} />
                <bdi>{nameIn(group.name, locale)}</bdi>
              </label>
            ))}
          </div>
        </fieldset>
      )}
      <fieldset className="space-y-2" data-testid="schedule-recipients">
        <legend className="font-semibold">{t("recipients")}</legend>
        <p className="text-sm text-ink-muted">{t(canChooseRecipients ? "recipientsHint" : "recipientsSelf")}</p>
        <ul className="flex flex-wrap gap-2">
          {value.recipients.map((r) => (
            <li key={r.id} className="inline-flex items-center gap-2 rounded-full border border-line px-3 py-1 text-sm">
              <bdi>{r.name}</bdi>
              {canChooseRecipients && (
                <button type="button" className="font-semibold text-bad" aria-label={t("removeRecipient", { name: r.name })}
                  onClick={() => set("recipients", value.recipients.filter((x) => x.id !== r.id))}>×</button>
              )}
            </li>
          ))}
        </ul>
        {canChooseRecipients && (
          <div className="space-y-2">
            <div className="flex flex-wrap items-end gap-2">
              <div className="space-y-1">
                <label htmlFor={`${id}-search`} className="block text-sm">{t("findPeople")}</label>
                <input id={`${id}-search`} type="search" value={search} onChange={(e) => setSearch(e.target.value)}
                  onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void findPeople(); } }} className={fieldInput} />
              </div>
              <button type="button" className={fieldButton} onClick={() => void findPeople()}>{t("search")}</button>
            </div>
            {found && (
              <ul aria-live="polite" className="space-y-1">
                {found.length === 0 && <li className="text-sm text-ink-muted">{t("nobodyFound")}</li>}
                {found.filter((p) => !value.recipients.some((r) => r.id === p.id)).map((p) => (
                  <li key={p.id}>
                    <button type="button" className={fieldButton} onClick={() => set("recipients", [...value.recipients, p])}>{t("addRecipient", { name: p.name })}</button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </fieldset>
      <label className="inline-flex items-center gap-2">
        <input type="checkbox" checked={value.active} onChange={(e) => set("active", e.target.checked)} />
        {t("active")}
      </label>
      {problem && <p role="alert" className="text-bad">{problem}</p>}
      <div className="flex flex-wrap items-center gap-3">
        <button type="submit" disabled={busy} className={primaryAction}>{t(value.id ? "save" : "create")}</button>
      </div>
      <SaveStatusLine status={status} />
    </form>
  );
}
