"use client";

import { useId } from "react";
import { useTranslations } from "next-intl";
import {
  conditionOperators,
  operatorsWithValue,
  ruleActions,
  type ConditionOperator,
  type FieldRule,
  type FormField,
  type ResolvedField,
  type RuleCondition,
} from "@/lib/forms/types";
import { fieldButton, fieldInput } from "./ui";

/** The first comparison that fits a field. */
const firstOperator = (field: FormField | undefined): ConditionOperator => operatorsFor(field)[0] ?? "NotEmpty";

/** Operators that make sense for a field type. */
export function operatorsFor(field: FormField | undefined): ConditionOperator[] {
  if (!field) return ["Empty", "NotEmpty"];
  switch (field.type) {
    case "Checkbox":
      return ["Checked", "NotChecked"];
    case "Number":
    case "Date":
      return ["Equals", "NotEquals", "GreaterThan", "LessThan", "Empty", "NotEmpty"];
    case "Dropdown":
    case "Radio":
      return ["Equals", "NotEquals", "Empty", "NotEmpty"];
    case "Text":
    case "RichText":
      return ["Equals", "NotEquals", "Contains", "Empty", "NotEmpty"];
    default:
      return ["Empty", "NotEmpty"];
  }
}

/**
 * Rules on a field (module 2): show, hide, require or make read-only when all or any of the
 * conditions on other answers hold, for example show "Monitor size" only when "Add a monitor" is ticked.
 */
export function RulesEditor({ field, allFields, onChange }: { field: FormField; allFields: ResolvedField[]; onChange: (rules: FieldRule[]) => void }) {
  const t = useTranslations("forms.builder");
  const id = useId();
  const rules = field.rules ?? [];
  const others = allFields.map((f) => f.field).filter((f) => f.key !== field.key);
  const setRule = (i: number, rule: FieldRule) => onChange(rules.map((r, j) => (j === i ? rule : r)));
  const setCondition = (i: number, k: number, condition: RuleCondition) => {
    const rule = rules[i];
    if (rule) setRule(i, { ...rule, conditions: rule.conditions.map((c, j) => (j === k ? condition : c)) });
  };
  const name = (f: FormField) => f.label.en || f.label.ar || f.key;

  return (
    <fieldset className="space-y-3">
      <legend className="font-semibold">{t("rules")}</legend>
      <p className="text-sm text-ink-muted">{t("rulesHint")}</p>
      {rules.map((rule, i) => (
        <div key={i} className="space-y-2 rounded-md border border-line p-2" role="group" aria-label={t("ruleNumber", { number: i + 1 })}>
          <div className="grid gap-2 sm:grid-cols-2">
            <div>
              <label htmlFor={`${id}-${i}-action`} className="block text-sm">{t("ruleAction")}</label>
              <select id={`${id}-${i}-action`} value={rule.action} onChange={(e) => setRule(i, { ...rule, action: e.target.value as FieldRule["action"] })} className={fieldInput}>
                {ruleActions.map((a) => <option key={a} value={a}>{t(`actions.${a}`)}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor={`${id}-${i}-match`} className="block text-sm">{t("ruleMatch")}</label>
              <select id={`${id}-${i}-match`} value={rule.match} onChange={(e) => setRule(i, { ...rule, match: e.target.value as FieldRule["match"] })} className={fieldInput}>
                <option value="All">{t("matchAll")}</option>
                <option value="Any">{t("matchAny")}</option>
              </select>
            </div>
          </div>
          {rule.conditions.map((condition, k) => {
            const other = others.find((f) => f.key === condition.field);
            const operators = operatorsFor(other);
            const withValue = operatorsWithValue.has(condition.operator);
            return (
              <div key={k} className="grid gap-2 rounded border border-line p-2 sm:grid-cols-3" role="group" aria-label={t("conditionNumber", { number: k + 1 })}>
                <div>
                  <label htmlFor={`${id}-${i}-${k}-field`} className="block text-sm">{t("conditionField")}</label>
                  <select id={`${id}-${i}-${k}-field`} value={condition.field}
                    onChange={(e) => {
                      const next = others.find((f) => f.key === e.target.value);
                      setCondition(i, k, { field: e.target.value, operator: firstOperator(next), value: null });
                    }} className={fieldInput}>
                    <option value="">{t("conditionChoose")}</option>
                    {others.map((f) => <option key={f.key} value={f.key}>{name(f)}</option>)}
                  </select>
                </div>
                <div>
                  <label htmlFor={`${id}-${i}-${k}-op`} className="block text-sm">{t("conditionOperator")}</label>
                  <select id={`${id}-${i}-${k}-op`} value={condition.operator}
                    onChange={(e) => setCondition(i, k, { ...condition, operator: e.target.value as ConditionOperator })} className={fieldInput}>
                    {conditionOperators.filter((o) => operators.includes(o)).map((o) => <option key={o} value={o}>{t(`operators.${o}`)}</option>)}
                  </select>
                </div>
                {withValue && (
                  <div>
                    <label htmlFor={`${id}-${i}-${k}-value`} className="block text-sm">{t("conditionValue")}</label>
                    {other && (other.type === "Dropdown" || other.type === "Radio") ? (
                      <select id={`${id}-${i}-${k}-value`} value={condition.value ?? ""} onChange={(e) => setCondition(i, k, { ...condition, value: e.target.value || null })} className={fieldInput}>
                        <option value="">{t("conditionChoose")}</option>
                        {(other.options ?? []).map((o) => <option key={o.value} value={o.value}>{o.label.en || o.label.ar || o.value}</option>)}
                      </select>
                    ) : (
                      <input id={`${id}-${i}-${k}-value`} dir="auto" type={other?.type === "Date" ? "date" : other?.type === "Number" ? "number" : "text"}
                        value={condition.value ?? ""} onChange={(e) => setCondition(i, k, { ...condition, value: e.target.value || null })} className={fieldInput} />
                    )}
                  </div>
                )}
                <div className="sm:col-span-3">
                  <button type="button" className={fieldButton} onClick={() => setRule(i, { ...rule, conditions: rule.conditions.filter((_, j) => j !== k) })}>
                    {t("conditionRemove", { number: k + 1 })}
                  </button>
                </div>
              </div>
            );
          })}
          <div className="flex flex-wrap gap-2">
            <button type="button" className={fieldButton} disabled={others.length === 0}
              onClick={() => setRule(i, { ...rule, conditions: [...rule.conditions, { field: others[0]?.key ?? "", operator: firstOperator(others[0]), value: null }] })}>
              {t("conditionAdd")}
            </button>
            <button type="button" className={fieldButton} onClick={() => onChange(rules.filter((_, j) => j !== i))}>
              {t("ruleRemove", { number: i + 1 })}
            </button>
          </div>
        </div>
      ))}
      <button type="button" className={fieldButton} disabled={others.length === 0}
        onClick={() => onChange([...rules, { action: "Show", match: "All", conditions: [{ field: others[0]?.key ?? "", operator: firstOperator(others[0]), value: null }] }])}>
        {t("ruleAdd")}
      </button>
      {others.length === 0 && <p className="text-sm text-ink-muted">{t("rulesNeedFields")}</p>}
    </fieldset>
  );
}
