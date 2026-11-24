"use client";

import { useId } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, inputClass, smallButton } from "@/components/directory/common";
import { PeopleList } from "@/components/catalog/PeopleList";
import { textOf, type Department, type GroupSummary, type Role, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import {
  answerOperators,
  approverKinds,
  channelValues,
  conditionFields,
  emptyCondition,
  emptyLevel,
  fieldKind,
  idListFields,
  listOperators,
  nameListFields,
  numberFields,
  numberOperators,
  stateValues,
  type ApprovalLevelDefinition,
  type ApproverKind,
  type ApproverRef,
  type ConditionField,
  type WorkflowCondition,
} from "@/lib/workflows";

/** What conditions and approvers can be chosen from. */
export interface WorkflowChoices {
  services: { id: string; code: string; name: { en: string | null; ar: string | null } }[];
  categories: TreeNode[];
  locations: TreeNode[];
  groups: GroupSummary[];
  departments: Department[];
  roles: Role[];
}

const languageValues = ["en", "ar"] as const;

/** The list with items a and b exchanged. */
function swap<T>(list: T[], a: number, b: number): T[] {
  const copy = [...list];
  [copy[a], copy[b]] = [copy[b]!, copy[a]!];
  return copy;
}

/** Options of a list condition's field, labelled in the page's language. */
function useOptions(field: ConditionField, choices: WorkflowChoices) {
  const locale = useLocale() as Locale;
  const t = useTranslations("workflows");
  switch (field) {
    case "service":
      return choices.services.map((s) => ({ value: s.id, label: `${s.code} · ${textOf(s.name, locale)}` }));
    case "category":
      return choices.categories.map((c) => ({ value: c.id, label: textOf(c.path, locale) || textOf(c.name, locale) }));
    case "location":
      return choices.locations.map((c) => ({ value: c.id, label: textOf(c.path, locale) || textOf(c.name, locale) }));
    case "group":
      return choices.groups.map((g) => ({ value: g.id, label: textOf(g.name, locale) }));
    case "requesterDepartment":
      return choices.departments.map((d) => ({ value: d.id, label: textOf(d.name, locale) }));
    case "channel":
      return channelValues.map((c) => ({ value: c, label: t(`channels.${c}`) }));
    case "state":
      return stateValues.map((s) => ({ value: s, label: t(`states.${s}`) }));
    case "requesterLanguage":
      return languageValues.map((l) => ({ value: l, label: t(`languages.${l}`) }));
    default:
      return [];
  }
}

/** One condition: a field, an operator and a value, each labelled. */
function ConditionRow({
  condition,
  index,
  choices,
  onChange,
  onRemove,
}: {
  condition: WorkflowCondition;
  index: number;
  choices: WorkflowChoices;
  onChange: (c: WorkflowCondition) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("workflows");
  const id = useId();
  const kind = fieldKind(condition.field);
  const options = useOptions(kind, choices);
  const operators = idListFields.includes(kind) || nameListFields.includes(kind) ? listOperators : numberFields.includes(kind) ? numberOperators : answerOperators;
  const values = Array.isArray(condition.value) ? condition.value.map(String) : [];
  return (
    <li className="grid gap-2 rounded-md border border-line p-3 sm:grid-cols-[1fr_1fr_2fr_auto]" data-testid="condition-row">
      <div className="space-y-1">
        <label htmlFor={`${id}-field`} className="block text-sm">{t("conditionField", { number: index + 1 })}</label>
        <select id={`${id}-field`} className={inputClass} value={kind}
          onChange={(e) => onChange(emptyCondition(e.target.value as ConditionField))}>
          {conditionFields.map((f) => <option key={f} value={f}>{t(`fields.${f}`)}</option>)}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-op`} className="block text-sm">{t("operator")}</label>
        <select id={`${id}-op`} className={inputClass} value={condition.op}
          onChange={(e) => onChange({ ...condition, op: e.target.value as WorkflowCondition["op"] })}>
          {operators.map((o) => <option key={o} value={o}>{t(`operators.${o}`)}</option>)}
        </select>
      </div>
      <div className="space-y-1">
        {kind === "answer" ? (
          <div className="grid gap-2 sm:grid-cols-2">
            <div className="space-y-1">
              <label htmlFor={`${id}-key`} className="block text-sm">{t("answerKey")}</label>
              <input id={`${id}-key`} dir="ltr" className={inputClass} value={condition.field.slice("answer.".length)} maxLength={60}
                onChange={(e) => onChange({ ...condition, field: `answer.${e.target.value.replace(/[^A-Za-z0-9]/g, "")}` })} />
            </div>
            {condition.op !== "empty" && condition.op !== "notEmpty" && (
              <div className="space-y-1">
                <label htmlFor={`${id}-value`} className="block text-sm">{t("value")}</label>
                <input id={`${id}-value`} dir="auto" className={inputClass} value={String(condition.value ?? "")} maxLength={500}
                  onChange={(e) => onChange({ ...condition, value: e.target.value })} />
              </div>
            )}
          </div>
        ) : numberFields.includes(kind) ? (
          <>
            <label htmlFor={`${id}-value`} className="block text-sm">{t(kind === "fee" ? "valueAed" : "value")}</label>
            <input id={`${id}-value`} type="number" dir="ltr" className={inputClass} value={Number(condition.value ?? 0)}
              onChange={(e) => onChange({ ...condition, value: Number(e.target.value) })} />
          </>
        ) : (
          <>
            <label htmlFor={`${id}-value`} className="block text-sm">{t("values")}</label>
            <select id={`${id}-value`} multiple size={Math.min(5, Math.max(2, options.length))} className={inputClass} value={values}
              onChange={(e) => onChange({ ...condition, value: Array.from(e.target.selectedOptions, (o) => o.value) })}>
              {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </>
        )}
      </div>
      <div className="flex items-end">
        <button type="button" className={smallButton} onClick={onRemove} aria-label={t("removeCondition", { number: index + 1 })}>{t("remove")}</button>
      </div>
    </li>
  );
}

/** A list of conditions with an add button. */
export function ConditionsEditor({
  legend,
  conditions,
  choices,
  onChange,
}: {
  legend: string;
  conditions: WorkflowCondition[];
  choices: WorkflowChoices;
  onChange: (c: WorkflowCondition[]) => void;
}) {
  const t = useTranslations("workflows");
  return (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{legend}</legend>
      {conditions.length === 0 && <p className="text-sm text-ink-muted">{t("noConditions")}</p>}
      <ol className="space-y-2">
        {conditions.map((c, i) => (
          <ConditionRow key={i} index={i} condition={c} choices={choices}
            onChange={(next) => onChange(conditions.map((x, j) => (j === i ? next : x)))}
            onRemove={() => onChange(conditions.filter((_, j) => j !== i))} />
        ))}
      </ol>
      <button type="button" className={smallButton} onClick={() => onChange([...conditions, emptyCondition()])}>{t("addCondition")}</button>
    </fieldset>
  );
}

/** One approver of a level: who, and the group, person, role or form field when the kind needs one. */
function ApproverRow({ approver, choices, onChange, onRemove, label }: {
  approver: ApproverRef;
  choices: WorkflowChoices;
  onChange: (a: ApproverRef) => void;
  onRemove: () => void;
  label: string;
}) {
  const t = useTranslations("workflows");
  const locale = useLocale() as Locale;
  const id = useId();
  return (
    <li className="space-y-2 rounded-md border border-line p-2" data-testid="approver-row">
      <div className="flex flex-wrap items-end gap-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-kind`} className="block text-sm">{label}</label>
          <select id={`${id}-kind`} className={inputClass} value={approver.kind}
            onChange={(e) => onChange({ kind: e.target.value as ApproverKind })}>
            {approverKinds.map((k) => <option key={k} value={k}>{t(`approverKinds.${k}`)}</option>)}
          </select>
        </div>
        {(approver.kind === "Group" || approver.kind === "GroupLeads") && (
          <div className="space-y-1">
            <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
            <select id={`${id}-group`} className={inputClass} value={approver.id ?? ""} onChange={(e) => onChange({ ...approver, id: e.target.value || null })}>
              <option value="">{t("chooseGroup")}</option>
              {choices.groups.map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
            </select>
          </div>
        )}
        {approver.kind === "Role" && (
          <div className="space-y-1">
            <label htmlFor={`${id}-role`} className="block text-sm">{t("role")}</label>
            <select id={`${id}-role`} className={inputClass} value={approver.key ?? ""} onChange={(e) => onChange({ ...approver, key: e.target.value || null })}>
              <option value="">{t("chooseRole")}</option>
              {choices.roles.map((r) => <option key={r.key} value={r.key}>{textOf(r.name, locale)}</option>)}
            </select>
          </div>
        )}
        {approver.kind === "AnswerUser" && (
          <div className="space-y-1">
            <label htmlFor={`${id}-key`} className="block text-sm">{t("answerKey")}</label>
            <input id={`${id}-key`} dir="ltr" className={inputClass} value={approver.key ?? ""} maxLength={60}
              onChange={(e) => onChange({ ...approver, key: e.target.value.replace(/[^A-Za-z0-9]/g, "") })} />
          </div>
        )}
        <button type="button" className={smallButton} onClick={onRemove}>{t("remove")}</button>
      </div>
      {approver.kind === "User" && (
        <PeopleList legend={t("person")} ids={approver.id ? [approver.id] : []} single idPrefix={`${id}-person`}
          onChange={(ids) => onChange({ ...approver, id: ids[0] ?? null })} />
      )}
    </li>
  );
}

/**
 * Approval levels (task C3): each level has a bilingual name, any or all approvers (parallel), the
 * approvers, and conditions under which it applies (approval by amount or answer).
 */
export function ApprovalLevelsEditor({
  legend,
  hint,
  levels,
  choices,
  onChange,
}: {
  legend: string;
  hint?: string;
  levels: ApprovalLevelDefinition[];
  choices: WorkflowChoices;
  onChange: (levels: ApprovalLevelDefinition[]) => void;
}) {
  const t = useTranslations("workflows");
  const id = useId();
  const set = (i: number, patch: Partial<ApprovalLevelDefinition>) => onChange(levels.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  return (
    <fieldset className="space-y-3" aria-describedby={hint ? `${id}-hint` : undefined}>
      <legend className="font-semibold">{legend}</legend>
      {hint && <p id={`${id}-hint`} className="text-sm text-ink-muted">{hint}</p>}
      <ol className="space-y-3">
        {levels.map((level, i) => (
          <li key={i} className="space-y-3 rounded-lg border border-line p-3" data-testid="approval-level">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h4 className="font-semibold">{t("levelNumber", { number: i + 1 })}</h4>
              <div className="flex gap-2">
                <button type="button" className={smallButton} disabled={i === 0}
                  onClick={() => onChange(swap(levels, i, i - 1))}>
                  {t("moveLevelUp", { number: i + 1 })}</button>
                <button type="button" className={smallButton} disabled={i === levels.length - 1}
                  onClick={() => onChange(swap(levels, i, i + 1))}>
                  {t("moveLevelDown", { number: i + 1 })}</button>
                <button type="button" className={smallButton} onClick={() => onChange(levels.filter((_, j) => j !== i))}
                  aria-label={t("removeLevel", { number: i + 1 })}>{t("remove")}</button>
              </div>
            </div>
            <BilingualInputs id={`${id}-${i}-name`} label={t("levelName")} en={level.name.en ?? ""} ar={level.name.ar ?? ""} maxLength={150}
              onChange={(v) => set(i, { name: { en: v.en || null, ar: v.ar || null } })} />
            <fieldset className="space-y-1">
              <legend className="text-sm font-semibold">{t("mode")}</legend>
              {(["Any", "All"] as const).map((mode) => (
                <label key={mode} className="flex items-center gap-2">
                  <input type="radio" className="size-5" name={`${id}-${i}-mode`} checked={level.mode === mode} onChange={() => set(i, { mode })} />
                  {t(`modes.${mode}`)}
                </label>
              ))}
            </fieldset>
            <ul className="space-y-2">
              {level.approvers.map((a, j) => (
                <ApproverRow key={j} approver={a} choices={choices} label={t("approverNumber", { number: j + 1 })}
                  onChange={(next) => set(i, { approvers: level.approvers.map((x, k) => (k === j ? next : x)) })}
                  onRemove={() => set(i, { approvers: level.approvers.filter((_, k) => k !== j) })} />
              ))}
            </ul>
            <button type="button" className={smallButton} onClick={() => set(i, { approvers: [...level.approvers, { kind: "RequesterManager" }] })}>{t("addApprover")}</button>
            <ConditionsEditor legend={t("levelConditions")} conditions={level.conditions} choices={choices} onChange={(conditions) => set(i, { conditions })} />
          </li>
        ))}
      </ol>
      <button type="button" className={smallButton} onClick={() => onChange([...levels, emptyLevel()])}>{t("addLevel")}</button>
    </fieldset>
  );
}
