"use client";

import { useId } from "react";
import { useTranslations } from "next-intl";
import { BilingualInputs } from "@/components/directory/common";
import {
  columnTypes,
  fieldFormats,
  isPersonalData,
  profileSources,
  type FieldOption,
  type FieldType,
  type FormField,
  type JsonValue,
  type ProfileSource,
  type ResolvedField,
  type Text,
} from "@/lib/forms/types";
import { ConfigurationItemClasses } from "@/components/cmdb/ConfigurationItemClasses";
import { DataListSource } from "@/components/apps/DataListSource";
import { RulesEditor } from "./RulesEditor";
import { fieldButton, fieldInput } from "./ui";

/** Profile values that fit each type of field (as the server checks). */
export function profileSourcesFor(type: FieldType): ProfileSource[] {
  if (type === "User") return ["Manager"];
  if (type === "Location") return ["Location"];
  if (type === "Text") return profileSources.filter((s) => s !== "Manager" && s !== "Location");
  return [];
}

const textOrNull = (text: Text): Text | null => (text.en?.trim() || text.ar?.trim() ? { en: text.en?.trim() || null, ar: text.ar?.trim() || null } : null);
const numberOrNull = (value: string): number | null => (value.trim() === "" || Number.isNaN(Number(value)) ? null : Number(value));

