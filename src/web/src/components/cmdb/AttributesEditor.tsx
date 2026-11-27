"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { FieldProperties } from "@/components/forms/FieldProperties";
import { fieldButton, fieldInput } from "@/components/forms/ui";
import { attributeTypes, textIn as bilingualText, type InheritedAttribute } from "@/lib/cmdb";
import { textIn, type FieldType, type FormField, type FormProblem } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";

type AttributeType = (typeof attributeTypes)[number];

/** A key not yet used: the type's name in small letters and a number. */
function newKey(type: FieldType, taken: Set<string>): string {
  const base = type.charAt(0).toLowerCase() + type.slice(1);
  for (let i = 1; ; i++) if (!taken.has(`${base}${i}`)) return `${base}${i}`;
}

function newAttribute(type: AttributeType, taken: Set<string>): FormField {
  const field: FormField = { key: newKey(type, taken), type, label: { en: "", ar: "" } };
  if (type === "Dropdown" || type === "Radio") field.options = [{ value: "option1", label: { en: "", ar: "" } }];
  return field;
}

/** A problem's text: the CMDB's own codes first, then the form builder's. */
export function useProblemText() {
  const tCmdb = useTranslations("cmdb.problems");
  const tForms = useTranslations("forms.problems");
  return (problem: FormProblem) =>
    tCmdb.has(problem.code) ? tCmdb(problem.code) : tForms.has(problem.code) ? tForms(problem.code) : problem.message;
}

/**
 * A class's own attributes (task G1): C1 fields limited to text, number, date, dropdown, checkbox,
 * radio, user and location, edited with the form builder's properties (no rules, profile
 * defaults, read-only setting or personal data), with the attributes inherited from the classes
 * above shown read-only.
 */
export function AttributesEditor({
  attributes,
  inherited,
  problems,
  onChange,
}: {
  attributes: FormField[];
  inherited: InheritedAttribute[];
  problems: FormProblem[];
  onChange: (attributes: FormField[]) => void;
}) {
  const t = useTranslations("cmdb.attributes");
  const tTypes = useTranslations("forms.types");
  const locale = useLocale() as Locale;
  const id = useId();
  const problemText = useProblemText();
  const [type, setType] = useState<AttributeType>("Text");
  const [selected, setSelected] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const taken = new Set([...attributes.map((a) => a.key), ...inherited.map((a) => a.field.key)]);
  const nameOf = (field: FormField) => textIn(field.label, locale) || field.key;

  const move = (i: number, by: number) => {
    const next = [...attributes];
    const [item] = next.splice(i, 1);
    if (!item) return;
    next.splice(i + by, 0, item);
    onChange(next);
    setSelected(selected === i ? i + by : selected);
    setAnnouncement(t("moved", { name: nameOf(item), position: i + by + 1, count: next.length }));
  };

  return (
    <div className="space-y-3">
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("inherited")}</legend>
        {inherited.length === 0 ? (
          <p className="text-sm text-ink-muted">{t("noInherited")}</p>
        ) : (
          <ul className="space-y-1 text-sm" data-testid="inherited-attributes">
            {inherited.map((a) => (
              <li key={`${a.classId}-${a.field.key}`}>
                {t("inheritedLine", { name: textIn(a.field.label, locale) || a.field.key, type: tTypes(a.field.type), from: bilingualText(a.className, locale) })}
              </li>
            ))}
          </ul>
        )}
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("own")}</legend>
        {attributes.length === 0 && <p className="text-sm text-ink-muted">{t("none")}</p>}
        <ol className="space-y-2" data-testid="own-attributes">
          {attributes.map((field, i) => {
            const own = problems.filter((p) => p.field === field.key);
            return (
              <li key={i} className="space-y-2 rounded-md border border-line p-2">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold">{nameOf(field)}</span>
                  <span className="text-sm text-ink-muted">{tTypes(field.type)}</span>
                  {field.required && <span className="rounded-full border border-line px-2 text-xs">{t("required")}</span>}
                  <span className="ms-auto flex flex-wrap gap-1">
                    <button type="button" className={fieldButton} aria-expanded={selected === i} onClick={() => setSelected(selected === i ? null : i)}>
                      {selected === i ? t("close", { name: nameOf(field) }) : t("edit", { name: nameOf(field) })}
                    </button>
                    <button type="button" className={fieldButton} disabled={i === 0} onClick={() => move(i, -1)}>{t("moveUp", { name: nameOf(field) })}</button>
                    <button type="button" className={fieldButton} disabled={i === attributes.length - 1} onClick={() => move(i, 1)}>{t("moveDown", { name: nameOf(field) })}</button>
                    <button type="button" className={fieldButton}
                      onClick={() => {
                        onChange(attributes.filter((_, j) => j !== i));
                        setSelected(null);
                        setAnnouncement(t("removed", { name: nameOf(field) }));
                      }}>
                      {t("remove", { name: nameOf(field) })}
                    </button>
                  </span>
                </div>
                {own.length > 0 && (
                  <ul className="text-sm font-semibold text-bad">
                    {own.map((p, k) => <li key={k}>{problemText(p)}</li>)}
                  </ul>
                )}
                {selected === i && (
                  <div className="rounded-md bg-surface-muted p-3">
                    <FieldProperties field={field} attribute allFields={[]} onChange={(changed) => onChange(attributes.map((a, j) => (j === i ? changed : a)))} />
                  </div>
                )}
              </li>
            );
          })}
        </ol>
        <div className="flex flex-wrap items-end gap-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-type`} className="block text-sm">{t("newType")}</label>
            <select id={`${id}-type`} value={type} onChange={(e) => setType(e.target.value as AttributeType)} className={fieldInput}>
              {attributeTypes.map((x) => <option key={x} value={x}>{tTypes(x)}</option>)}
            </select>
          </div>
          <button type="button" className={fieldButton} disabled={attributes.length >= 50}
            onClick={() => {
              const field = newAttribute(type, taken);
              onChange([...attributes, field]);
              setSelected(attributes.length);
              setAnnouncement(t("added", { type: tTypes(type) }));
            }}>
            {t("add")}
          </button>
        </div>
        <p role="status" aria-live="polite" className="sr-only">{announcement}</p>
      </fieldset>
    </div>
  );
}
