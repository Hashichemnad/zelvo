"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import type { WorkflowChoices } from "@/components/workflows/WorkflowEditors";
import { getJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import { formatLongDate, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import {
  bundleStages, newTrigger, proactiveModes, scheduleUnits, triggerKinds,
  type BundleItem, type ProactiveRun, type ProactiveTrigger, type ServiceKind, type ServiceProactive,
} from "@/lib/catalog";

function swap<T>(list: T[], a: number, b: number): T[] {
  const copy = [...list];
  [copy[a], copy[b]] = [copy[b]!, copy[a]!];
  return copy;
}

function toggle(list: string[], value: string, on: boolean): string[] {
  return on ? [...new Set([...list, value])] : list.filter((v) => v !== value);
}

/**
 * Whether the service is one service or a bundle (task C5, module 2), and a bundle's services:
 * each mandatory or optional, starting after the one before or together with it. The stage each
 * starts in is shown as the API works it out.
 */
export function BundleEditor({ kind, items, choices, selfId, onChange }: {
  kind: ServiceKind;
  items: BundleItem[];
  choices: WorkflowChoices;
  selfId: string;
  onChange: (kind: ServiceKind, items: BundleItem[]) => void;
}) {
  const t = useTranslations("catalog.bundle");
  const locale = useLocale() as Locale;
  const id = useId();
  const stages = bundleStages(items);
  const set = (i: number, patch: Partial<BundleItem>) => onChange(kind, items.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  return (
    <div className="space-y-4" data-testid="bundle-editor">
      <fieldset className="space-y-1">
        <legend className="font-semibold">{t("kind")}</legend>
        {(["Service", "Bundle"] as const).map((k) => (
          <label key={k} className="flex items-center gap-2">
            <input type="radio" className="size-6" name={`${id}-kind`} checked={kind === k} onChange={() => onChange(k, items)} />
            {t(`kinds.${k}`)}
          </label>
        ))}
        <p className="text-sm text-ink-muted">{t("kindHint")}</p>
      </fieldset>
      {kind === "Bundle" && (
        <fieldset className="space-y-2">
          <legend className="font-semibold">{t("items")}</legend>
          <p className="text-sm text-ink-muted">{t("itemsHint")}</p>
          <ol className="space-y-2">
            {items.map((item, i) => (
              <li key={i} className="space-y-2 rounded-md border border-line p-3" data-testid="bundle-item">
                <p className="text-sm font-semibold">{t("stage", { number: (stages[i] ?? 0) + 1 })}</p>
                <div className="space-y-1">
                  <label htmlFor={`${id}-item-${i}`} className="block text-sm font-semibold">{t("service", { number: i + 1 })}</label>
                  <select id={`${id}-item-${i}`} className={fieldInput} value={item.serviceId ?? ""} onChange={(e) => set(i, { serviceId: e.target.value || null })}>
                    <option value="">{t("chooseService")}</option>
                    {choices.services.filter((s) => s.id !== selfId).map((s) => <option key={s.id} value={s.id}>{`${s.code} · ${textOf(s.name, locale)}`}</option>)}
                  </select>
                </div>
                <label className="flex items-center gap-2">
                  <input type="checkbox" className="size-6" checked={item.required} onChange={(e) => set(i, { required: e.target.checked })} />
                  {t("required")}
                </label>
                {i > 0 && (
                  <label className="flex items-center gap-2">
                    <input type="checkbox" className="size-6" checked={item.parallelWithPrevious} onChange={(e) => set(i, { parallelWithPrevious: e.target.checked })} />
                    {t("parallel")}
                  </label>
                )}
                <div className="flex flex-wrap gap-2">
                  <button type="button" className={smallButton} disabled={i === 0} onClick={() => onChange(kind, swap(items, i, i - 1))} aria-label={t("moveUp", { number: i + 1 })}>{t("up")}</button>
                  <button type="button" className={smallButton} disabled={i === items.length - 1} onClick={() => onChange(kind, swap(items, i, i + 1))} aria-label={t("moveDown", { number: i + 1 })}>{t("down")}</button>
                  <button type="button" className={smallButton} onClick={() => onChange(kind, items.filter((_, j) => j !== i).map((x, j) => (j === 0 ? { ...x, parallelWithPrevious: false } : x)))}
                    aria-label={t("removeItem", { number: i + 1 })}>{t("remove")}</button>
                </div>
              </li>
            ))}
          </ol>
          <button type="button" className={smallButton} onClick={() => onChange(kind, [...items, { serviceId: null, required: true, parallelWithPrevious: false }])}>{t("add")}</button>
        </fieldset>
      )}
    </div>
  );
}

/** One trigger: what starts the service, with the settings its kind needs. */
function TriggerEditor({ trigger, index, choices, selfId, onChange, onRemove }: {
  trigger: ProactiveTrigger;
  index: number;
  choices: WorkflowChoices;
  selfId: string;
  onChange: (t: ProactiveTrigger) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("catalog.proactive");
  const locale = useLocale() as Locale;
  const id = useId();
  const set = (patch: Partial<ProactiveTrigger>) => onChange({ ...trigger, ...patch });
  const people = trigger.people;
  return (
    <li className="space-y-2 rounded-md border border-line p-3" data-testid="proactive-trigger">
      <div className="grid gap-2 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-kind`} className="block text-sm font-semibold">{t("triggerKind", { number: index + 1 })}</label>
          <select id={`${id}-kind`} className={fieldInput} value={trigger.kind} onChange={(e) => set({ kind: e.target.value as ProactiveTrigger["kind"] })}>
            {triggerKinds.map((k) => <option key={k} value={k}>{t(`kinds.${k}`)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-key`} className="block text-sm font-semibold">{t("key")}</label>
          <input id={`${id}-key`} dir="ltr" className={fieldInput} value={trigger.key} aria-describedby={`${id}-key-hint`} onChange={(e) => set({ key: e.target.value })} />
          <p id={`${id}-key-hint`} className="text-sm text-ink-muted">{t("keyHint")}</p>
        </div>
      </div>
      <p className="text-sm text-ink-muted">{t(`kindHints.${trigger.kind}`)}</p>
      {trigger.kind === "Date" && (
        <div className="grid gap-2 sm:grid-cols-3">
          <div className="space-y-1">
            <label htmlFor={`${id}-source`} className="block text-sm font-semibold">{t("sourceService")}</label>
            <select id={`${id}-source`} className={fieldInput} value={trigger.sourceServiceId ?? ""} onChange={(e) => set({ sourceServiceId: e.target.value || null })}>
              <option value="">{t("apiOnly")}</option>
              {choices.services.filter((s) => s.id !== selfId).map((s) => <option key={s.id} value={s.id}>{`${s.code} · ${textOf(s.name, locale)}`}</option>)}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-field`} className="block text-sm font-semibold">{t("sourceField")}</label>
            <input id={`${id}-field`} dir="ltr" className={fieldInput} value={trigger.sourceFieldKey ?? ""} disabled={!trigger.sourceServiceId}
              onChange={(e) => set({ sourceFieldKey: e.target.value.trim() || null })} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-offset`} className="block text-sm font-semibold">{t("offsetDays")}</label>
            <input id={`${id}-offset`} type="number" dir="ltr" className={fieldInput} value={trigger.offsetDays} aria-describedby={`${id}-offset-hint`}
              onChange={(e) => set({ offsetDays: Number.parseInt(e.target.value, 10) || 0 })} />
            <p id={`${id}-offset-hint`} className="text-sm text-ink-muted">{t("offsetHint")}</p>
          </div>
        </div>
      )}
      {trigger.kind === "Schedule" && (
        <div className="space-y-2">
          <div className="grid gap-2 sm:grid-cols-3">
            <div className="space-y-1">
              <label htmlFor={`${id}-start`} className="block text-sm font-semibold">{t("startOn")}</label>
              <input id={`${id}-start`} type="date" dir="ltr" className={fieldInput} value={trigger.startOn ?? ""} onChange={(e) => set({ startOn: e.target.value || null })} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-every`} className="block text-sm font-semibold">{t("every")}</label>
              <input id={`${id}-every`} type="number" min={1} max={365} dir="ltr" className={fieldInput} value={trigger.every} onChange={(e) => set({ every: Number.parseInt(e.target.value, 10) || 1 })} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-unit`} className="block text-sm font-semibold">{t("unit")}</label>
              <select id={`${id}-unit`} className={fieldInput} value={trigger.unit} onChange={(e) => set({ unit: e.target.value as ProactiveTrigger["unit"] })}>
                {scheduleUnits.map((u) => <option key={u} value={u}>{t(`units.${u}`)}</option>)}
              </select>
            </div>
          </div>
          <fieldset className="space-y-1">
            <legend className="text-sm font-semibold">{t("peopleGroups")}</legend>
            {choices.groups.filter((g) => g.active || people.groupIds.includes(g.id)).map((g) => (
              <label key={g.id} className="flex items-center gap-2">
                <input type="checkbox" className="size-6" checked={people.groupIds.includes(g.id)}
                  onChange={(e) => set({ people: { ...people, groupIds: toggle(people.groupIds, g.id, e.target.checked) } })} />
                {textOf(g.name, locale)}
              </label>
            ))}
          </fieldset>
          <fieldset className="space-y-1">
            <legend className="text-sm font-semibold">{t("peopleRoles")}</legend>
            {choices.roles.map((r) => (
              <label key={r.key} className="flex items-center gap-2">
                <input type="checkbox" className="size-6" checked={people.roleKeys.includes(r.key)}
                  onChange={(e) => set({ people: { ...people, roleKeys: toggle(people.roleKeys, r.key, e.target.checked) } })} />
                {textOf(r.name, locale)}
              </label>
            ))}
          </fieldset>
          <fieldset className="space-y-1">
            <legend className="text-sm font-semibold">{t("peopleDepartments")}</legend>
            {choices.departments.map((d) => (
              <label key={d.id} className="flex items-center gap-2">
                <input type="checkbox" className="size-6" checked={people.departmentIds.includes(d.id)}
                  onChange={(e) => set({ people: { ...people, departmentIds: toggle(people.departmentIds, d.id, e.target.checked) } })} />
                {textOf(d.name, locale)}
              </label>
            ))}
          </fieldset>
        </div>
      )}
      <button type="button" className={smallButton} onClick={onRemove} aria-label={t("removeTrigger", { number: index + 1 })}>{t("remove")}</button>
    </li>
  );
}

/**
 * A proactive service (task C5, module 2): on or off, offer (the person confirms the form filled
 * in) or start automatically, how long offers stay open, the triggers, and the latest runs.
 */
export function ProactiveEditor({ value, choices, serviceId, display, onChange }: {
  value: ServiceProactive;
  choices: WorkflowChoices;
  serviceId: string;
  display: DateDisplayOptions;
  onChange: (p: ServiceProactive) => void;
}) {
  const t = useTranslations("catalog.proactive");
  const id = useId();
  const set = (patch: Partial<ServiceProactive>) => onChange({ ...value, ...patch });
  const [runs, setRuns] = useState<ProactiveRun[] | null>(null);
  // The latest runs, once the service is proactive.
  useEffect(() => {
    if (!value.enabled) return;
    let cancelled = false;
    getJson<{ items: ProactiveRun[] }>(`/v1/proactive/runs?serviceId=${serviceId}&pageSize=20`)
      .then((page) => { if (!cancelled) setRuns(Array.isArray(page?.items) ? page.items : []); })
      .catch(() => { if (!cancelled) setRuns([]); });
    return () => {
      cancelled = true;
    };
  }, [serviceId, value.enabled]);

  return (
    <div className="space-y-4" data-testid="proactive-editor">
      <label className="flex items-center gap-2 font-semibold">
        <input type="checkbox" className="size-6" checked={value.enabled} onChange={(e) => set({ enabled: e.target.checked })} />
        {t("enabled")}
      </label>
      <p className="text-sm text-ink-muted">{t("hint")}</p>
      {value.enabled && (
        <>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("mode")}</legend>
            {proactiveModes.map((m) => (
              <label key={m} className="flex items-center gap-2">
                <input type="radio" className="size-6" name={`${id}-mode`} checked={value.mode === m} onChange={() => set({ mode: m })} />
                {t(`modes.${m}`)}
              </label>
            ))}
          </fieldset>
          <div className="max-w-xs space-y-1">
            <label htmlFor={`${id}-days`} className="block text-sm font-semibold">{t("offerDays")}</label>
            <input id={`${id}-days`} type="number" min={1} max={90} dir="ltr" className={fieldInput} value={value.offerDays}
              onChange={(e) => set({ offerDays: Number.parseInt(e.target.value, 10) || 1 })} />
          </div>
        </>
      )}
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("triggers")}</legend>
        <ol className="space-y-2">
          {value.triggers.map((trigger, i) => (
            <TriggerEditor key={i} trigger={trigger} index={i} choices={choices} selfId={serviceId}
              onChange={(next) => set({ triggers: value.triggers.map((x, j) => (j === i ? next : x)) })}
              onRemove={() => set({ triggers: value.triggers.filter((_, j) => j !== i) })} />
          ))}
        </ol>
        <button type="button" className={smallButton} onClick={() => set({ triggers: [...value.triggers, newTrigger("Event", value.triggers.length)] })}>{t("addTrigger")}</button>
      </fieldset>
      {value.enabled && <section aria-labelledby={`${id}-runs`} className="space-y-2">
        <h3 id={`${id}-runs`} className="font-semibold">{t("runs")}</h3>
        {runs === null ? <p>{t("loading")}</p> : runs.length === 0 ? <p className="text-ink-muted">{t("noRuns")}</p> : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-start">
                  <th scope="col" className="p-1 text-start">{t("person")}</th>
                  <th scope="col" className="p-1 text-start">{t("trigger")}</th>
                  <th scope="col" className="p-1 text-start">{t("dueOn")}</th>
                  <th scope="col" className="p-1 text-start">{t("state")}</th>
                  <th scope="col" className="p-1 text-start">{t("request")}</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.id} className="border-t border-line" data-testid="proactive-run">
                    <td className="p-1" dir="auto">{r.person.displayName ?? "—"}</td>
                    <td className="p-1" dir="ltr">{r.trigger}</td>
                    <td className="p-1">{formatLongDate(new Date(`${r.dueOn}T12:00:00Z`), display)}</td>
                    <td className="p-1">{t(`states.${r.state}`)}{r.reason ? <span className="block text-ink-muted" dir="ltr">{r.reason}</span> : null}</td>
                    <td className="p-1" dir="ltr">{r.request?.number ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>}
    </div>
  );
}