/** Edits one field: key, labels, help, options, checks, defaults, table columns and rules. */
export function FieldProperties({
  field,
  onChange,
  allFields,
  column = false,
  attribute = false,
}: {
  field: FormField;
  onChange: (field: FormField) => void;
  /** Every field of the form (field sets expanded), for rules. */
  allFields: ResolvedField[];
  /** A table column: no rules, defaults or nested tables. */
  column?: boolean;
  /** An attribute of a CMDB class (task G1): no rules, profile defaults, read-only setting or personal data. */
  attribute?: boolean;
}) {
  const t = useTranslations("forms.builder");
  const tTypes = useTranslations("forms.types");
  const id = useId();
  const v = field.validation ?? {};
  const setValidation = (change: Partial<NonNullable<FormField["validation"]>>) => onChange({ ...field, validation: { ...v, ...change } });
  const forcedPersonal = isPersonalData({ ...field, personalData: false });
  const sources = attribute ? [] : profileSourcesFor(field.type);
  // Configuration items hold no personal data, so attributes have no Emirates ID or IBAN format.
  const formats = attribute ? fieldFormats.filter((f) => f === "Phone" || f === "Email") : fieldFormats;
  const textual = field.type === "Text" || field.type === "Number" || field.type === "Date" || field.type === "RichText";

  return (
    <div className="space-y-4">
      <p className="text-sm text-ink-muted">{t("typeIs", { type: tTypes(field.type) })}</p>
      <div className="space-y-1">
        <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
        <input id={`${id}-key`} dir="ltr" value={field.key} maxLength={60} aria-describedby={`${id}-key-hint`}
          onChange={(e) => onChange({ ...field, key: e.target.value.replace(/[^A-Za-z0-9]/g, "") })} className={fieldInput} />
        <p id={`${id}-key-hint`} className="text-sm text-ink-muted">{t("keyHint")}</p>
      </div>
      <BilingualInputs id={`${id}-label`} label={t("label")} maxLength={500} en={field.label.en ?? ""} ar={field.label.ar ?? ""}
        onChange={(value) => onChange({ ...field, label: value })} />
      {!column && (
        <BilingualInputs id={`${id}-help`} label={t("help")} maxLength={500} en={field.help?.en ?? ""} ar={field.help?.ar ?? ""}
          onChange={(value) => onChange({ ...field, help: textOrNull(value) })} />
      )}
      {!column && textual && field.type !== "RichText" && (
        <BilingualInputs id={`${id}-placeholder`} label={t("placeholder")} maxLength={500} en={field.placeholder?.en ?? ""} ar={field.placeholder?.ar ?? ""}
          onChange={(value) => onChange({ ...field, placeholder: textOrNull(value) })} />
      )}

      <fieldset className="space-y-1">
        <legend className="font-semibold">{t("behaviour")}</legend>
        <Toggle label={t("required")} checked={!!field.required} onChange={(required) => onChange({ ...field, required })} />
        {!column && !attribute && <Toggle label={t("readOnly")} hint={t("readOnlyHint")} checked={!!field.readOnly} onChange={(readOnly) => onChange({ ...field, readOnly })} />}
        {!column && !attribute && (
          <Toggle label={t("personalData")} hint={forcedPersonal ? t("personalDataForced") : t("personalDataHint")} checked={forcedPersonal || !!field.personalData}
            disabled={forcedPersonal} onChange={(personalData) => onChange({ ...field, personalData })} />
        )}
        {field.type === "Text" && <Toggle label={t("multiline")} checked={!!field.multiline} onChange={(multiline) => onChange({ ...field, multiline })} />}
        {(field.type === "Dropdown" || field.type === "ConfigurationItem") && !column && (
          <Toggle label={t("multiple")} checked={!!field.multiple} onChange={(multiple) => onChange({ ...field, multiple })} />
        )}
      </fieldset>

      {field.type === "ConfigurationItem" && (
        <ConfigurationItemClasses chosen={field.configurationItemClasses ?? []}
          onChange={(classes) => onChange({ ...field, configurationItemClasses: classes.length > 0 ? classes : null })} />
      )}

      {(field.type === "Dropdown" || field.type === "Radio") && !attribute && (
        <DataListSource value={field.dataList ?? null}
          onChange={(dataList) => onChange({ ...field, dataList, options: dataList ? null : field.options ?? [] })} />
      )}
      {(field.type === "Dropdown" || field.type === "Radio") && !field.dataList && (
        <OptionsEditor options={field.options ?? []} onChange={(options) => onChange({ ...field, options })} />
      )}

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("checks")}</legend>
        {field.type === "Text" && (
          <>
            <div className="space-y-1">
              <label htmlFor={`${id}-format`} className="block">{t("format")}</label>
              <select id={`${id}-format`} value={v.format ?? ""} onChange={(e) => setValidation({ format: (e.target.value || null) as never })} className={fieldInput}>
                <option value="">{t("formatNone")}</option>
                {formats.map((f) => <option key={f} value={f}>{t(`formats.${f}`)}</option>)}
              </select>
            </div>
            <NumberInput id={`${id}-minLength`} label={t("minLength")} value={v.minLength} onChange={(minLength) => setValidation({ minLength })} />
            <NumberInput id={`${id}-maxLength`} label={t("maxLength")} value={v.maxLength} onChange={(maxLength) => setValidation({ maxLength })} />
            <div className="space-y-1">
              <label htmlFor={`${id}-pattern`} className="block">{t("pattern")}</label>
              <input id={`${id}-pattern`} dir="ltr" value={v.pattern ?? ""} maxLength={500} aria-describedby={`${id}-pattern-hint`}
                onChange={(e) => setValidation({ pattern: e.target.value || null })} className={fieldInput} />
              <p id={`${id}-pattern-hint`} className="text-sm text-ink-muted">{t("patternHint")}</p>
            </div>
          </>
        )}
        {field.type === "Number" && (
          <>
            <NumberInput id={`${id}-min`} label={t("min")} value={v.min} onChange={(min) => setValidation({ min })} />
            <NumberInput id={`${id}-max`} label={t("max")} value={v.max} onChange={(max) => setValidation({ max })} />
          </>
        )}
        {field.type === "Date" && (
          <>
            <DateInput id={`${id}-minDate`} label={t("minDate")} value={v.minDate} onChange={(minDate) => setValidation({ minDate })} />
            <DateInput id={`${id}-maxDate`} label={t("maxDate")} value={v.maxDate} onChange={(maxDate) => setValidation({ maxDate })} />
          </>
        )}
        {field.type === "File" && (
          <>
            <div className="space-y-1">
              <label htmlFor={`${id}-types`} className="block">{t("fileTypes")}</label>
              <input id={`${id}-types`} dir="ltr" value={(v.fileTypes ?? []).join(", ")} aria-describedby={`${id}-types-hint`}
                onChange={(e) => setValidation({ fileTypes: e.target.value.split(/[,\s]+/).map((x) => x.trim().toLowerCase().replace(/^\./, "")).filter(Boolean) })}
                className={fieldInput} />
              <p id={`${id}-types-hint`} className="text-sm text-ink-muted">{t("fileTypesHint")}</p>
            </div>
            <NumberInput id={`${id}-size`} label={t("maxFileSizeMb")} value={v.maxFileSizeMb} onChange={(maxFileSizeMb) => setValidation({ maxFileSizeMb })} />
            <NumberInput id={`${id}-files`} label={t("maxFiles")} value={v.maxFiles} onChange={(maxFiles) => setValidation({ maxFiles })} />
          </>
        )}
        {field.type === "Table" && (
          <>
            <NumberInput id={`${id}-minRows`} label={t("minRows")} value={field.minRows} onChange={(minRows) => onChange({ ...field, minRows })} />
            <NumberInput id={`${id}-maxRows`} label={t("maxRows")} value={field.maxRows} onChange={(maxRows) => onChange({ ...field, maxRows })} />
          </>
        )}
        {!["Text", "Number", "Date", "File", "Table"].includes(field.type) && <p className="text-sm text-ink-muted">{t("noChecks")}</p>}
      </fieldset>

      {!column && (sources.length > 0 || ["Text", "Number", "Date", "Checkbox", "Dropdown", "Radio", "RichText"].includes(field.type)) && (
        <fieldset className="space-y-2">
          <legend className="font-semibold">{t("default")}</legend>
          {sources.length > 0 && (
            <div className="space-y-1">
              <label htmlFor={`${id}-profile`} className="block">{t("profileDefault")}</label>
              <select id={`${id}-profile`} value={field.profileDefault ?? ""} aria-describedby={`${id}-profile-hint`}
                onChange={(e) => onChange({ ...field, profileDefault: (e.target.value || null) as ProfileSource | null })} className={fieldInput}>
                <option value="">{t("profileNone")}</option>
                {sources.map((s) => <option key={s} value={s}>{t(`profile.${s}`)}</option>)}
              </select>
              <p id={`${id}-profile-hint`} className="text-sm text-ink-muted">{t("profileHint")}</p>
            </div>
          )}
          <DefaultValueInput id={`${id}-default`} field={field} onChange={(defaultValue) => onChange({ ...field, defaultValue })} />
        </fieldset>
      )}

      {field.type === "Table" && !column && <ColumnsEditor field={field} onChange={onChange} />}

      {!column && !attribute && <RulesEditor field={field} allFields={allFields} onChange={(rules) => onChange({ ...field, rules })} />}
    </div>
  );
}

