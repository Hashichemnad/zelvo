"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import { ApiError, deleteRequest, getJson, postForm } from "@/lib/client-api";
import { selectableNodes, textOf, type TreeNode } from "@/lib/directory";
import { CiLookup, lookupPath } from "@/components/cmdb/CiLookup";
import type { CiPick } from "@/lib/cmdb";
import { effectiveAnswers, errorsFor, maxConfigurationItems } from "@/lib/forms/check";
import { evaluate, type FieldState } from "@/lib/forms/rules";
import { isFallback, textIn, type AnswerError, type Answers, type FormField, type FormReferences, type FormUpload, type JsonValue, type ResolvedField, type Text } from "@/lib/forms/types";
import { SignaturePad } from "./SignaturePad";
import { fieldButton, fieldInput } from "./ui";

type Locale = "en" | "ar";

export interface FormRendererProps {
  /** The form's fields in order (field sets expanded). */
  fields: ResolvedField[];
  /** What people typed or chose, by field key. */
  answers: Answers;
  onChange: (answers: Answers) => void;
  /** Defaults (profile values first), by field key. */
  defaults: Answers;
  /** Names of users, paths of locations and files that defaults refer to. */
  references?: FormReferences;
  /** Problems to show under the fields (from the browser's checks or the server). */
  errors: AnswerError[];
  /** Language of the form's own texts (labels, options, help). */
  contentLocale: Locale;
  /** Locations to choose from (location fields). */
  locations: TreeNode[];
  /** For uploads (files, drawn signatures); absent in the builder's preview. */
  csrfToken?: string;
  /** The builder's preview: nothing is uploaded or looked up. */
  preview?: boolean;
  /** Prefix for element ids, unique on the page. */
  idPrefix: string;
}

/**
 * A form as people fill it (module 2): every field type, with rules applied as people type (show,
 * hide, require, read-only), defaults from the profile, and problems shown under each field. Used
 * by "Report an incident", "Log an incident" and the builder's desktop and mobile preview.
 */
export function FormRenderer(props: FormRendererProps) {
  const { fields, answers, defaults } = props;
  const plain = fields.map((f) => f.field);
  const working = effectiveAnswers(plain, answers, defaults);
  const states = evaluate(plain, working);
  return (
    <div className="space-y-5" lang={props.contentLocale} dir={props.contentLocale === "ar" ? "rtl" : "ltr"}>
      {fields.map(({ field }) =>
        states[field.key]?.visible ? (
          <FieldView key={field.key} field={field} state={states[field.key]!} value={working[field.key] ?? null} {...props}
            set={(value) => props.onChange({ ...answers, [field.key]: value })} />
        ) : null,
      )}
    </div>
  );
}

interface FieldViewProps extends FormRendererProps {
  field: FormField;
  state: FieldState;
  value: JsonValue;
  set: (value: JsonValue) => void;
}

/** A text of the form in the content language, marked when it falls back to the other language. */
function FormText({ text, locale }: { text: Text | null | undefined; locale: Locale }) {
  const value = textIn(text, locale);
  if (!value) return null;
  return isFallback(text, locale) ? <span lang={locale === "ar" ? "en" : "ar"} dir="auto">{value}</span> : <>{value}</>;
}

function errorText(t: ReturnType<typeof useTranslations>, error: AnswerError, field: FormField): string {
  const v = field.validation ?? {};
  const params = {
    min: String(v.min ?? ""), max: String(v.max ?? ""), minLength: String(v.minLength ?? ""), maxLength: String(v.maxLength ?? ""),
    minDate: v.minDate ?? "", maxDate: v.maxDate ?? "", maxFiles: String(v.maxFiles ?? 5),
    minRows: String(field.minRows ?? ""), maxRows: String(field.maxRows ?? 100),
  };
  return t.has(`errors.${error.code}`) ? t(`errors.${error.code}`, params) : (error.message ?? t("errors.invalid"));
}

