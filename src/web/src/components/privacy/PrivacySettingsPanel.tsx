"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import type { Locale } from "@/i18n/routing";
import { ApiError, deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import {
  parseDays, ticketTypes,
  type ConsentPurpose, type PrivacyNotice, type PrivacySettings, type RetentionPreview, type RetentionTicketType,
} from "@/lib/privacy";

type Status = "idle" | "saving" | "saved" | "failed" | "invalid";

function StatusLine({ status }: { status: Status }) {
  const t = useTranslations("privacy.settings");
  return (
    <p role="status" aria-live="polite" className={status === "failed" || status === "invalid" ? "text-bad" : ""}>
      {status === "idle" ? "" : t(`status.${status}`)}
    </p>
  );
}

/** Settings → Privacy, the privacy notice: versions, the draft in English and Arabic side by side with a preview, publishing. */
export function NoticeEditor({ initial, display, csrfToken }: { initial: PrivacyNotice[]; display: DateDisplayOptions; csrfToken: string }) {
  const t = useTranslations("privacy.settings.notice");
  const id = useId();
  const [notices, setNotices] = useState(initial);
  const draft = notices.find((n) => n.state === "Draft") ?? null;
  const published = notices.find((n) => n.state === "Published") ?? null;
  const start = draft ?? published;
  const [text, setText] = useState({
    titleEn: start?.title.en ?? "", titleAr: start?.title.ar ?? "", bodyEn: start?.body.en ?? "", bodyAr: start?.body.ar ?? "",
  });
  const [preview, setPreview] = useState(false);
  const [status, setStatus] = useState<Status>("idle");
  const [error, setError] = useState<string | null>(null);

  const reload = async () => setNotices(await getJson<PrivacyNotice[]>("/v1/privacy/notices"));

  async function save(event?: FormEvent) {
    event?.preventDefault();
    setStatus("saving");
    setError(null);
    try {
      await putJson("/v1/privacy/notices/draft", { title: { en: text.titleEn, ar: text.titleAr }, body: { en: text.bodyEn, ar: text.bodyAr } }, csrfToken);
      await reload();
      setStatus("saved");
      return true;
    } catch {
      setStatus("failed");
      return false;
    }
  }

  async function publish() {
    if (!(await save())) return;
    setStatus("saving");
    try {
      await postJson("/v1/privacy/notices/draft/publish", {}, csrfToken);
      await reload();
      setStatus("saved");
    } catch (e) {
      setStatus("invalid");
      setError(e instanceof ApiError && e.status === 400 ? t("bothLanguages") : null);
    }
  }

  async function discard() {
    setStatus("saving");
    try {
      await deleteRequest("/v1/privacy/notices/draft", csrfToken);
      await reload();
      setText({ titleEn: published?.title.en ?? "", titleAr: published?.title.ar ?? "", bodyEn: published?.body.en ?? "", bodyAr: published?.body.ar ?? "" });
      setStatus("idle");
    } catch {
      setStatus("failed");
    }
  }

  const column = (lang: "en" | "ar") => {
    const titleKey = lang === "en" ? "titleEn" : "titleAr";
    const bodyKey = lang === "en" ? "bodyEn" : "bodyAr";
    return (
      <div className="space-y-2" lang={lang} dir={lang === "ar" ? "rtl" : "ltr"}>
        <label htmlFor={`${id}-${titleKey}`} className="block font-semibold">{t(`title_${lang}`)}</label>
        <input id={`${id}-${titleKey}`} className={fieldInput} maxLength={200} value={text[titleKey]} onChange={(e) => setText({ ...text, [titleKey]: e.target.value })} />
        <label htmlFor={`${id}-${bodyKey}`} className="block font-semibold">{t(`body_${lang}`)}</label>
        {preview
          ? <div className="rounded-md border border-line p-3"><MarkdownView markdown={text[bodyKey]} articleId={null} lang={lang} dir={lang === "ar" ? "rtl" : "ltr"} /></div>
          : <textarea id={`${id}-${bodyKey}`} className={`${fieldInput} font-mono`} rows={14} maxLength={50000} value={text[bodyKey]} onChange={(e) => setText({ ...text, [bodyKey]: e.target.value })} />}
      </div>
    );
  };

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="privacy-notice-editor">
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h3>
      <p className="max-w-2xl text-ink-muted">{t("intro")}</p>
      <p data-testid="privacy-notice-current">
        {published
          ? t("published", { version: published.version, date: published.publishedAt ? formatDateTime(new Date(published.publishedAt), display) : "" })
          : t("nonePublished")}
        {draft && ` ${t("draft", { version: draft.version })}`}
      </p>
      <form onSubmit={save} className="space-y-3" noValidate>
        <div className="grid gap-4 md:grid-cols-2">
          {column("en")}
          {column("ar")}
        </div>
        <p className="text-sm text-ink-muted">{t("markdownHint")}</p>
        <div className="flex flex-wrap gap-2">
          <button type="submit" className={primaryAction} disabled={status === "saving"}>{t("saveDraft")}</button>
          <button type="button" className={fieldButton} onClick={() => setPreview(!preview)} aria-pressed={preview}>{t(preview ? "edit" : "preview")}</button>
          <button type="button" className={fieldButton} onClick={() => void publish()} disabled={status === "saving"}>{t("publish")}</button>
          {draft && <button type="button" className={fieldButton} onClick={() => void discard()}>{t("discard")}</button>}
        </div>
        {error && <p role="alert" className="text-bad">{error}</p>}
        <StatusLine status={status} />
      </form>
      {notices.length > 0 && (
        <table className="w-full text-start text-sm">
          <caption className="text-start font-semibold">{t("versions")}</caption>
          <thead>
            <tr>
              <th scope="col" className="border-b border-line py-1 text-start">{t("version")}</th>
              <th scope="col" className="border-b border-line py-1 text-start">{t("state")}</th>
              <th scope="col" className="border-b border-line py-1 text-start">{t("publishedAt")}</th>
            </tr>
          </thead>
          <tbody>
            {notices.map((n) => (
              <tr key={n.id}>
                <td className="border-b border-line py-1">{n.version}</td>
                <td className="border-b border-line py-1">{t(`states.${n.state}`)}</td>
                <td className="border-b border-line py-1">{n.publishedAt ? formatDateTime(new Date(n.publishedAt), display) : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

interface PurposeDraft {
  id: string | null;
  key: string;
  nameEn: string;
  nameAr: string;
  descriptionEn: string;
  descriptionAr: string;
  active: boolean;
}

const emptyPurpose: PurposeDraft = { id: null, key: "", nameEn: "", nameAr: "", descriptionEn: "", descriptionAr: "", active: true };

/** Settings → Privacy, optional consent purposes: add, change wording, ask or stop asking. */
export function PurposesEditor({ initial, csrfToken }: { initial: ConsentPurpose[]; csrfToken: string }) {
  const t = useTranslations("privacy.settings.purposes");
  const locale = useLocale() as Locale;
  const id = useId();
  const [purposes, setPurposes] = useState(initial);
  const [editing, setEditing] = useState<PurposeDraft | null>(null);
  const [status, setStatus] = useState<Status>("idle");
  const [problem, setProblem] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!editing) return;
    setStatus("saving");
    setProblem(null);
    const body = {
      key: editing.id ? undefined : editing.key.trim(),
      name: { en: editing.nameEn, ar: editing.nameAr },
      description: { en: editing.descriptionEn, ar: editing.descriptionAr },
      active: editing.active,
    };
    try {
      if (editing.id) await putJson(`/v1/privacy/purposes/${editing.id}`, body, csrfToken);
      else await postJson("/v1/privacy/purposes", body, csrfToken);
      setPurposes(await getJson<ConsentPurpose[]>("/v1/privacy/purposes"));
      setEditing(null);
      setStatus("saved");
    } catch (e) {
      const errors = e instanceof ApiError ? (e.details.errors as Record<string, string[]> | undefined) : undefined;
      setProblem(errors ? Object.keys(errors).map((k) => t(`problems.${["key", "name", "description"].includes(k) ? k : "other"}`)).join(" ") : null);
      setStatus(e instanceof ApiError && e.status === 400 ? "invalid" : "failed");
    }
  }

  const field = (key: keyof PurposeDraft, label: string, lang?: "en" | "ar", area = false) => (
    <div className="space-y-1" lang={lang} dir={lang === "ar" ? "rtl" : lang ? "ltr" : undefined}>
      <label htmlFor={`${id}-${key}`} className="block font-semibold">{label}</label>
      {area
        ? <textarea id={`${id}-${key}`} rows={3} maxLength={2000} className={fieldInput} value={String(editing![key] ?? "")} onChange={(e) => setEditing({ ...editing!, [key]: e.target.value })} />
        : <input id={`${id}-${key}`} maxLength={key === "key" ? 50 : 150} dir={key === "key" ? "ltr" : undefined} className={fieldInput}
            value={String(editing![key] ?? "")} onChange={(e) => setEditing({ ...editing!, [key]: e.target.value })} />}
    </div>
  );

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="privacy-purposes">
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h3>
      <p className="max-w-2xl text-ink-muted">{t("intro")}</p>
      {purposes.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2">
          {purposes.map((p) => (
            <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-line p-3">
              <span>
                <span className="font-semibold">{textOf(p.name, locale)}</span>
                <span className="ms-2 font-mono text-sm" dir="ltr">{p.key}</span>
                {!p.active && <span className="ms-2 text-sm text-ink-muted">{t("inactive")}</span>}
              </span>
              <button type="button" className={fieldButton} onClick={() => setEditing({
                id: p.id, key: p.key, nameEn: p.name.en ?? "", nameAr: p.name.ar ?? "", descriptionEn: p.description.en ?? "", descriptionAr: p.description.ar ?? "", active: p.active,
              })}>{t("edit", { name: textOf(p.name, locale) })}</button>
            </li>
          ))}
        </ul>
      )}
      {editing ? (
        <form onSubmit={submit} className="space-y-3 rounded-lg border border-line p-4" noValidate>
          {!editing.id && field("key", t("key"))}
          {!editing.id && <p className="text-sm text-ink-muted">{t("keyHint")}</p>}
          <div className="grid gap-3 md:grid-cols-2">
            {field("nameEn", t("name_en"), "en")}
            {field("nameAr", t("name_ar"), "ar")}
            {field("descriptionEn", t("description_en"), "en", true)}
            {field("descriptionAr", t("description_ar"), "ar", true)}
          </div>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={editing.active} onChange={(e) => setEditing({ ...editing, active: e.target.checked })} className="size-4" />
            {t("active")}
          </label>
          {problem && <p role="alert" className="text-bad">{problem}</p>}
          <div className="flex gap-2">
            <button type="submit" className={primaryAction} disabled={status === "saving"}>{t("save")}</button>
            <button type="button" className={fieldButton} onClick={() => setEditing(null)}>{t("cancel")}</button>
          </div>
        </form>
      ) : (
        <button type="button" className={fieldButton} onClick={() => setEditing(emptyPurpose)}>{t("add")}</button>
      )}
      <StatusLine status={status} />
    </section>
  );
}

/** Settings → Privacy, retention periods and the time to answer requests, with a preview of what they would anonymise now. */
export function RetentionEditor({ initial, csrfToken }: { initial: PrivacySettings; csrfToken: string }) {
  const t = useTranslations("privacy.settings.retention");
  const tTypes = useTranslations("privacy.ticketTypes");
  const id = useId();
  const [days, setDays] = useState<Record<RetentionTicketType, string>>(
    Object.fromEntries(ticketTypes.map((type) => [type, initial.ticketRetentionDays[type]?.toString() ?? ""])) as Record<RetentionTicketType, string>);
  const [accounts, setAccounts] = useState(initial.inactiveAccountDays?.toString() ?? "");
  const [due, setDue] = useState(initial.requestDueDays.toString());
  const [preview, setPreview] = useState<RetentionPreview | null>(null);
  const [status, setStatus] = useState<Status>("idle");

  const parsed = () => {
    const tickets: Partial<Record<RetentionTicketType, number>> = {};
    for (const type of ticketTypes) {
      const value = parseDays(days[type]);
      if (value === undefined) return null;
      if (value !== null) tickets[type] = value;
    }
    const inactive = parseDays(accounts);
    const dueDays = Number(due);
    if (inactive === undefined || !/^\d+$/.test(due.trim()) || dueDays < 1 || dueDays > 90) return null;
    return { tickets, inactive, dueDays };
  };

  async function runPreview() {
    const values = parsed();
    if (!values) {
      setStatus("invalid");
      return;
    }
    const query = new URLSearchParams();
    for (const [type, value] of Object.entries(values.tickets)) query.set(type.charAt(0).toLowerCase() + type.slice(1), String(value));
    if (values.inactive !== null) query.set("inactiveAccounts", String(values.inactive));
    try {
      setPreview(await getJson<RetentionPreview>(`/v1/settings/privacy/retention-preview?${query}`));
      setStatus("idle");
    } catch {
      setStatus("failed");
    }
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const values = parsed();
    if (!values) {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    try {
      await putJson("/v1/settings/privacy", {
        requestDueDays: values.dueDays,
        ticketRetentionDays: values.tickets,
        inactiveAccountDays: values.inactive ?? undefined,
        clearInactiveAccountDays: values.inactive === null,
      }, csrfToken);
      setStatus("saved");
    } catch (e) {
      setStatus(e instanceof ApiError && e.status === 400 ? "invalid" : "failed");
    }
  }

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="privacy-retention">
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h3>
      <p className="max-w-2xl text-ink-muted">{t("intro")}</p>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <fieldset className="grid gap-3 sm:grid-cols-2">
          <legend className="font-semibold">{t("tickets")}</legend>
          {ticketTypes.map((type) => (
            <div key={type} className="space-y-1">
              <label htmlFor={`${id}-${type}`} className="block">{tTypes(type)}</label>
              <input id={`${id}-${type}`} inputMode="numeric" dir="ltr" className={fieldInput} value={days[type]} placeholder={t("keep")}
                aria-describedby={`${id}-hint`} onChange={(e) => setDays({ ...days, [type]: e.target.value })} />
            </div>
          ))}
        </fieldset>
        <p id={`${id}-hint`} className="text-sm text-ink-muted">{t("hint")}</p>
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-accounts`} className="block font-semibold">{t("accounts")}</label>
            <input id={`${id}-accounts`} inputMode="numeric" dir="ltr" className={fieldInput} value={accounts} placeholder={t("keep")} onChange={(e) => setAccounts(e.target.value)}
              aria-describedby={`${id}-accounts-hint`} />
            <p id={`${id}-accounts-hint`} className="text-sm text-ink-muted">{t("accountsHint")}</p>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-due`} className="block font-semibold">{t("due")}</label>
            <input id={`${id}-due`} inputMode="numeric" dir="ltr" className={fieldInput} value={due} onChange={(e) => setDue(e.target.value)} aria-describedby={`${id}-due-hint`} />
            <p id={`${id}-due-hint`} className="text-sm text-ink-muted">{t("dueHint")}</p>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" className={fieldButton} onClick={() => void runPreview()}>{t("preview")}</button>
          <button type="submit" className={primaryAction} disabled={status === "saving"}>{t("save")}</button>
        </div>
        {preview && (
          <div role="status" className="rounded-md border border-line p-3" data-testid="retention-preview">
            <p className="font-semibold">{t("previewHeading")}</p>
            <ul className="list-disc ps-6">
              {Object.entries(preview.tickets).map(([type, count]) => <li key={type}>{t("previewTickets", { type: tTypes(type as RetentionTicketType), count: count ?? 0 })}</li>)}
              <li>{t("previewAccounts", { count: preview.accounts })}</li>
            </ul>
          </div>
        )}
        <StatusLine status={status} />
      </form>
    </section>
  );
}