function Toggle({ label, hint, checked, disabled, onChange }: { label: string; hint?: string; checked: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  const id = useId();
  return (
    <div>
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={checked} disabled={disabled} aria-describedby={hint ? `${id}-hint` : undefined}
          onChange={(e) => onChange(e.target.checked)} className="size-6 shrink-0 accent-brand" />
        {label}
      </label>
      {hint && <p id={`${id}-hint`} className="ms-8 text-sm text-ink-muted">{hint}</p>}
    </div>
  );
}

function NumberInput({ id, label, value, onChange }: { id: string; label: string; value: number | null | undefined; onChange: (v: number | null) => void }) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block">{label}</label>
      <input id={id} type="number" step="any" dir="ltr" value={value ?? ""} onChange={(e) => onChange(numberOrNull(e.target.value))} className={fieldInput} />
    </div>
  );
}

function DateInput({ id, label, value, onChange }: { id: string; label: string; value: string | null | undefined; onChange: (v: string | null) => void }) {
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block">{label}</label>
      <input id={id} type="date" dir="ltr" value={value ?? ""} onChange={(e) => onChange(e.target.value || null)} className={fieldInput} />
    </div>
  );
}

function DefaultValueInput({ id, field, onChange }: { id: string; field: FormField; onChange: (v: JsonValue | undefined) => void }) {
  const t = useTranslations("forms.builder");
  const value = field.defaultValue;
  const label = <label htmlFor={id} className="block">{t("defaultValue")}</label>;
  switch (field.type) {
    case "Text":
    case "RichText":
      return <div className="space-y-1">{label}<input id={id} dir="auto" value={typeof value === "string" ? value : ""} onChange={(e) => onChange(e.target.value || undefined)} className={fieldInput} /></div>;
    case "Number":
      return <div className="space-y-1">{label}<input id={id} type="number" step="any" dir="ltr" value={typeof value === "number" ? value : ""} onChange={(e) => onChange(numberOrNull(e.target.value) ?? undefined)} className={fieldInput} /></div>;
    case "Date":
      return <div className="space-y-1">{label}<input id={id} type="date" dir="ltr" value={typeof value === "string" ? value : ""} onChange={(e) => onChange(e.target.value || undefined)} className={fieldInput} /></div>;
    case "Checkbox":
      return (
        <label className="flex items-center gap-2">
          <input id={id} type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked ? true : undefined)} className="size-6 shrink-0 accent-brand" />
          {t("defaultTicked")}
        </label>
      );
    case "Dropdown":
    case "Radio":
      return (
        <div className="space-y-1">
          {label}
          <select id={id} value={typeof value === "string" ? value : Array.isArray(value) ? String(value[0] ?? "") : ""}
            onChange={(e) => onChange(e.target.value ? (field.multiple ? [e.target.value] : e.target.value) : undefined)} className={fieldInput}>
            <option value="">{t("defaultNone")}</option>
            {(field.options ?? []).map((o) => <option key={o.value} value={o.value}>{o.label.en || o.label.ar || o.value}</option>)}
          </select>
        </div>
      );
    default:
      return null;
  }
}

