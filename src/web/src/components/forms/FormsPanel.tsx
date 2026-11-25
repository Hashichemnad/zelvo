"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { BilingualInputs, Localized, SaveStatusLine, cellClass, smallButton, tableClass, useSaver } from "@/components/directory/common";
import { deleteRequest, getJson, patchJson, postJson, putJson } from "@/lib/client-api";
import { selectableNodes, textOf, toInput, type TreeNode } from "@/lib/directory";
import type { FieldSetDetail, FormBinding, FormDetail, FormSummary } from "@/lib/forms/types";
import { fieldInput, primaryAction } from "./ui";

type Locale = "en" | "ar";

/** A new form or field set: key and bilingual name. */
function CreateForm({ kind, csrfToken }: { kind: "forms" | "fieldSets"; csrfToken: string }) {
  const t = useTranslations("forms.list");
  const router = useRouter();
  const id = useId();
  const [key, setKey] = useState("");
  const [name, setName] = useState({ en: "", ar: "" });
  const { status, run, busy } = useSaver();
  return (
    <form className="space-y-2 rounded-lg border border-line p-3" aria-label={kind === "forms" ? t("createForm") : t("createFieldSet")}
      onSubmit={(e) => {
        e.preventDefault();
        void run(async () => {
          const created = await postJson<{ id: string }>(kind === "forms" ? "/v1/forms" : "/v1/field-sets", { key: key.trim(), name: toInput(name.en, name.ar) }, csrfToken);
          router.push(kind === "forms" ? `/settings/forms/${created.id}` : `/settings/field-sets/${created.id}`);
        });
      }}>
      <div className="space-y-1">
        <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
        <input id={`${id}-key`} dir="ltr" value={key} maxLength={60} aria-describedby={`${id}-key-hint`}
          onChange={(e) => setKey(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, "-"))} className={fieldInput} />
        <p id={`${id}-key-hint`} className="text-sm text-ink-muted">{t("keyHint")}</p>
      </div>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} maxLength={150} onChange={setName} />
      <button type="submit" className={primaryAction} disabled={busy || !key.trim() || (!name.en.trim() && !name.ar.trim())}>
        {kind === "forms" ? t("createForm") : t("createFieldSet")}
      </button>
      <SaveStatusLine status={status} />
    </form>
  );
}

