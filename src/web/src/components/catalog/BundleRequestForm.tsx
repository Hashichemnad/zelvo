"use client";

import { useId, useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { fieldButton, primaryAction } from "@/components/forms/ui";
import { Localized } from "@/components/directory/common";
import { ApiError, deleteRequest, postForm, postJson } from "@/lib/client-api";
import { checkAnswers } from "@/lib/forms/check";
import type { AnswerError, Answers, ResolvedField, ResolvedForm } from "@/lib/forms/types";
import type { TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { uploadFieldOf, type BundleForm, type PublishedService, type ServiceRequest } from "@/lib/catalog";

interface Upload {
  id: string;
  fileName: string;
}

type Status = "idle" | "saving" | "invalid" | "failed" | "rateLimited" | "formChanged" | "unavailable" | "prerequisite" | "offerGone";

/** The fields of each form that are not already asked by a form before it: a question several services ask is asked once. */
export function sharedFields(forms: (ResolvedForm | null)[]): ResolvedField[][] {
  const seen = new Set<string>();
  return forms.map((form) => (form?.form ? form.fields.filter((f) => {
    if (seen.has(f.field.key)) return false;
    seen.add(f.field.key);
    return true;
  }) : []));
}

/**
 * Requesting a bundle (task C5): one combined form (the bundle's own questions, then each
 * service's, a shared question asked once), the optional services to include, and each service's
 * documents. Zelvo makes a part per service; parts start in their stage.
 */
export function BundleRequestForm({ service, bundle, csrfToken, locations, offer }: {
  service: PublishedService;
  bundle: BundleForm;
  csrfToken: string;
  locations: TreeNode[];
  offer?: { id: string; answers: Answers } | null;
}) {
  const t = useTranslations("catalog.request");
  const tb = useTranslations("catalog.request.bundle");
  const tOffers = useTranslations("catalog.offers");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [chosen, setChosen] = useState<Set<string>>(() => new Set(bundle.parts.filter((p) => p.required).map((p) => p.serviceId)));
  const [answers, setAnswers] = useState<Answers>(() => ({ ...(offer?.answers ?? {}) }));
  const [errors, setErrors] = useState<AnswerError[]>([]);
  const [documentErrors, setDocumentErrors] = useState<AnswerError[]>([]);
  const [uploads, setUploads] = useState<Record<string, Upload[]>>({});
  const [uploadMessage, setUploadMessage] = useState("");
  const [status, setStatus] = useState<Status>("idle");

  const included = bundle.parts.filter((p) => chosen.has(p.serviceId));
  const blocked = bundle.parts.find((p) => p.required && !p.available);
  const forms = useMemo(() => [bundle.form, ...included.map((p) => p.form)], [bundle.form, included]);
  const fieldsPerForm = useMemo(() => sharedFields(forms), [forms]);
  const defaults = useMemo(() => Object.assign({}, ...forms.map((f) => f?.defaults ?? {})) as Answers, [forms]);
  const docKey = (serviceId: string, key: string) => `${serviceId}:${key}`;

  async function upload(serviceId: string, documentKey: string, file: File) {
    const data = new FormData();
    data.append("file", file, file.name);
    data.append("field", uploadFieldOf(documentKey));
    setUploadMessage(t("uploading"));
    try {
      const { body } = await postForm<Upload>("/v1/form-uploads", data, csrfToken);
      setUploads((u) => ({ ...u, [docKey(serviceId, documentKey)]: [...(u[docKey(serviceId, documentKey)] ?? []), { id: body.id, fileName: body.fileName }] }));
      setUploadMessage(t("uploaded", { name: body.fileName }));
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setUploadMessage(code && t.has(`uploadErrors.${code}`) ? t(`uploadErrors.${code}`) : t("uploadFailed"));
    }
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const found: AnswerError[] = [];
    const toSend: Answers = {};
    for (const form of forms) {
      if (!form?.form) continue;
      const own = Object.fromEntries(Object.entries(answers).filter(([key]) => form.fields.some((f) => f.field.key === key)));
      const checked = checkAnswers(form.fields.map((f) => f.field), own, form.defaults);
      for (const e of checked.errors) if (!found.some((x) => x.field === e.field)) found.push(e);
      Object.assign(toSend, checked.toSend);
    }
    const missing = included.flatMap((p) => p.documents.filter((d) => d.required && !(uploads[docKey(p.serviceId, d.key)]?.length))
      .map((d) => ({ field: `items.${p.code}.documents.${d.key}`, code: "document-required" })));
    setErrors(found);
    setDocumentErrors(missing);
    if (found.length > 0 || missing.length > 0) {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    try {
      const created = await postJson<ServiceRequest>("/v1/service-requests", {
        serviceId: service.id,
        offerId: offer?.id,
        formVersionId: bundle.form.form?.versionId,
        formAnswers: toSend,
        items: included.map((p) => ({
          serviceId: p.serviceId,
          formVersionId: p.form?.form?.versionId,
          documents: Object.fromEntries(p.documents.map((d) => [d.key, (uploads[docKey(p.serviceId, d.key)] ?? []).map((u) => u.id)]).filter(([, list]) => (list as string[]).length > 0)),
        })),
      }, csrfToken);
      router.push(`/my-requests/${created.id}`);
    } catch (error) {
      if (!(error instanceof ApiError)) {
        setStatus("failed");
        return;
      }
      const answerErrors = error.details.formErrors as AnswerError[] | undefined;
      const docErrors = error.details.documentErrors as AnswerError[] | undefined;
      if (answerErrors?.length || docErrors?.length) {
        setErrors(answerErrors ?? []);
        setDocumentErrors(docErrors ?? []);
        setStatus("invalid");
        return;
      }
      setStatus(error.rateLimited ? "rateLimited"
        : error.code === "form-changed" ? "formChanged"
          : error.code === "bundle-part-unavailable" || error.code === "service-suspended" ? "unavailable"
            : error.code === "prerequisite-missing" ? "prerequisite"
              : error.code === "offer-unavailable" ? "offerGone"
                : error.status === 400 ? "invalid" : "failed");
    }
  }

  const messages: Record<Status, string> = {
    idle: "", saving: t("submitting"), invalid: t("invalid"), failed: t("failed"), rateLimited: tCommon("rateLimited"), formChanged: t("formChanged"),
    unavailable: tb("errors.bundle-part-unavailable"), prerequisite: t("errors.prerequisite-missing"), offerGone: tb("errors.offer-unavailable"),
  };

  if (blocked) {
    return <p role="alert">{tb("unavailable", { name: blocked.name ? (blocked.name[locale] ?? blocked.name.en ?? blocked.code ?? "") : (blocked.code ?? "") })}</p>;
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-5" noValidate data-testid="request-bundle">
      <fieldset className="space-y-2 rounded-lg border border-line p-4">
        <legend className="font-semibold">{tb("parts")}</legend>
        <p className="text-sm text-ink-muted">{tb("partsHint")}</p>
        <ul className="space-y-2">
          {bundle.parts.map((p) => {
            const name = p.name ? <Localized text={p.name} locale={locale} /> : p.code;
            return (
              <li key={p.serviceId} className="flex flex-wrap items-center gap-2" data-testid="bundle-part-choice">
                {p.required ? (
                  <span className="font-semibold">{name}</span>
                ) : (
                  <label className="flex items-center gap-2 font-semibold">
                    <input type="checkbox" className="size-6" checked={chosen.has(p.serviceId)} disabled={!p.available}
                      onChange={(e) => setChosen((c) => {
                        const next = new Set(c);
                        if (e.target.checked) next.add(p.serviceId); else next.delete(p.serviceId);
                        return next;
                      })} />
                    {name}
                  </label>
                )}
                <span className="text-sm text-ink-muted">
                  {p.required ? tb("mandatory") : tb("optional")} · {tb("stage", { number: p.stage + 1 })}
                  {p.fee?.paid && p.fee.amountAed !== null ? ` · ${tb("fee", { amount: p.fee.amountAed })}` : ""}
                </span>
              </li>
            );
          })}
        </ul>
      </fieldset>
      {offer && <p role="note" className="rounded-md border border-line p-2">{tOffers("filling")}</p>}
      <p className="text-sm text-ink-muted">{tb("sharedHint")}</p>
      {forms.map((form, i) => {
        const fields = fieldsPerForm[i] ?? [];
        if (!form?.form || fields.length === 0) return null;
        const part = i === 0 ? null : included[i - 1];
        return (
          <section key={form.form.versionId} aria-labelledby={`${id}-form-${i}`} className="space-y-3 rounded-lg border border-line p-4">
            <h2 id={`${id}-form-${i}`} className="text-lg font-semibold">
              {part?.name ? <Localized text={part.name} locale={locale} /> : <Localized text={form.form.name} locale={locale} />}
            </h2>
            <FormRenderer fields={fields} answers={answers} onChange={(next) => setAnswers((a) => ({ ...a, ...next }))} defaults={defaults} references={form.references}
              errors={errors} contentLocale={locale} locations={locations} csrfToken={csrfToken} idPrefix={`${id}-f${i}`} />
          </section>
        );
      })}
      {included.filter((p) => p.documents.length > 0).map((p) => (
        <section key={p.serviceId} aria-labelledby={`${id}-docs-${p.serviceId}`} className="space-y-3 rounded-lg border border-line p-4">
          <h2 id={`${id}-docs-${p.serviceId}`} className="text-lg font-semibold">
            {t("documents")}: {p.name ? <Localized text={p.name} locale={locale} /> : p.code}
          </h2>
          {p.documents.map((d) => {
            const key = docKey(p.serviceId, d.key);
            const errs = documentErrors.filter((e) => e.field === `items.${p.code}.documents.${d.key}`);
            const errorId = `${id}-doc-${key}-error`;
            return (
              <div key={d.key} className="space-y-1">
                <label htmlFor={`${id}-doc-${key}`} className="block font-semibold">
                  <Localized text={d.name} locale={locale} /> <span className="text-sm font-normal text-ink-muted">({d.required ? t("mandatory") : t("optional")})</span>
                </label>
                <input id={`${id}-doc-${key}`} type="file" accept={d.fileTypes.length ? d.fileTypes.map((x) => `.${x}`).join(",") : undefined}
                  aria-invalid={errs.length > 0 ? true : undefined} aria-describedby={errs.length > 0 ? errorId : undefined} className="block text-sm"
                  onChange={(e) => {
                    const file = e.target.files?.[0];
                    e.target.value = "";
                    if (file) void upload(p.serviceId, d.key, file);
                  }} />
                <ul className="space-y-1">
                  {(uploads[key] ?? []).map((u) => (
                    <li key={u.id} className="flex items-center gap-2 text-sm">
                      <span>{u.fileName}</span>
                      <button type="button" className={fieldButton} aria-label={t("removeFile", { name: u.fileName })}
                        onClick={() => {
                          void deleteRequest(`/v1/form-uploads/${u.id}`, csrfToken).catch(() => undefined);
                          setUploads((all) => ({ ...all, [key]: (all[key] ?? []).filter((x) => x.id !== u.id) }));
                        }}>{t("remove")}</button>
                    </li>
                  ))}
                </ul>
                {errs.length > 0 && <p id={errorId} className="text-sm text-bad">{errs.map((e) => (t.has(`documentErrors.${e.code}`) ? t(`documentErrors.${e.code}`) : (e.message ?? e.code))).join(" ")}</p>}
              </div>
            );
          })}
        </section>
      ))}
      <p role="status" aria-live="polite" className="text-sm">{uploadMessage}</p>
      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" className={primaryAction} disabled={status === "saving"}>{status === "saving" ? t("submitting") : t("submit")}</button>
        <p role="status" aria-live="polite" className="text-bad">{status === "saving" ? "" : messages[status]}</p>
      </div>
    </form>
  );
}
