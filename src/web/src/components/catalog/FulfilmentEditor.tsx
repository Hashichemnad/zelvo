"use client";

import { useId } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { ConditionsEditor, type WorkflowChoices } from "@/components/workflows/WorkflowEditors";
import { textOf } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { AssignmentMethod, FulfilmentTask, ServiceEscalation, ServiceFulfilment } from "@/lib/catalog";

/** A whole number of minutes, or none, from a number input. */
function minutesOf(value: string): number | null {
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) && n > 0 ? n : null;
}

function swap<T>(list: T[], a: number, b: number): T[] {
  const copy = [...list];
  [copy[a], copy[b]] = [copy[b]!, copy[a]!];
  return copy;
}

/** A group and how it takes requests, labelled. */
function GroupChoice({ id, label, groupId, method, choices, onChange, none }: {
  id: string;
  label: string;
  groupId: string | null;
  method: AssignmentMethod;
  choices: WorkflowChoices;
  onChange: (groupId: string | null, method: AssignmentMethod) => void;
  none: string;
}) {
  const t = useTranslations("catalog.fulfilment");
  const locale = useLocale() as Locale;
  return (
    <div className="grid gap-2 sm:grid-cols-2">
      <div className="space-y-1">
        <label htmlFor={`${id}-group`} className="block text-sm font-semibold">{label}</label>
        <select id={`${id}-group`} className={fieldInput} value={groupId ?? ""} onChange={(e) => onChange(e.target.value || null, method)}>
          <option value="">{none}</option>
          {choices.groups.filter((g) => g.active || g.id === groupId).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-method`} className="block text-sm font-semibold">{t("method")}</label>
        <select id={`${id}-method`} className={fieldInput} value={method} onChange={(e) => onChange(groupId, e.target.value as AssignmentMethod)}>
          <option value="GroupQueue">{t("methods.GroupQueue")}</option>
          <option value="RoundRobin">{t("methods.RoundRobin")}</option>
        </select>
      </div>
    </div>
  );
}

/**
 * The fulfilment of a service (task C4, module 2): the group that receives new requests and the
 * groups chosen by form answers, the service's own ownership and completion SLAs with escalations,
 * and the task template, in sequence or in parallel, each with its own target time.
 */
export function FulfilmentEditor({ value, choices, onChange }: { value: ServiceFulfilment; choices: WorkflowChoices; onChange: (f: ServiceFulfilment) => void }) {
  const t = useTranslations("catalog.fulfilment");
  const locale = useLocale() as Locale;
  const id = useId();
  const set = (patch: Partial<ServiceFulfilment>) => onChange({ ...value, ...patch });
  const setTask = (i: number, patch: Partial<FulfilmentTask>) => set({ tasks: value.tasks.map((x, j) => (j === i ? { ...x, ...patch } : x)) });
  const setEscalation = (i: number, patch: Partial<ServiceEscalation>) => set({ escalations: value.escalations.map((x, j) => (j === i ? { ...x, ...patch } : x)) });

  return (
    <div className="space-y-6" data-testid="fulfilment-editor">
      <GroupChoice id={`${id}-initial`} label={t("initialGroup")} groupId={value.groupId} method={value.method} choices={choices} none={t("byRules")}
        onChange={(groupId, method) => set({ groupId, method })} />

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("overrides")}</legend>
        <p className="text-sm text-ink-muted">{t("overridesHint")}</p>
        <ol className="space-y-3">
          {value.overrides.map((o, i) => (
            <li key={i} className="space-y-2 rounded-md border border-line p-3" data-testid="group-override">
              <GroupChoice id={`${id}-override-${i}`} label={t("overrideGroup", { number: i + 1 })} groupId={o.groupId} method={o.method} choices={choices} none={t("chooseGroup")}
                onChange={(groupId, method) => set({ overrides: value.overrides.map((x, j) => (j === i ? { ...x, groupId, method } : x)) })} />
              <ConditionsEditor legend={t("whenAnswers")} conditions={o.conditions} choices={choices}
                onChange={(conditions) => set({ overrides: value.overrides.map((x, j) => (j === i ? { ...x, conditions } : x)) })} />
              <button type="button" className={smallButton} onClick={() => set({ overrides: value.overrides.filter((_, j) => j !== i) })}
                aria-label={t("removeOverride", { number: i + 1 })}>{t("remove")}</button>
            </li>
          ))}
        </ol>
        <button type="button" className={smallButton} onClick={() => set({ overrides: [...value.overrides, { conditions: [{ field: "answer.", op: "eq", value: "" }], groupId: null, method: "GroupQueue" }] })}>
          {t("addOverride")}
        </button>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("sla")}</legend>
        <p className="text-sm text-ink-muted">{t("slaHint")}</p>
        <div className="grid gap-2 sm:grid-cols-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-ownership`} className="block text-sm font-semibold">{t("ownershipMinutes")}</label>
            <input id={`${id}-ownership`} type="number" min={1} dir="ltr" className={fieldInput} value={value.ownershipMinutes ?? ""}
              onChange={(e) => set({ ownershipMinutes: minutesOf(e.target.value) })} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-completion`} className="block text-sm font-semibold">{t("completionMinutes")}</label>
            <input id={`${id}-completion`} type="number" min={1} dir="ltr" className={fieldInput} value={value.completionMinutes ?? ""} aria-describedby={`${id}-completion-hint`}
              onChange={(e) => set({ completionMinutes: minutesOf(e.target.value) })} />
            <p id={`${id}-completion-hint`} className="text-sm text-ink-muted">{t("completionHint")}</p>
          </div>
        </div>
        <ol className="space-y-2">
          {value.escalations.map((e, i) => (
            <li key={i} className="grid gap-2 rounded-md border border-line p-3 sm:grid-cols-3" data-testid="service-escalation">
              <div className="space-y-1">
                <label htmlFor={`${id}-esc-${i}-percent`} className="block text-sm">{t("percent", { number: i + 1 })}</label>
                <input id={`${id}-esc-${i}-percent`} type="number" min={1} max={300} dir="ltr" className={fieldInput} value={e.percent}
                  onChange={(ev) => setEscalation(i, { percent: Number(ev.target.value) })} />
              </div>
              <div className="space-y-1">
                <label htmlFor={`${id}-esc-${i}-metric`} className="block text-sm">{t("metric")}</label>
                <select id={`${id}-esc-${i}-metric`} className={fieldInput} value={e.metric ?? ""} onChange={(ev) => setEscalation(i, { metric: (ev.target.value || null) as ServiceEscalation["metric"] })}>
                  <option value="">{t("metrics.both")}</option>
                  <option value="Response">{t("metrics.Response")}</option>
                  <option value="Resolution">{t("metrics.Resolution")}</option>
                </select>
              </div>
              <fieldset className="space-y-1">
                <legend className="text-sm">{t("alert")}</legend>
                <label className="flex items-center gap-2"><input type="checkbox" checked={e.notifyAssignee} onChange={(ev) => setEscalation(i, { notifyAssignee: ev.target.checked })} />{t("alertAssignee")}</label>
                <label className="flex items-center gap-2"><input type="checkbox" checked={e.notifyGroupLeads} onChange={(ev) => setEscalation(i, { notifyGroupLeads: ev.target.checked })} />{t("alertLeads")}</label>
                <label htmlFor={`${id}-esc-${i}-group`} className="block text-sm">{t("alertGroup")}</label>
                <select id={`${id}-esc-${i}-group`} className={fieldInput} value={e.notifyGroupId ?? ""} onChange={(ev) => setEscalation(i, { notifyGroupId: ev.target.value || null })}>
                  <option value="">{t("noGroup")}</option>
                  {choices.groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
                </select>
              </fieldset>
              <button type="button" className={smallButton} onClick={() => set({ escalations: value.escalations.filter((_, j) => j !== i) })}
                aria-label={t("removeEscalation", { number: i + 1 })}>{t("remove")}</button>
            </li>
          ))}
        </ol>
        <button type="button" className={smallButton}
          onClick={() => set({ escalations: [...value.escalations, { metric: null, percent: 80, notifyAssignee: true, notifyGroupLeads: false, notifyGroupId: null }] })}>
          {t("addEscalation")}
        </button>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("tasks")}</legend>
        <p className="text-sm text-ink-muted">{t("tasksHint")}</p>
        <ol className="space-y-3">
          {value.tasks.map((task, i) => (
            <li key={i} className="space-y-2 rounded-md border border-line p-3" data-testid="fulfilment-task">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-semibold">{t("taskNumber", { number: i + 1 })}</span>
                <button type="button" className={smallButton} disabled={i === 0} onClick={() => set({ tasks: swap(value.tasks, i, i - 1) })}
                  aria-label={t("moveTaskUp", { number: i + 1 })}>{t("up")}</button>
                <button type="button" className={smallButton} disabled={i === value.tasks.length - 1} onClick={() => set({ tasks: swap(value.tasks, i, i + 1) })}
                  aria-label={t("moveTaskDown", { number: i + 1 })}>{t("down")}</button>
                <button type="button" className={smallButton} onClick={() => set({ tasks: value.tasks.filter((_, j) => j !== i) })}
                  aria-label={t("removeTask", { number: i + 1 })}>{t("remove")}</button>
              </div>
              <div className="space-y-1">
                <label htmlFor={`${id}-task-${i}-key`} className="block text-sm font-semibold">{t("taskKey")}</label>
                <input id={`${id}-task-${i}-key`} dir="ltr" maxLength={40} className={fieldInput} value={task.key}
                  onChange={(e) => setTask(i, { key: e.target.value.replace(/[^A-Za-z0-9]/g, "") })} />
              </div>
              <BilingualInputs id={`${id}-task-${i}-name`} label={t("taskName")} en={task.name.en ?? ""} ar={task.name.ar ?? ""} maxLength={150}
                onChange={(v) => setTask(i, { name: { en: v.en || null, ar: v.ar || null } })} />
              <div className="grid gap-2 sm:grid-cols-2">
                <div className="space-y-1">
                  <label htmlFor={`${id}-task-${i}-group`} className="block text-sm font-semibold">{t("taskGroup")}</label>
                  <select id={`${id}-task-${i}-group`} className={fieldInput} value={task.groupId ?? ""} onChange={(e) => setTask(i, { groupId: e.target.value || null })}>
                    <option value="">{t("requestGroup")}</option>
                    {choices.groups.filter((g) => g.active || g.id === task.groupId).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
                  </select>
                </div>
                <div className="space-y-1">
                  <label htmlFor={`${id}-task-${i}-target`} className="block text-sm font-semibold">{t("targetMinutes")}</label>
                  <input id={`${id}-task-${i}-target`} type="number" min={1} dir="ltr" className={fieldInput} value={task.targetMinutes ?? ""}
                    onChange={(e) => setTask(i, { targetMinutes: minutesOf(e.target.value) })} />
                </div>
              </div>
              {i > 0 && (
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={task.parallelWithPrevious} onChange={(e) => setTask(i, { parallelWithPrevious: e.target.checked })} />
                  {t("parallel")}
                </label>
              )}
            </li>
          ))}
        </ol>
        <button type="button" className={smallButton}
          onClick={() => set({ tasks: [...value.tasks, { key: `task${value.tasks.length + 1}`, name: { en: null, ar: null }, description: { en: null, ar: null }, groupId: null, parallelWithPrevious: false, targetMinutes: null }] })}>
          {t("addTask")}
        </button>
      </fieldset>
    </div>
  );
}
