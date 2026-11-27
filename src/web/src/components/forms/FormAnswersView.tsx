"use client";

import { useLocale, useTranslations } from "next-intl";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import { textOf } from "@/lib/directory";
import { isFallback, textIn, type FormField, type JsonValue, type TicketFormView } from "@/lib/forms/types";

type Locale = "en" | "ar";

/**
 * The form answers given with a ticket (task C1), with the fields as they were when the form was
 * filled. Files link to the ticket's attachments, which are downloadable once scanned clean.
 */
export function FormAnswersView({ view, attachmentBase }: { view: TicketFormView; attachmentBase: string }) {
  const t = useTranslations("forms.answers");
  const locale = useLocale() as Locale;
  if (!view.form || !view.fields) return null;
  const answers = view.answers ?? {};
  const shown = view.fields.filter(({ field }) => field.key in answers);
  return (
    <section aria-labelledby="form-answers-heading" className="space-y-3 rounded-lg border border-line p-4">
      <h2 id="form-answers-heading" className="text-lg font-semibold">
        {t("title", { form: textOf(view.form.name, locale) })}
      </h2>
      {shown.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <dl className="grid gap-x-4 gap-y-2 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]">
          {shown.map(({ field }) => (
            <div key={field.key} className="contents">
              <dt className="font-semibold">
                <span lang={isFallback(field.label, locale) ? (locale === "ar" ? "en" : "ar") : undefined}>{textIn(field.label, locale)}</span>
              </dt>
              <dd className="min-w-0">
                <Answer field={field} value={answers[field.key] ?? null} view={view} attachmentBase={attachmentBase} />
              </dd>
            </div>
          ))}
        </dl>
      )}
      <p className="text-sm text-ink-muted">{t("version", { number: view.form.versionNumber })}</p>
    </section>
  );
}

function Answer({ field, value, view, attachmentBase }: { field: FormField; value: JsonValue; view: TicketFormView; attachmentBase: string }) {
  const t = useTranslations("forms.answers");
  const locale = useLocale() as Locale;
  const refs = view.references;
  const option = (v: string) => textIn(field.options?.find((o) => o.value === v)?.label, locale) || v;
  const file = (id: string) => {
    const f = refs?.files[id];
    if (!f) return <span>{t("fileGone")}</span>;
    return f.scanStatus === "Clean" || f.scanStatus === "NotScanned" ? (
      <a className="text-brand underline" href={`${attachmentBase}/${f.id}/content`}>{f.fileName}</a>
    ) : (
      <span>{f.fileName} ({t(`scan.${f.scanStatus}`)})</span>
    );
  };
  switch (field.type) {
    case "Checkbox":
      return <>{value === true ? t("yes") : t("no")}</>;
    case "Dropdown":
    case "Radio":
      return <>{Array.isArray(value) ? value.map((v) => option(String(v))).join(", ") : option(String(value))}</>;
    case "User":
      return <>{refs?.users[String(value)] ?? String(value)}</>;
    case "Location":
      return <>{refs?.locations[String(value)] ? textOf(refs.locations[String(value)], locale) : String(value)}</>;
    case "ConfigurationItem": {
      // Task G1: number and name of each item picked.
      const ids = (Array.isArray(value) ? value : [value]).map(String);
      return (
        <ul className="space-y-1">
          {ids.map((id) => <li key={id} dir="auto">{refs?.configurationItems?.[id] ?? t("itemGone")}</li>)}
        </ul>
      );
    }
    case "File":
      return (
        <ul className="space-y-1">
          {(Array.isArray(value) ? value : []).map((id) => <li key={String(id)}>{file(String(id))}</li>)}
        </ul>
      );
    case "Signature": {
      const s = value as Record<string, JsonValue>;
      return typeof s?.typedName === "string" ? <>{t("typedSignature", { name: s.typedName })}</> : s?.attachmentId ? file(String(s.attachmentId)) : null;
    }
    case "RichText":
      return <MarkdownView markdown={String(value)} articleId={null} />;
    case "Table": {
      const rows = Array.isArray(value) ? (value as Record<string, JsonValue>[]) : [];
      return (
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-sm">
            <caption className="sr-only">{textIn(field.label, locale)}</caption>
            <thead>
              <tr>{(field.columns ?? []).map((c) => <th key={c.key} scope="col" className="border border-line p-1 text-start">{textIn(c.label, locale)}</th>)}</tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i}>
                  {(field.columns ?? []).map((c) => (
                    <td key={c.key} className="border border-line p-1">
                      {row[c.key] === undefined || row[c.key] === null ? "" : c.type === "Checkbox" ? (row[c.key] ? t("yes") : t("no"))
                        : c.type === "Dropdown" ? textIn(c.options?.find((o) => o.value === row[c.key])?.label, locale) || String(row[c.key]) : String(row[c.key])}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      );
    }
    default:
      return <span dir="auto" className="whitespace-pre-wrap break-words">{String(value)}</span>;
  }
}