/** Options of a dropdown or radio field: stored value and bilingual label, in order. */
export function OptionsEditor({ options, onChange }: { options: FieldOption[]; onChange: (options: FieldOption[]) => void }) {
  const t = useTranslations("forms.builder");
  const id = useId();
  const set = (i: number, option: FieldOption) => onChange(options.map((o, j) => (j === i ? option : o)));
  const move = (i: number, by: number) => {
    const next = [...options];
    const [item] = next.splice(i, 1);
    if (item) next.splice(i + by, 0, item);
    onChange(next);
  };
  return (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{t("options")}</legend>
      {options.map((option, i) => (
        <div key={i} className="space-y-1 rounded-md border border-line p-2">
          <div className="grid gap-2 sm:grid-cols-3">
            <div>
              <label htmlFor={`${id}-${i}-value`} className="block text-sm">{t("optionValue", { number: i + 1 })}</label>
              <input id={`${id}-${i}-value`} dir="ltr" value={option.value} maxLength={100} onChange={(e) => set(i, { ...option, value: e.target.value })} className={fieldInput} />
            </div>
            <div>
              <label htmlFor={`${id}-${i}-en`} className="block text-sm">{t("optionEnglish", { number: i + 1 })}</label>
              <input id={`${id}-${i}-en`} lang="en" dir="ltr" value={option.label.en ?? ""} maxLength={500} onChange={(e) => set(i, { ...option, label: { ...option.label, en: e.target.value } })} className={fieldInput} />
            </div>
            <div>
              <label htmlFor={`${id}-${i}-ar`} className="block text-sm">{t("optionArabic", { number: i + 1 })}</label>
              <input id={`${id}-${i}-ar`} lang="ar" dir="rtl" value={option.label.ar ?? ""} maxLength={500} onChange={(e) => set(i, { ...option, label: { ...option.label, ar: e.target.value } })} className={fieldInput} />
            </div>
          </div>
          <div className="flex flex-wrap gap-1">
            <button type="button" className={fieldButton} disabled={i === 0} onClick={() => move(i, -1)}>{t("optionUp", { number: i + 1 })}</button>
            <button type="button" className={fieldButton} disabled={i === options.length - 1} onClick={() => move(i, 1)}>{t("optionDown", { number: i + 1 })}</button>
            <button type="button" className={fieldButton} onClick={() => onChange(options.filter((_, j) => j !== i))}>{t("optionRemove", { number: i + 1 })}</button>
          </div>
        </div>
      ))}
      <button type="button" className={fieldButton}
        onClick={() => onChange([...options, { value: `option${options.length + 1}`, label: { en: "", ar: "" } }])}>
        {t("optionAdd")}
      </button>
    </fieldset>
  );
}

/** Columns of a table field: simple types, each with its own properties. */
function ColumnsEditor({ field, onChange }: { field: FormField; onChange: (field: FormField) => void }) {
  const t = useTranslations("forms.builder");
  const tTypes = useTranslations("forms.types");
  const id = useId();
  const columns = field.columns ?? [];
  const setColumns = (next: FormField[]) => onChange({ ...field, columns: next });
  return (
    <fieldset className="space-y-2">
      <legend className="font-semibold">{t("columns")}</legend>
      {columns.map((c, i) => (
        <details key={i} className="rounded-md border border-line p-2">
          <summary className="cursor-pointer font-semibold">{t("columnSummary", { number: i + 1, name: c.label.en || c.label.ar || c.key })}</summary>
          <div className="mt-2 space-y-2">
            <div className="space-y-1">
              <label htmlFor={`${id}-${i}-type`} className="block">{t("columnType")}</label>
              <select id={`${id}-${i}-type`} value={c.type} onChange={(e) => setColumns(columns.map((x, j) => (j === i ? { ...x, type: e.target.value as FieldType } : x)))} className={fieldInput}>
                {columnTypes.map((type) => <option key={type} value={type}>{tTypes(type)}</option>)}
              </select>
            </div>
            <FieldProperties field={c} column allFields={[]} onChange={(changed) => setColumns(columns.map((x, j) => (j === i ? changed : x)))} />
            <button type="button" className={fieldButton} onClick={() => setColumns(columns.filter((_, j) => j !== i))}>{t("columnRemove", { number: i + 1 })}</button>
          </div>
        </details>
      ))}
      <button type="button" className={fieldButton}
        onClick={() => setColumns([...columns, { key: `column${columns.length + 1}`, type: "Text", label: { en: "", ar: "" } }])}>
        {t("columnAdd")}
      </button>
    </fieldset>
  );
}
