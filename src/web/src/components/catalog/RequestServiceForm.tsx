"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { Localized } from "@/components/directory/common";
import { ApiError, deleteRequest, getJson, postForm, postJson } from "@/lib/client-api";
import { checkAnswers } from "@/lib/forms/check";
import type { AnswerError, Answers, ResolvedForm } from "@/lib/forms/types";
import type { TreeNode } from "@/lib/directory";
import type { Locale, Person } from "@/lib/incidents";
import { uploadFieldOf, type PublishedService, type ServiceRequest } from "@/lib/catalog";

interface Upload {
  id: string;
  fileName: string;
}

type Status = "idle" | "saving" | "invalid" | "failed" | "rateLimited" | "formChanged" | "suspended" | "prerequisite" | "external" | "forbidden" | "notOffered" | "offerGone";

/**
 * Requesting a service (task C2): an optional title and details, the service's form, the
 * required documents (uploaded with /form-uploads under document<Key>) and, for roles the
 * service allows, someone else to request it for. Server refusals show under their field.
 */
export function RequestServiceForm({ service, initialForm, csrfToken, locations, offer }: {
  service: PublishedService; initialForm: ResolvedForm | null; csrfToken: string; locations: TreeNode[];
  /** A proactive offer this request accepts (task C5): its answers fill in the form. */
  offer?: { id: string; answers: Answers } | null;
}) {
  const tOffers = useTranslations("catalog.offers");
  const t = useTranslations("catalog.request");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [form, setForm] = useState<ResolvedForm | null>(initialForm?.form ? initialForm : null);
  const [answers, setAnswers] = useState<Answers>(() =>
    Object.fromEntries(Object.entries(offer?.answers ?? {}).filter(([key]) => initialForm?.fields.some((f) => f.field.key === key))));
  const [formErrors, setFormErrors] = useState<AnswerError[]>([]);
  const [documentErrors, setDocumentErrors] = useState<AnswerError[]>([]);
  const [uploads, setUploads] = useState<Record<string, Upload[]>>({});
  const [uploadMessage, setUploadMessage] = useState("");
  const [status, setStatus] = useState<Status>("idle");
  const [onBehalf, setOnBehalf] = useState(false);
  const [requester, setRequester] = useState<Person | null>(null);
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<Person[]>([]);
  const [reload, setReload] = useState(0);

  // The form again when the requester changes (their profile defaults) or the form changed.
  const requesterId = onBehalf ? (requester?.id ?? null) : null;
  useEffect(() => {
    if (!requesterId && reload === 0) return;
    let cancelled = false;
    const params = new URLSearchParams({ language: locale });
    if (requesterId) params.set("requesterId", requesterId);
    getJson<ResolvedForm>(`/v1/catalog/published/${service.id}/request-form?${params}`)
      .then((resolved) => {
        if (cancelled) return;
        setForm(resolved.form ? resolved : null);
        setAnswers((current) => Object.fromEntries(Object.entries(current).filter(([key]) => resolved.fields.some((f) => f.field.key === key))));
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [requesterId, reload, service.id, locale]);

  async function upload(documentKey: string, file: File) {
    const data = new FormData();
    data.append("file", file, file.name);
    data.append("field", uploadFieldOf(documentKey));
    setUploadMessage(t("uploading"));
    try {
      const { body } = await postForm<Upload>("/v1/form-uploads", data, csrfToken);
      setUploads((u) => ({ ...u, [documentKey]: [...(u[documentKey] ?? []), { id: body.id, fileName: body.fileName }] }));
      setDocumentErrors((errors) => errors.filter((e) => e.field !== `documents.${documentKey}`));
      setUploadMessage(t("uploaded", { name: body.fileName }));
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      setUploadMessage(code && t.has(`uploadErrors.${code}`) ? t(`uploadErrors.${code}`) : t("uploadFailed"));
    }
  }

  async function removeUpload(documentKey: string, uploadId: string) {
    await deleteRequest(`/v1/form-uploads/${uploadId}`, csrfToken).catch(() => undefined);
    setUploads((u) => ({ ...u, [documentKey]: (u[documentKey] ?? []).filter((x) => x.id !== uploadId) }));
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (onBehalf && !requester) {
      setStatus("invalid");
      return;
    }
    const missingDocuments = service.documents.filter((d) => d.required && !(uploads[d.key]?.length)).map((d) => ({ field: `documents.${d.key}`, code: "document-required" }));
    let formBody: { formVersionId: string; formAnswers: Answers } | null = null;
    if (form?.form) {
      const checked = checkAnswers(form.fields.map((f) => f.field), answers, form.defaults);
      setFormErrors(checked.errors);
      if (checked.errors.length > 0 || missingDocuments.length > 0) {
        setDocumentErrors(missingDocuments);
        setStatus("invalid");
        return;
      }
      formBody = { formVersionId: form.form.versionId, formAnswers: checked.toSend };
    } else if (missingDocuments.length > 0) {
      setDocumentErrors(missingDocuments);
      setStatus("invalid");
      return;
    }
    setDocumentErrors([]);
    setStatus("saving");
    try {
      const created = await postJson<ServiceRequest>("/v1/service-requests", {
        serviceId: service.id,
        offerId: offer?.id,
        requesterId: requester && onBehalf ? requester.id : undefined,
        title: title.trim() || undefined,
        description: description.trim() || undefined,
        documents: Object.fromEntries(Object.entries(uploads).filter(([, list]) => list.length > 0).map(([key, list]) => [key, list.map((u) => u.id)])),
        ...formBody,
      }, csrfToken);
      router.push(onBehalf ? `/catalog/${service.id}?requested=${encodeURIComponent(created.number)}` : `/my-requests/${created.id}`);
    } catch (error) {
      if (!(error instanceof ApiError)) {
        setStatus("failed");
        return;
      }
      const answerErrors = error.details.formErrors as AnswerError[] | undefined;
      const docErrors = error.details.documentErrors as AnswerError[] | undefined;
      if (answerErrors?.length || docErrors?.length) {
        setFormErrors(answerErrors ?? []);
        setDocumentErrors(docErrors ?? []);
        setStatus("invalid");
        return;
      }
      const errors = error.details.errors as Record<string, unknown> | undefined;
      setStatus(
        error.rateLimited ? "rateLimited"
          : error.code === "form-changed" ? "formChanged"
            : error.code === "service-suspended" ? "suspended"
              : error.code === "offer-unavailable" ? "offerGone"
              : error.code === "prerequisite-missing" ? "prerequisite"
                : error.code === "service-external" ? "external"
                  : error.status === 403 ? "forbidden"
                    : errors && "requesterId" in errors ? "notOffered"
                      : error.status === 400 ? "invalid" : "failed",
      );
      if (error.code === "form-changed") setReload((n) => n + 1);
    }
  }

  const messages: Record<Status, string> = {
    idle: "", saving: t("submitting"), invalid: t("invalid"), failed: t("failed"), rateLimited: tCommon("rateLimited"), formChanged: t("formChanged"),
    suspended: t("errors.service-suspended"), prerequisite: t("errors.prerequisite-missing"), external: t("errors.service-external"),
    forbidden: t("errors.forbidden"), notOffered: t("errors.not-offered"), offerGone: t("bundle.errors.offer-unavailable"),
  };

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-5" noValidate data-testid="request-service">
      {offer && <p role="note" className="rounded-md border border-line p-2">{tOffers("filling")}</p>}
      {service.mayRequestOnBehalf && !offer && (
        <fieldset className="space-y-2 rounded-lg border border-line p-4" data-testid="on-behalf">
          <legend className="font-semibold">{t("forWhom")}</legend>
          <label className="flex items-center gap-2">
            <input type="radio" className="size-6" name={`${id}-who`} checked={!onBehalf} onChange={() => setOnBehalf(false)} />
            {t("forMe")}
          </label>
          <label className="flex items-center gap-2">
            <input type="radio" className="size-6" name={`${id}-who`} checked={onBehalf} onChange={() => setOnBehalf(true)} />
            {t("forSomeoneElse")}
          </label>
          {onBehalf && (
            <div className="space-y-2">
              {requester && <p>{t("requestingFor", { name: requester.displayName ?? requester.id })}</p>}
              <div className="flex flex-wrap items-end gap-2">
                <div className="space-y-1">
                  <label htmlFor={`${id}-person`} className="block text-sm">{t("findPerson")}</label>
                  <input id={`${id}-person`} type="search" value={query} onChange={(e) => setQuery(e.target.value)} className={fieldInput} />
                </div>
                <button type="button" className={fieldButton} disabled={query.trim().length < 2}
                  onClick={() => void getJson<Person[]>(`/v1/forms/people?${new URLSearchParams({ q: query.trim() })}`).then(setFound).catch(() => setFound([]))}>{t("find")}</button>
              </div>
              {found.length > 0 && (
                <ul className="space-y-1">
                  {found.map((p) => (
                    <li key={p.id}>
                      <button type="button" className={fieldButton} onClick={() => { setRequester(p); setFound([]); setQuery(""); }}>
                        {t("choosePerson", { name: p.displayName ?? p.id })}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </fieldset>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-title`} className="block font-semibold">{t("title")}</label>
        <input id={`${id}-title`} value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} dir="auto" aria-describedby={`${id}-title-hint`} className={fieldInput} />
        <p id={`${id}-title-hint`} className="text-sm text-ink-muted">{t("titleHint")}</p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-description`} className="block font-semibold">{t("description")}</label>
        <textarea id={`${id}-description`} value={description} onChange={(e) => setDescription(e.target.value)} rows={4} maxLength={20000} dir="auto" className={fieldInput} />
      </div>
      {form?.form && (
        <section aria-labelledby={`${id}-form`} className="space-y-3 rounded-lg border border-line p-4">
          <h2 id={`${id}-form`} className="text-lg font-semibold"><Localized text={form.form.name} locale={locale} /></h2>
          <FormRenderer fields={form.fields} answers={answers} onChange={setAnswers} defaults={form.defaults} references={form.references}
            errors={formErrors} contentLocale={locale} locations={locations} csrfToken={csrfToken} idPrefix={`${id}-answers`} />
        </section>
      )}
      {service.documents.length > 0 && (
        <section aria-labelledby={`${id}-documents`} className="space-y-3 rounded-lg border border-line p-4">
          <h2 id={`${id}-documents`} className="text-lg font-semibold">{t("documents")}</h2>
          {service.documents.map((d) => {
            const errors = documentErrors.filter((e) => e.field === `documents.${d.key}`);
            const errorId = `${id}-doc-${d.key}-error`;
            return (
              <div key={d.key} className="space-y-1" data-document={d.key}>
                <label htmlFor={`${id}-doc-${d.key}`} className="block font-semibold">
                  <Localized text={d.name} locale={locale} /> <span className="text-sm font-normal text-ink-muted">({d.required ? t("mandatory") : t("optional")})</span>
                </label>
                <input id={`${id}-doc-${d.key}`} type="file" accept={d.fileTypes.length ? d.fileTypes.map((x) => `.${x}`).join(",") : undefined}
                  aria-invalid={errors.length > 0 ? true : undefined} aria-describedby={errors.length > 0 ? errorId : undefined}
                  onChange={(e) => {
                    const file = e.target.files?.[0];
                    e.target.value = "";
                    if (file) void upload(d.key, file);
                  }} className="block text-sm" />
                <ul className="space-y-1">
                  {(uploads[d.key] ?? []).map((u) => (
                    <li key={u.id} className="flex items-center gap-2 text-sm">
                      <span>{u.fileName}</span>
                      <button type="button" className={fieldButton} onClick={() => void removeUpload(d.key, u.id)} aria-label={t("removeFile", { name: u.fileName })}>{t("remove")}</button>
                    </li>
                  ))}
                </ul>
                {errors.length > 0 && (
                  <p id={errorId} className="text-sm text-bad">{errors.map((e) => (t.has(`documentErrors.${e.code}`) ? t(`documentErrors.${e.code}`) : (e.message ?? e.code))).join(" ")}</p>
                )}
              </div>
            );
          })}
          <p role="status" aria-live="polite" className="text-sm">{uploadMessage}</p>
        </section>
      )}
      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" className={primaryAction} disabled={status === "saving"}>{status === "saving" ? t("submitting") : t("submit")}</button>
        <p role="status" aria-live="polite" className="text-bad">{status === "saving" ? "" : messages[status]}</p>
      </div>
    </form>
  );
}
