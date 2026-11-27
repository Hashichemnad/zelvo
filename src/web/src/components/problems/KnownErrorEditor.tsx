"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import { postJson, putJson } from "@/lib/client-api";
import type { KnownError } from "@/lib/problems";
import { ProblemStatusLine, useSaver } from "./common";

/**
 * A problem's known error (task H1): title, symptoms and workaround in English and Arabic side by
 * side (Arabic right to left). Problem managers retire it, offer it again and publish it in the
 * portal as an external knowledge article draft, which still needs the four-eyes review.
 */
export function KnownErrorEditor({ problemId, knownError, canEdit, canManage, canWriteArticles, csrfToken }: {
  problemId: string;
  knownError: KnownError | null;
  canEdit: boolean;
  canManage: boolean;
  canWriteArticles: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("problems.knownError");
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [editing, setEditing] = useState(false);
  const [fields, setFields] = useState({
    titleEn: knownError?.title.en ?? "", titleAr: knownError?.title.ar ?? "",
    symptomsEn: knownError?.symptoms.en ?? "", symptomsAr: knownError?.symptoms.ar ?? "",
    workaroundEn: knownError?.workaround.en ?? "", workaroundAr: knownError?.workaround.ar ?? "",
  });
  const valid = (fields.titleEn.trim() || fields.titleAr.trim()) && (fields.workaroundEn.trim() || fields.workaroundAr.trim());
  const both = (en: string, ar: string) => ({ en: en.trim() || null, ar: ar.trim() || null });

  function save() {
    void run(async () => {
      await putJson(`/v1/problems/${problemId}/known-error`, {
        title: both(fields.titleEn, fields.titleAr),
        symptoms: both(fields.symptomsEn, fields.symptomsAr),
        workaround: both(fields.workaroundEn, fields.workaroundAr),
      }, csrfToken, knownError ? { "If-Match": knownError.etag } : {});
      setEditing(false);
      router.refresh();
    });
  }

  const field = (key: keyof typeof fields, label: string, lang: "en" | "ar", long: boolean) => (
    <label className="space-y-1 text-sm">
      <span className="block font-semibold">{label}</span>
      {long ? (
        <textarea value={fields[key]} rows={4} maxLength={10000} lang={lang} dir={lang === "ar" ? "rtl" : "ltr"} className={inputClass}
          onChange={(e) => setFields({ ...fields, [key]: e.target.value })} />
      ) : (
        <input value={fields[key]} maxLength={300} lang={lang} dir={lang === "ar" ? "rtl" : "ltr"} className={inputClass}
          onChange={(e) => setFields({ ...fields, [key]: e.target.value })} />
      )}
    </label>
  );

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="known-error">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">
        {t("heading")} {knownError && <span className="font-mono text-base text-ink-muted" dir="ltr">{knownError.number}</span>}
      </h2>
      {!knownError && !editing && <p>{t("none")}</p>}
      {knownError && !editing && (
        <div className="space-y-3">
          {knownError.status === "Retired" && <p className="font-semibold text-ink-muted">{t("retired")}</p>}
          <div className="grid gap-3 sm:grid-cols-2">
            {(["en", "ar"] as const).map((lang) => (knownError.title[lang] || knownError.workaround[lang]) && (
              <div key={lang} lang={lang} dir={lang === "ar" ? "rtl" : "ltr"} className="space-y-1">
                <p className="font-semibold">{knownError.title[lang]}</p>
                {knownError.symptoms[lang] && <p className="whitespace-pre-wrap text-sm">{knownError.symptoms[lang]}</p>}
                <p className="whitespace-pre-wrap">{knownError.workaround[lang]}</p>
              </div>
            ))}
          </div>
          {knownError.articleId && (
            <p>
              <Link href={`/knowledge/${knownError.articleId}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                {t("articleLink", { number: knownError.articleNumber ?? "", state: knownError.articleState ? t(`articleStates.${knownError.articleState}`) : "" })}
              </Link>
            </p>
          )}
        </div>
      )}
      {editing && (
        <div className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            {field("titleEn", t("titleEn"), "en", false)}
            {field("titleAr", t("titleAr"), "ar", false)}
            {field("symptomsEn", t("symptomsEn"), "en", true)}
            {field("symptomsAr", t("symptomsAr"), "ar", true)}
            {field("workaroundEn", t("workaroundEn"), "en", true)}
            {field("workaroundAr", t("workaroundAr"), "ar", true)}
          </div>
          {!valid && <p className="text-sm text-ink-muted">{t("needsOneLanguage")}</p>}
          <button type="button" className={buttonClass} disabled={busy || !valid} onClick={save}>{t("save")}</button>
        </div>
      )}
      <div className="flex flex-wrap gap-2">
        {canEdit && !editing && (
          <button type="button" className={secondaryButtonClass} onClick={() => setEditing(true)}>{knownError ? t("edit") : t("record")}</button>
        )}
        {canManage && knownError && (
          <button type="button" className={secondaryButtonClass} disabled={busy}
            onClick={() => void run(async () => {
              await postJson(`/v1/known-errors/${knownError.id}/${knownError.status === "Active" ? "retire" : "activate"}`, {}, csrfToken);
              router.refresh();
            })}>
            {knownError.status === "Active" ? t("retire") : t("activate")}
          </button>
        )}
        {canManage && canWriteArticles && knownError && !knownError.articleId && (
          <button type="button" className={secondaryButtonClass} disabled={busy} aria-describedby={`${id}-article`}
            onClick={() => void run(async () => {
              await postJson(`/v1/known-errors/${knownError.id}/article`, {}, csrfToken);
              router.refresh();
            })}>
            {t("article")}
          </button>
        )}
      </div>
      {canManage && canWriteArticles && knownError && !knownError.articleId && <p id={`${id}-article`} className="text-sm text-ink-muted">{t("articleHint")}</p>}
      <ProblemStatusLine status={status} />
    </section>
  );
}