/** Settings → Forms: the tenant's forms, where each is used, and a new form. */
export function FormsPanel({ initial, csrfToken }: { initial: FormSummary[]; csrfToken: string }) {
  const t = useTranslations("forms.list");
  const locale = useLocale() as Locale;
  return (
    <div className="space-y-4">
      {initial.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <table className={tableClass}>
          <caption className="sr-only">{t("caption")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("name")}</th>
              <th scope="col" className={cellClass}>{t("state")}</th>
              <th scope="col" className={cellClass}>{t("usedFor")}</th>
            </tr>
          </thead>
          <tbody>
            {initial.map((form) => (
              <tr key={form.id}>
                <td className={cellClass}>
                  <Link href={`/settings/forms/${form.id}`} className="font-semibold text-brand underline">
                    <Localized text={form.name} locale={locale} />
                  </Link>
                  <span className="block text-sm text-ink-muted" dir="ltr">{form.key}</span>
                </td>
                <td className={cellClass}>
                  {t(`states.${form.state}`)}
                  {form.publishedVersion !== null && <span className="block text-sm">{t("version", { number: form.publishedVersion })}</span>}
                  {form.hasUnpublishedChanges && form.state !== "Draft" && <span className="block text-sm text-ink-muted">{t("unpublishedChanges")}</span>}
                </td>
                <td className={cellClass}>
                  {form.usedBy.length === 0
                    ? t("notUsed")
                    : form.usedBy.map((b) => (b.category ? textOf(b.category.path, locale) : t("defaultFor", { type: t(`recordTypes.${b.recordType}`) }))).join("; ")}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <CreateForm kind="forms" csrfToken={csrfToken} />
    </div>
  );
}

/** Record types whose new tickets fill a form chosen per category (service requests use their service's form). */
const bindableTypes = ["Incident", "Complaint", "Inquiry", "Suggestion"] as const;

/** Which form each category uses (module 2: "each category can ask its own questions"). */
export function FormBindingsPanel({ initial, forms, categories, csrfToken }: { initial: FormBinding[]; forms: FormSummary[]; categories: TreeNode[]; csrfToken: string }) {
  const t = useTranslations("forms.bindings");
  const tList = useTranslations("forms.list");
  const locale = useLocale() as Locale;
  const id = useId();
  const [bindings, setBindings] = useState(initial);
  const [categoryId, setCategoryId] = useState("");
  const [formId, setFormId] = useState("");
  // Task C6: complaints, inquiries and suggestions have their own forms per category too.
  const [recordType, setRecordType] = useState<(typeof bindableTypes)[number]>("Incident");
  const { status, run, busy } = useSaver();
  const usable = forms.filter((f) => f.state !== "Retired");
  const nodes = selectableNodes(categories);
  const reload = async () => setBindings(await getJson<FormBinding[]>("/v1/form-bindings"));
  return (
    <div className="space-y-3">
      <p className="max-w-2xl">{t("intro")}</p>
      {bindings.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <table className={tableClass}>
          <caption className="sr-only">{t("caption")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("recordType")}</th>
              <th scope="col" className={cellClass}>{t("where")}</th>
              <th scope="col" className={cellClass}>{t("form")}</th>
              <th scope="col" className={cellClass}>{t("actions")}</th>
            </tr>
          </thead>
          <tbody>
            {bindings.map((b) => {
              const where = b.category ? textOf(b.category.path, locale) : tList("defaultFor", { type: tList(`recordTypes.${b.recordType}`) });
              return (
                <tr key={b.id}>
                  <td className={cellClass}>{t(`recordTypeNames.${b.recordType}`)}</td>
                  <td className={cellClass}>{where}</td>
                  <td className={cellClass}>
                    <Localized text={b.form.name} locale={locale} />
                    {b.form.state !== "Published" && <span className="block text-sm text-ink-muted">{t("notPublished")}</span>}
                  </td>
                  <td className={cellClass}>
                    <button type="button" className={smallButton} disabled={busy}
                      onClick={() => void run(async () => { await deleteRequest(`/v1/form-bindings/${b.id}`, csrfToken); await reload(); })}>
                      {t("remove", { where })}
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <form className="grid gap-2 rounded-lg border border-line p-3 sm:grid-cols-4" aria-label={t("add")}
        onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            await putJson("/v1/form-bindings", { recordType, categoryId: categoryId || null, formId }, csrfToken);
            await reload();
          });
        }}>
        <div className="space-y-1">
          <label htmlFor={`${id}-type`} className="block font-semibold">{t("recordType")}</label>
          <select id={`${id}-type`} value={recordType} onChange={(e) => setRecordType(e.target.value as (typeof bindableTypes)[number])} className={fieldInput}>
            {bindableTypes.map((type) => <option key={type} value={type}>{tList(`recordTypes.${type}`)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-category`} className="block font-semibold">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={fieldInput}>
            <option value="">{tList("defaultFor", { type: tList(`recordTypes.${recordType}`) })}</option>
            {nodes.map((n) => <option key={n.id} value={n.id}>{textOf(n.path, locale) || textOf(n.name, locale)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-form`} className="block font-semibold">{t("form")}</label>
          <select id={`${id}-form`} value={formId} onChange={(e) => setFormId(e.target.value)} className={fieldInput}>
            <option value="">{t("chooseForm")}</option>
            {usable.map((f) => <option key={f.id} value={f.id}>{textOf(f.name, locale)}</option>)}
          </select>
        </div>
        <div className="flex items-end">
          <button type="submit" className={primaryAction} disabled={busy || !formId}>{t("add")}</button>
        </div>
      </form>
      <SaveStatusLine status={status} />
    </div>
  );
}

/** Settings → Field sets: reusable groups of fields and the forms that use them. */
export function FieldSetsPanel({ initial, csrfToken }: { initial: FieldSetDetail[]; csrfToken: string }) {
  const t = useTranslations("forms.list");
  const locale = useLocale() as Locale;
  return (
    <div className="space-y-4">
      {initial.length === 0 ? (
        <p>{t("emptyFieldSets")}</p>
      ) : (
        <table className={tableClass}>
          <caption className="sr-only">{t("captionFieldSets")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("name")}</th>
              <th scope="col" className={cellClass}>{t("state")}</th>
              <th scope="col" className={cellClass}>{t("usedByForms")}</th>
            </tr>
          </thead>
          <tbody>
            {initial.map((set) => (
              <tr key={set.id}>
                <td className={cellClass}>
                  <Link href={`/settings/field-sets/${set.id}`} className="font-semibold text-brand underline">
                    <Localized text={set.name} locale={locale} />
                  </Link>
                  <span className="block text-sm text-ink-muted" dir="ltr">{set.key}</span>
                </td>
                <td className={cellClass}>
                  {t(`states.${set.state}`)}
                  {set.publishedVersion !== null && <span className="block text-sm">{t("version", { number: set.publishedVersion })}</span>}
                </td>
                <td className={cellClass}>{set.usedBy.length === 0 ? t("notUsed") : set.usedBy.map((f) => textOf(f.name, locale)).join("; ")}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <CreateForm kind="fieldSets" csrfToken={csrfToken} />
    </div>
  );
}

/** A form's or field set's key, names and description, and its life cycle actions. */
export function FormDetailsPanel({ kind, initial, csrfToken }: { kind: "form" | "fieldSet"; initial: FormDetail | FieldSetDetail; csrfToken: string }) {
  const t = useTranslations("forms.details");
  const router = useRouter();
  const id = useId();
  const base = kind === "form" ? "/v1/forms" : "/v1/field-sets";
  const [detail, setDetail] = useState(initial);
  const [key, setKey] = useState(initial.key);
  const [name, setName] = useState({ en: initial.name.en ?? "", ar: initial.name.ar ?? "" });
  const [description, setDescription] = useState({ en: initial.description.en ?? "", ar: initial.description.ar ?? "" });
  const [copyKey, setCopyKey] = useState(`${initial.key}-copy`);
  const { status, run, busy } = useSaver();
  const act = (path: string) => run(async () => setDetail(await postJson(`${base}/${detail.id}/${path}`, undefined, csrfToken)));
  return (
    <div className="space-y-4">
      <form className="space-y-2" aria-label={t("title")}
        onSubmit={(e) => {
          e.preventDefault();
          void run(async () => setDetail(await patchJson(`${base}/${detail.id}`, {
            key: key.trim(), name: toInput(name.en, name.ar), description: { en: description.en.trim() || null, ar: description.ar.trim() || null },
          }, csrfToken)));
        }}>
        <div className="space-y-1">
          <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
          <input id={`${id}-key`} dir="ltr" value={key} maxLength={60} onChange={(e) => setKey(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, "-"))} className={fieldInput} />
        </div>
        <BilingualInputs id={`${id}-name`} label={t("name")} maxLength={150} en={name.en} ar={name.ar} onChange={setName} />
        <BilingualInputs id={`${id}-description`} label={t("description")} maxLength={1000} en={description.en} ar={description.ar} onChange={setDescription} />
        <button type="submit" className={smallButton} disabled={busy}>{t("save")}</button>
      </form>
      <div className="flex flex-wrap items-end gap-2">
        {detail.state === "Published" && <button type="button" className={smallButton} disabled={busy} onClick={() => void act("retire")}>{t("retire")}</button>}
        {detail.state === "Retired" && <button type="button" className={smallButton} disabled={busy} onClick={() => void act("reactivate")}>{t("reactivate")}</button>}
        {detail.publishedVersion === null && (
          <button type="button" className={smallButton} disabled={busy}
            onClick={() => void run(async () => { await deleteRequest(`${base}/${detail.id}`, csrfToken); router.push(kind === "form" ? "/settings/forms" : "/settings/field-sets"); })}>
            {t("delete")}
          </button>
        )}
        {kind === "form" && (
          <span className="flex flex-wrap items-end gap-2">
            <span className="space-y-1">
              <label htmlFor={`${id}-copy`} className="block text-sm">{t("copyKey")}</label>
              <input id={`${id}-copy`} dir="ltr" value={copyKey} maxLength={60} onChange={(e) => setCopyKey(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, "-"))} className={fieldInput} />
            </span>
            <button type="button" className={smallButton} disabled={busy || !copyKey}
              onClick={() => void run(async () => {
                const copy = await postJson<{ id: string }>(`${base}/${detail.id}/copy`, { key: copyKey }, csrfToken);
                router.push(`/settings/forms/${copy.id}`);
              })}>
              {t("copy")}
            </button>
          </span>
        )}
      </div>
      <p className="text-sm">{t("stateNow", { state: t(`states.${detail.state}`) })}</p>
      {"usedBy" in detail && detail.usedBy.length > 0 && kind === "fieldSet" && (
        <p className="text-sm">{t("usedBy", { forms: (detail as FieldSetDetail).usedBy.map((f) => f.name.en || f.name.ar || f.key).join(", ") })}</p>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}
