"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { Link } from "@/i18n/navigation";
import type { Locale } from "@/i18n/routing";
import { ApiError, getJson, postJson, putJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { exportUrl, isOpen, requestTypes, type MyPrivacy, type PrivacyRequest, type RequestType } from "@/lib/privacy";

/**
 * Account → Privacy (GOV-5, task F5): the published notice and its acceptance, the optional
 * purposes the person agrees to or not, their consent history, a copy of their data, and their
 * data subject requests.
 */
export function MyPrivacyPanel({ initial, initialRequests, display, csrfToken }: {
  initial: MyPrivacy;
  initialRequests: PrivacyRequest[];
  display: DateDisplayOptions;
  csrfToken: string;
}) {
  const t = useTranslations("privacy.account");
  const tTypes = useTranslations("privacy.requestTypes");
  const tStates = useTranslations("privacy.requestStates");
  const locale = useLocale() as Locale;
  const id = useId();
  const [privacy, setPrivacy] = useState(initial);
  const [requests, setRequests] = useState(initialRequests);
  const [type, setType] = useState<RequestType>("Access");
  const [details, setDetails] = useState("");
  const [message, setMessage] = useState<{ text: string; bad: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const when = (iso: string) => formatDateTime(new Date(iso), display);

  async function run(action: () => Promise<void>, done?: string) {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      if (done) setMessage({ text: done, bad: false });
    } catch (e) {
      setMessage({ text: e instanceof ApiError && e.code === "too-many-requests-open" ? t("tooMany") : t("failed"), bad: true });
    } finally {
      setBusy(false);
    }
  }

  const accept = () => run(async () => {
    setPrivacy(await postJson<MyPrivacy>("/v1/me/privacy/notice-acceptance", { noticeId: privacy.notice!.id }, csrfToken));
  }, t("accepted"));

  const choose = (purposeId: string, granted: boolean) => run(async () => {
    setPrivacy(await putJson<MyPrivacy>(`/v1/me/privacy/purposes/${purposeId}`, { granted }, csrfToken));
  }, t("saved"));

  async function ask(event: FormEvent) {
    event.preventDefault();
    await run(async () => {
      await postJson("/v1/me/privacy/requests", { type, details: details.trim() || null }, csrfToken);
      setRequests(await getJson<PrivacyRequest[]>("/v1/me/privacy/requests"));
      setDetails("");
    }, t("sent"));
  }

  const withdraw = (requestId: string) => run(async () => {
    await postJson(`/v1/me/privacy/requests/${requestId}/withdraw`, {}, csrfToken);
    setRequests(await getJson<PrivacyRequest[]>("/v1/me/privacy/requests"));
  });

  return (
    <div className="space-y-8" data-testid="my-privacy">
      <section aria-labelledby={`${id}-notice`} className="space-y-3">
        <h2 id={`${id}-notice`} className="text-xl font-bold">{t("noticeHeading")}</h2>
        {!privacy.notice ? <p>{t("noNotice")}</p> : (
          <>
            <p>
              <Link href="/privacy" className="font-semibold text-brand underline">{textOf(privacy.notice.title, locale)}</Link>
              {" "}{t("version", { version: privacy.notice.version })}
            </p>
            {privacy.noticeAccepted
              ? <p data-testid="notice-accepted">{privacy.noticeAcceptedAt ? t("acceptedOn", { date: when(privacy.noticeAcceptedAt) }) : t("acceptedAlready")}</p>
              : (
                <div className="space-y-2 rounded-md border-s-4 border-brand bg-surface-muted p-3">
                  <p>{t("pleaseAccept")}</p>
                  <button type="button" className={primaryAction} onClick={() => void accept()} disabled={busy}>{t("accept")}</button>
                </div>
              )}
          </>
        )}
      </section>

      {privacy.purposes.length > 0 && (
        <section aria-labelledby={`${id}-purposes`} className="space-y-3">
          <h2 id={`${id}-purposes`} className="text-xl font-bold">{t("purposesHeading")}</h2>
          <p className="max-w-2xl text-ink-muted">{t("purposesIntro")}</p>
          <ul className="space-y-3">
            {privacy.purposes.map((p) => (
              <li key={p.id} className="rounded-md border border-line p-3">
                <label className="flex items-start gap-2">
                  <input type="checkbox" className="mt-1 size-4" checked={p.granted} disabled={busy} onChange={(e) => void choose(p.id, e.target.checked)}
                    aria-describedby={`${id}-${p.id}-text`} />
                  <span className="font-semibold">{textOf(p.name, locale)}</span>
                </label>
                <p id={`${id}-${p.id}-text`} className="ms-6 text-sm text-ink-muted">{textOf(p.description, locale)}</p>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section aria-labelledby={`${id}-copy`} className="space-y-3">
        <h2 id={`${id}-copy`} className="text-xl font-bold">{t("copyHeading")}</h2>
        <p className="max-w-2xl text-ink-muted">{t("copyIntro")}</p>
        <a href={exportUrl(null)} className={`${fieldButton} inline-block`} download data-testid="download-my-data">{t("download")}</a>
      </section>

      <section aria-labelledby={`${id}-requests`} className="space-y-3">
        <h2 id={`${id}-requests`} className="text-xl font-bold">{t("requestsHeading")}</h2>
        <p className="max-w-2xl text-ink-muted">{t("requestsIntro")}</p>
        <form onSubmit={ask} className="max-w-2xl space-y-3" noValidate>
          <div className="space-y-1">
            <label htmlFor={`${id}-type`} className="block font-semibold">{t("type")}</label>
            <select id={`${id}-type`} className={fieldInput} value={type} onChange={(e) => setType(e.target.value as RequestType)}>
              {requestTypes.map((r) => <option key={r} value={r}>{tTypes(r)}</option>)}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-details`} className="block font-semibold">{t("details")}</label>
            <textarea id={`${id}-details`} className={fieldInput} rows={4} maxLength={4000} dir="auto" value={details} onChange={(e) => setDetails(e.target.value)} />
          </div>
          {type === "Erasure" && <p className="text-sm">{t("erasureNote")}</p>}
          <button type="submit" className={primaryAction} disabled={busy}>{t("send")}</button>
        </form>
        {requests.length === 0 ? <p>{t("noRequests")}</p> : (
          <ul className="space-y-2" data-testid="my-privacy-requests">
            {requests.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-line p-3">
                <span>
                  <span className="font-mono" dir="ltr">{r.number}</span> · {tTypes(r.type)} · {tStates(r.state)}
                  <span className="block text-sm text-ink-muted">{isOpen(r) ? t("due", { date: when(r.dueAt) }) : r.resolution}</span>
                </span>
                {isOpen(r) && <button type="button" className={fieldButton} onClick={() => void withdraw(r.id)} disabled={busy}>{t("withdraw", { number: r.number })}</button>}
              </li>
            ))}
          </ul>
        )}
      </section>

      {privacy.history.length > 0 && (
        <section aria-labelledby={`${id}-history`} className="space-y-3">
          <h2 id={`${id}-history`} className="text-xl font-bold">{t("historyHeading")}</h2>
          <ul className="space-y-1 text-sm">
            {privacy.history.map((h) => (
              <li key={h.id}>
                {when(h.at)} · {t(`history.${h.action}`, { version: h.noticeVersion ?? 0, purpose: privacy.purposes.find((p) => p.key === h.purpose) ? textOf(privacy.purposes.find((p) => p.key === h.purpose)!.name, locale) : (h.purpose ?? "") })}
              </li>
            ))}
          </ul>
        </section>
      )}

      <p role="status" aria-live="polite" className={message?.bad ? "text-bad" : ""}>{message?.text ?? ""}</p>
    </div>
  );
}
