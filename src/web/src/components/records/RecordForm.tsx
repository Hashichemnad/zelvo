"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { checkAnswers } from "@/lib/forms/check";
import type { AnswerError, Answers, ResolvedForm } from "@/lib/forms/types";
import { selectableNodes, textOf, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { CustomRecord, RecordType } from "@/lib/records";

/**
 * Raising a record of a custom type (task K2): title, description, category and the C1 form the
 * type uses for that category; staff with the type's edit permission also set the priority.
 */
export function RecordForm({ type, csrfToken, categories, locations, staff }: {
  type: RecordType;
  csrfToken: string;
  categories: TreeNode[];
  locations: TreeNode[];
  staff: boolean;
}) {
  const t = useTranslations("records.form");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [priority, setPriority] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "invalid" | "failed" | "formInvalid" | "formChanged" | "rateLimited">("idle");
  const [form, setForm] = useState<ResolvedForm | null>(null);
  const [formAnswers, setFormAnswers] = useState<Answers>({});
  const [formErrors, setFormErrors] = useState<AnswerError[]>([]);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const query = new URLSearchParams({ recordType: "Custom", customRecordTypeId: type.id, language: locale });
    if (categoryId) query.set("categoryId", categoryId);
    getJson<ResolvedForm>(`/v1/forms/resolve?${query}`)
      .then((resolved) => {
        if (cancelled) return;
        setForm(resolved.form ? resolved : null);
        setFormErrors([]);
        setFormAnswers((current) => Object.fromEntries(Object.entries(current).filter(([key]) => resolved.fields.some((f) => f.field.key === key))));
      })
      .catch(() => !cancelled && setForm(null));
    return () => {
      cancelled = true;
    };
  }, [type.id, categoryId, locale, reload]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (title.trim().length < 3) {
      setStatus("invalid");
      return;
    }
    let formBody: { formVersionId: string; formAnswers: Answers } | null = null;
    if (form?.form) {
      const checked = checkAnswers(form.fields.map((f) => f.field), formAnswers, form.defaults);
      setFormErrors(checked.errors);
      if (checked.errors.length > 0) {
        setStatus("formInvalid");
        return;
      }
      formBody = { formVersionId: form.form.versionId, formAnswers: checked.toSend };
    }
    setStatus("saving");
    try {
      const created = await postJson<CustomRecord>(`/v1/records/${type.key}`, {
        title: title.trim(), description: description.trim() || undefined, categoryId: categoryId || undefined,
        priority: staff && priority ? Number(priority) : undefined, ...formBody,
      }, csrfToken);
      router.push(`/records/${type.key}/${created.id}`);
    } catch (error) {
      const answerErrors = error instanceof ApiError ? (error.details.formErrors as AnswerError[] | undefined) : undefined;
      if (answerErrors?.length) {
        setFormErrors(answerErrors);
        setStatus("formInvalid");
        return;
      }
      if (error instanceof ApiError && error.code === "form-changed") {
        setStatus("formChanged");
        setReload((n) => n + 1);
        return;
      }
      setStatus(error instanceof ApiError && error.rateLimited ? "rateLimited" : error instanceof ApiError && error.status === 400 ? "invalid" : "failed");
    }
  }

  const choices = selectableNodes(categories);
  return (
    <form onSubmit={submit} className="max-w-2xl space-y-5" data-testid="record-form" noValidate>
      <div className="space-y-1">
        <label htmlFor={`${id}-title`} className="block font-semibold">{t("title")}</label>
        <input id={`${id}-title`} value={title} onChange={(e) => { setTitle(e.target.value); setStatus("idle"); }} required minLength={3} maxLength={300} dir="auto"
          aria-invalid={status === "invalid" ? true : undefined} className={fieldInput} />
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-description`} className="block font-semibold">{t("description")}</label>
        <textarea id={`${id}-description`} value={description} onChange={(e) => setDescription(e.target.value)} rows={6} maxLength={20000} dir="auto" className={fieldInput} />
      </div>
      {choices.length > 0 && (
        <div className="space-y-1">
          <label htmlFor={`${id}-category`} className="block font-semibold">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={fieldInput}>
            <option value="">{t("notSure")}</option>
            {choices.map((node) => <option key={node.id} value={node.id}>{textOf(node.path, locale) || textOf(node.name, locale)}</option>)}
          </select>
        </div>
      )}
      {staff && (
        <div className="space-y-1">
          <label htmlFor={`${id}-priority`} className="block font-semibold">{t("priority")}</label>
          <select id={`${id}-priority`} value={priority} onChange={(e) => setPriority(e.target.value)} className={fieldInput}>
            <option value="">{t("defaultPriority", { priority: type.defaultPriority })}</option>
            {[1, 2, 3, 4, 5].map((p) => <option key={p} value={p}>{`P${p}`}</option>)}
          </select>
        </div>
      )}
      {form?.form && (
        <section aria-labelledby={`${id}-form`} className="space-y-3 rounded-lg border border-line p-4">
          <h2 id={`${id}-form`} className="text-lg font-semibold">{textOf(form.form.name, locale)}</h2>
          <FormRenderer fields={form.fields} answers={formAnswers} onChange={(answers) => { setFormAnswers(answers); if (status === "formInvalid") setStatus("idle"); }}
            defaults={form.defaults} references={form.references} errors={formErrors} contentLocale={locale}
            locations={locations} csrfToken={csrfToken} idPrefix={`${id}-answers`} />
        </section>
      )}
      <button type="submit" className={primaryAction} disabled={status === "saving"}>{t("send")}</button>
      <p role="status" aria-live="polite" className={status === "idle" || status === "saving" ? "" : "text-bad"}>
        {status === "idle" ? "" : t(`status.${status}`)}
      </p>
    </form>
  );
}