function FieldView(props: FieldViewProps) {
  const t = useTranslations("forms.renderer");
  const { field, state, idPrefix, contentLocale } = props;
  const id = `${idPrefix}-${field.key}`;
  const own = errorsFor(props.errors, field.key).filter((e) => e.field === field.key);
  const label = (
    <>
      <FormText text={field.label} locale={contentLocale} />
      {state.required && <span className="text-bad"> ({t("required")})</span>}
      {state.readOnly && <span className="text-ink-muted"> ({t("readOnly")})</span>}
    </>
  );
  const help = field.help && textIn(field.help, contentLocale) ? `${id}-help` : null;
  const errorId = own.length > 0 ? `${id}-error` : null;
  const describedBy = [help, errorId].filter(Boolean).join(" ") || undefined;
  const control = { id, describedBy, invalid: own.length > 0 };
  const grouped = field.type === "Radio" || (field.type === "Dropdown" && field.multiple) || field.type === "Signature" || field.type === "Table" || field.type === "File"
    || field.type === "ConfigurationItem";

  const body = <Control {...props} control={control} />;
  const footer = (
    <>
      {help && (
        <p id={help} className="text-sm text-ink-muted">
          <FormText text={field.help} locale={contentLocale} />
        </p>
      )}
      {own.length > 0 && (
        <p id={errorId!} className="text-sm font-semibold text-bad">
          {own.map((e) => errorText(t, e, field)).join(" ")}
        </p>
      )}
    </>
  );

  if (field.type === "Checkbox") {
    return (
      <div className="space-y-1" data-field={field.key}>
        <div className="flex items-center gap-2">
          {body}
          <label htmlFor={id} className="font-semibold">
            {label}
          </label>
        </div>
        {footer}
      </div>
    );
  }

  if (grouped) {
    return (
      <fieldset className="space-y-2" data-field={field.key} aria-describedby={describedBy}>
        <legend className="font-semibold">{label}</legend>
        {body}
        {footer}
      </fieldset>
    );
  }

  return (
    <div className="space-y-1" data-field={field.key}>
      <label htmlFor={id} className="block font-semibold">
        {label}
      </label>
      {body}
      {footer}
    </div>
  );
}

interface ControlProps extends FieldViewProps {
  control: { id: string; describedBy?: string; invalid: boolean };
}

function Control(props: ControlProps) {
  const t = useTranslations("forms.renderer");
  const { field, state, value, set, control, contentLocale } = props;
  const common = {
    id: control.id,
    "aria-describedby": control.describedBy,
    "aria-invalid": control.invalid || undefined,
    "aria-required": state.required || undefined,
  };
  const placeholder = textIn(field.placeholder, contentLocale) || undefined;
  switch (field.type) {
    case "Text":
      return field.multiline ? (
        <textarea {...common} rows={4} dir="auto" value={typeof value === "string" ? value : ""} readOnly={state.readOnly} placeholder={placeholder}
          maxLength={field.validation?.maxLength ?? 10000} onChange={(e) => set(e.target.value)} className={fieldInput} />
      ) : (
        <input {...common} dir="auto" value={typeof value === "string" ? value : ""} readOnly={state.readOnly} placeholder={placeholder}
          maxLength={field.validation?.maxLength ?? 2000} onChange={(e) => set(e.target.value)} className={fieldInput}
          inputMode={field.validation?.format === "Phone" ? "tel" : field.validation?.format === "Email" ? "email" : undefined}
          autoComplete={field.validation?.format === "Email" ? "email" : field.validation?.format === "Phone" ? "tel" : undefined} />
      );
    case "RichText":
      return <RichTextControl {...props} />;
    case "Number":
      return (
        <input {...common} type="number" step="any" inputMode="decimal" dir="ltr" value={typeof value === "number" ? value : ""} readOnly={state.readOnly}
          min={field.validation?.min ?? undefined} max={field.validation?.max ?? undefined} placeholder={placeholder}
          onChange={(e) => set(e.target.value === "" ? null : Number(e.target.value))} className={fieldInput} />
      );
    case "Date":
      return (
        <input {...common} type="date" dir="ltr" value={typeof value === "string" ? value : ""} readOnly={state.readOnly}
          min={field.validation?.minDate ?? undefined} max={field.validation?.maxDate ?? undefined}
          onChange={(e) => set(e.target.value || null)} className={fieldInput} />
      );
    case "Checkbox":
      return (
        <input {...common} type="checkbox" checked={value === true} disabled={state.readOnly}
          onChange={(e) => set(e.target.checked)} className="size-6 shrink-0 accent-brand" />
      );
    case "Radio":
      return (
        <div className="space-y-1">
          {(field.options ?? []).map((option) => (
            <label key={option.value} className="flex items-center gap-2">
              <input type="radio" name={control.id} value={option.value} checked={value === option.value} disabled={state.readOnly}
                onChange={() => set(option.value)} className="size-6 shrink-0 accent-brand" />
              <FormText text={option.label} locale={contentLocale} />
            </label>
          ))}
        </div>
      );
    case "Dropdown":
      if (field.multiple) {
        const chosen = Array.isArray(value) ? (value as string[]) : [];
        return (
          <div className="space-y-1">
            {(field.options ?? []).map((option) => (
              <label key={option.value} className="flex items-center gap-2">
                <input type="checkbox" checked={chosen.includes(option.value)} disabled={state.readOnly}
                  onChange={(e) => set(e.target.checked ? [...chosen, option.value] : chosen.filter((c) => c !== option.value))} className="size-6 shrink-0 accent-brand" />
                <FormText text={option.label} locale={contentLocale} />
              </label>
            ))}
          </div>
        );
      }
      return (
        <select {...common} value={typeof value === "string" ? value : ""} disabled={state.readOnly} onChange={(e) => set(e.target.value || null)} className={fieldInput}>
          <option value="">{t("choose")}</option>
          {(field.options ?? []).map((option) => (
            <option key={option.value} value={option.value}>
              {textIn(option.label, contentLocale)}
            </option>
          ))}
        </select>
      );
    case "Location": {
      const nodes = selectableNodes(props.locations);
      return (
        <select {...common} value={typeof value === "string" ? value : ""} disabled={state.readOnly} onChange={(e) => set(e.target.value || null)} className={fieldInput}>
          <option value="">{t("choose")}</option>
          {nodes.map((node) => (
            <option key={node.id} value={node.id}>
              {textOf(node.path, contentLocale) || textOf(node.name, contentLocale)}
            </option>
          ))}
        </select>
      );
    }
    case "User":
      return <UserControl {...props} />;
    case "ConfigurationItem":
      return <ConfigurationItemControl {...props} />;
    case "File":
      return <FileControl {...props} />;
    case "Signature":
      return <SignatureControl {...props} />;
    case "Table":
      return <TableControl {...props} />;
    default:
      return null;
  }
}

function RichTextControl({ value, set, state, control }: ControlProps) {
  const t = useTranslations("forms.renderer");
  const [showPreview, setShowPreview] = useState(false);
  const text = typeof value === "string" ? value : "";
  return (
    <div className="space-y-2">
      <button type="button" className={fieldButton} aria-pressed={showPreview} onClick={() => setShowPreview(!showPreview)}>
        {showPreview ? t("richTextEdit") : t("richTextPreview")}
      </button>
      {showPreview ? (
        <div className="rounded-md border border-line p-3">
          <MarkdownView markdown={text} articleId={null} />
        </div>
      ) : (
        <textarea id={control.id} aria-describedby={[control.describedBy, `${control.id}-md`].filter(Boolean).join(" ")} aria-invalid={control.invalid || undefined}
          rows={6} dir="auto" value={text} readOnly={state.readOnly} maxLength={20000} onChange={(e) => set(e.target.value)} className={fieldInput} />
      )}
      <p id={`${control.id}-md`} className="text-sm text-ink-muted">{t("richTextHint")}</p>
    </div>
  );
}

function UserControl({ value, set, state, control, references, preview }: ControlProps) {
  const t = useTranslations("forms.renderer");
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<{ id: string; displayName: string | null }[] | null>(null);
  const [names, setNames] = useState<Record<string, string | null>>({});
  const [failed, setFailed] = useState(false);
  const chosen = typeof value === "string" ? value : null;
  const name = chosen ? (names[chosen] ?? references?.users[chosen] ?? chosen) : null;

  async function search() {
    setFailed(false);
    try {
      setResults(await getJson(`/v1/forms/people?q=${encodeURIComponent(query.trim())}`));
    } catch {
      setFailed(true);
    }
  }

  return (
    <div className="space-y-2">
      {chosen && (
        <p className="flex flex-wrap items-center gap-2">
          <span className="font-semibold" id={`${control.id}-chosen`}>{name}</span>
          {!state.readOnly && (
            <button type="button" className={fieldButton} onClick={() => set(null)}>
              {t("userClear")}
            </button>
          )}
        </p>
      )}
      {!state.readOnly && (
        <div className="flex flex-wrap gap-2">
          <input id={control.id} aria-describedby={control.describedBy} aria-invalid={control.invalid || undefined} value={query} disabled={preview}
            placeholder={t("userSearchPlaceholder")} onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                if (query.trim().length >= 2) void search();
              }
            }}
            className={`${fieldInput} max-w-xs`} />
          <button type="button" className={fieldButton} disabled={preview || query.trim().length < 2} onClick={() => void search()}>
            {t("userSearch")}
          </button>
        </div>
      )}
      <p role="status" aria-live="polite" className="text-sm">
        {failed ? t("userSearchFailed") : results ? t("userResults", { count: results.length }) : ""}
      </p>
      {results && results.length > 0 && (
        <ul className="space-y-1">
          {results.map((person) => (
            <li key={person.id}>
              <button type="button" className={fieldButton}
                onClick={() => {
                  setNames({ ...names, [person.id]: person.displayName });
                  set(person.id);
                  setResults(null);
                  setQuery("");
                }}>
                {t("userPick", { name: person.displayName ?? person.id })}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Task G1: one configuration item, or several, found by number or name. */
function ConfigurationItemControl({ field, value, set, state, control, references, preview }: ControlProps) {
  const t = useTranslations("forms.renderer");
  const [names, setNames] = useState<Record<string, string>>({});
  const chosen = field.multiple ? (Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : []) : typeof value === "string" ? [value] : [];
  const missing = chosen.filter((id) => !names[id] && !references?.configurationItems?.[id]);
  const missingKey = missing.join(",");

  // Names of items chosen before (defaults, answers kept while the page is open).
  useEffect(() => {
    if (preview || !missingKey) return;
    let live = true;
    getJson<CiPick[]>(lookupPath("", [], missingKey.split(",")))
      .then((found) => live && setNames((current) => ({ ...current, ...Object.fromEntries(found.map((f) => [f.id, `${f.number} ${f.name}`])) })))
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [missingKey, preview]);

  const nameOf = (id: string) => names[id] ?? references?.configurationItems?.[id] ?? t("ciUnknown");
  const full = field.multiple && chosen.length >= maxConfigurationItems;
  return (
    <div className="space-y-2">
      {chosen.length > 0 && (
        <ul className="space-y-1" aria-label={t("ciChosen")}>
          {chosen.map((id) => (
            <li key={id} className="flex flex-wrap items-center gap-2">
              <span className="font-semibold" dir="auto">{nameOf(id)}</span>
              {!state.readOnly && (
                <button type="button" className={fieldButton}
                  onClick={() => set(field.multiple ? chosen.filter((x) => x !== id) : null)}>
                  {t("ciRemove", { name: nameOf(id) })}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {!state.readOnly && !full && (
        <CiLookup id={control.id} label={t(field.multiple ? "ciFindMore" : chosen.length > 0 ? "ciFindOther" : "ciFind")}
          describedBy={control.describedBy} invalid={control.invalid} classIds={field.configurationItemClasses ?? []} exclude={chosen} disabled={preview}
          onPick={(item) => {
            setNames((current) => ({ ...current, [item.id]: `${item.number} ${item.name}` }));
            set(field.multiple ? [...chosen, item.id] : item.id);
          }} />
      )}
      {preview && <p className="text-sm text-ink-muted">{t("ciPreview")}</p>}
    </div>
  );
}

function useUploader(fieldKey: string, csrfToken: string | undefined) {
  const [uploads, setUploads] = useState<Record<string, FormUpload>>({});
  const [message, setMessage] = useState<string | null>(null);
  const t = useTranslations("forms.renderer");
  async function upload(file: Blob, name: string): Promise<string | null> {
    if (!csrfToken) return null;
    const form = new FormData();
    form.append("file", file, name);
    form.append("field", fieldKey);
    setMessage(t("uploading"));
    try {
      const { body } = await postForm<FormUpload>("/v1/form-uploads", form, csrfToken);
      setUploads((current) => ({ ...current, [body.id]: body }));
      setMessage(t("uploaded", { name: body.fileName }));
      return body.id;
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setMessage(code && t.has(`errors.${code}`) ? t(`errors.${code}`) : t("uploadFailed"));
      return null;
    }
  }
  async function remove(id: string) {
    if (csrfToken) await deleteRequest(`/v1/form-uploads/${id}`, csrfToken).catch(() => undefined);
    setUploads((current) => {
      const next = { ...current };
      delete next[id];
      return next;
    });
  }
  return { uploads, upload, remove, message };
}

function FileControl(props: ControlProps) {
  const { field, value, set, state, control, csrfToken, preview } = props;
  const t = useTranslations("forms.renderer");
  const ids = Array.isArray(value) ? (value as string[]) : [];
  const { uploads, upload, remove, message } = useUploader(field.key, csrfToken);
  const maxFiles = field.validation?.maxFiles ?? 5;
  const accept = field.validation?.fileTypes?.length ? field.validation.fileTypes.map((e) => `.${e}`).join(",") : undefined;
  return (
    <div className="space-y-2">
      {ids.length > 0 && (
        <ul className="space-y-1">
          {ids.map((id) => (
            <li key={id} className="flex flex-wrap items-center gap-2">
              <span>{uploads[id]?.fileName ?? t("fileUploaded")}</span>
              {!state.readOnly && (
                <button type="button" className={fieldButton}
                  onClick={() => {
                    void remove(id);
                    set(ids.filter((x) => x !== id));
                  }}>
                  {t("fileRemove", { name: uploads[id]?.fileName ?? "" })}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {!state.readOnly && ids.length < maxFiles && (
        <input id={control.id} type="file" accept={accept} disabled={preview} aria-label={textIn(field.label, props.contentLocale)} aria-describedby={[control.describedBy, `${control.id}-limits`].filter(Boolean).join(" ")}
          aria-invalid={control.invalid || undefined} multiple={maxFiles - ids.length > 1}
          onChange={async (e) => {
            const files = Array.from(e.target.files ?? []).slice(0, maxFiles - ids.length);
            e.target.value = "";
            const added: string[] = [];
            for (const file of files) {
              const id = await upload(file, file.name);
              if (id) added.push(id);
            }
            if (added.length > 0) set([...ids, ...added]);
          }}
          className="block text-sm" />
      )}
      <p id={`${control.id}-limits`} className="text-sm text-ink-muted">
        {t("fileLimits", { count: maxFiles, types: field.validation?.fileTypes?.join(", ") || t("fileAnyType") })}
        {preview ? ` ${t("previewNoUpload")}` : ""}
      </p>
      <p role="status" aria-live="polite" className="text-sm">{message}</p>
    </div>
  );
}

function SignatureControl({ field, value, set, state, control, csrfToken, preview }: ControlProps) {
  const t = useTranslations("forms.renderer");
  const current = value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, JsonValue>) : null;
  const [mode, setMode] = useState<"draw" | "type">(current && "typedName" in current ? "type" : "draw");
  const { upload, message } = useUploader(field.key, csrfToken);
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-4" role="radiogroup" aria-label={t("signatureHow")}>
        {(["draw", "type"] as const).map((m) => (
          <label key={m} className="flex items-center gap-2">
            <input type="radio" name={`${control.id}-mode`} checked={mode === m} disabled={state.readOnly}
              onChange={() => {
                setMode(m);
                set(null);
              }} className="size-6 shrink-0 accent-brand" />
            {t(m === "draw" ? "signatureDraw" : "signatureType")}
          </label>
        ))}
      </div>
      {mode === "draw" ? (
        current && "attachmentId" in current ? (
          <p className="flex flex-wrap items-center gap-2">
            <span>{t("signatureSaved")}</span>
            {!state.readOnly && (
              <button type="button" className={fieldButton} onClick={() => set(null)}>
                {t("signatureAgain")}
              </button>
            )}
          </p>
        ) : (
          <SignaturePad id={control.id} describedBy={control.describedBy} disabled={state.readOnly || preview}
            onSave={async (png) => {
              const id = await upload(png, "signature.png");
              if (id) set({ attachmentId: id });
            }} />
        )
      ) : (
        <div className="space-y-2">
          <label htmlFor={`${control.id}-typed`} className="block">{t("signatureName")}</label>
          <input id={`${control.id}-typed`} dir="auto" autoComplete="name" readOnly={state.readOnly} maxLength={200}
            aria-describedby={control.describedBy} aria-invalid={control.invalid || undefined}
            value={typeof current?.typedName === "string" ? current.typedName : ""}
            onChange={(e) => set({ typedName: e.target.value, confirmed: current?.confirmed === true })} className={`${fieldInput} max-w-md`} />
          <label className="flex items-center gap-2">
            <input type="checkbox" disabled={state.readOnly} checked={current?.confirmed === true}
              onChange={(e) => set({ typedName: typeof current?.typedName === "string" ? current.typedName : "", confirmed: e.target.checked })} className="size-6 shrink-0 accent-brand" />
            {t("signatureConfirm")}
          </label>
        </div>
      )}
      {preview && mode === "draw" && <p className="text-sm text-ink-muted">{t("previewNoUpload")}</p>}
      <p role="status" aria-live="polite" className="text-sm">{message}</p>
    </div>
  );
}

function TableControl(props: ControlProps) {
  const t = useTranslations("forms.renderer");
  const { field, value, set, state, contentLocale, errors, control } = props;
  const rows = Array.isArray(value) ? (value as Record<string, JsonValue>[]) : [];
  const columns = field.columns ?? [];
  const max = Math.min(field.maxRows ?? 100, 100);
  const setCell = (i: number, key: string, cell: JsonValue) => set(rows.map((row, j) => (j === i ? { ...row, [key]: cell } : row)));
  return (
    <div className="space-y-2">
      <div className="overflow-x-auto">
        <table className="min-w-full border-collapse text-sm" aria-describedby={control.describedBy}>
          <caption className="sr-only"><FormText text={field.label} locale={contentLocale} /></caption>
          <thead>
            <tr>
              <th scope="col" className="border border-line p-2 text-start">{t("row")}</th>
              {columns.map((c) => (
                <th key={c.key} scope="col" className="border border-line p-2 text-start">
                  <FormText text={c.label} locale={contentLocale} />
                  {c.required && <span className="text-bad"> ({t("required")})</span>}
                </th>
              ))}
              {!state.readOnly && <th scope="col" className="border border-line p-2"><span className="sr-only">{t("rowActions")}</span></th>}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={i}>
                <th scope="row" className="border border-line p-2 font-normal">{i + 1}</th>
                {columns.map((c) => {
                  const cellErrors = errors.filter((e) => e.field === `${field.key}.${i}.${c.key}`);
                  const cellId = `${control.id}-${i}-${c.key}`;
                  const cellLabel = `${textIn(c.label, contentLocale)} ${t("rowNumber", { number: i + 1 })}`;
                  const cell = row[c.key] ?? null;
                  return (
                    <td key={c.key} className="border border-line p-1 align-top">
                      {c.type === "Checkbox" ? (
                        <input id={cellId} type="checkbox" aria-label={cellLabel} checked={cell === true} disabled={state.readOnly}
                          onChange={(e) => setCell(i, c.key, e.target.checked)} className="size-6 shrink-0 accent-brand" />
                      ) : c.type === "Dropdown" ? (
                        <select id={cellId} aria-label={cellLabel} value={typeof cell === "string" ? cell : ""} disabled={state.readOnly}
                          aria-invalid={cellErrors.length > 0 || undefined} onChange={(e) => setCell(i, c.key, e.target.value || null)} className={fieldInput}>
                          <option value="">{t("choose")}</option>
                          {(c.options ?? []).map((o) => (
                            <option key={o.value} value={o.value}>{textIn(o.label, contentLocale)}</option>
                          ))}
                        </select>
                      ) : (
                        <input id={cellId} aria-label={cellLabel} readOnly={state.readOnly} aria-invalid={cellErrors.length > 0 || undefined}
                          type={c.type === "Number" ? "number" : c.type === "Date" ? "date" : "text"} step={c.type === "Number" ? "any" : undefined}
                          dir={c.type === "Text" ? "auto" : "ltr"}
                          value={typeof cell === "string" || typeof cell === "number" ? cell : ""}
                          onChange={(e) => setCell(i, c.key, c.type === "Number" ? (e.target.value === "" ? null : Number(e.target.value)) : e.target.value || null)}
                          className={fieldInput} />
                      )}
                      {cellErrors.length > 0 && <p className="text-bad">{cellErrors.map((e) => errorText(t, e, c)).join(" ")}</p>}
                    </td>
                  );
                })}
                {!state.readOnly && (
                  <td className="border border-line p-1">
                    <button type="button" className={fieldButton} onClick={() => set(rows.filter((_, j) => j !== i))}>
                      {t("removeRow", { number: i + 1 })}
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!state.readOnly && rows.length < max && (
        <button type="button" className={fieldButton} onClick={() => set([...rows, {}])}>
          {t("addRow")}
        </button>
      )}
    </div>
  );
}
