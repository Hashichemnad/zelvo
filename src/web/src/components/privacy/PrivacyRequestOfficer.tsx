"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { fieldButton, fieldInput, primaryAction } from "@/components/forms/ui";
import { useRouter } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { exportUrl, isOpen, requestTypes, type PrivacyRequest, type RequestType } from "@/lib/privacy";

interface Person {
  id: string;
  displayName: string | null;
  email?: string | null;
}

/** A problem code from the API, in words. */
function useProblem() {
  const t = useTranslations("privacy.officer.problems");
  return (e: unknown) => {
    const code = e instanceof ApiError ? e.code : null;
    const known = ["open-tickets", "erase-self", "last-administrator", "subject-erased", "request-closed", "erase-to-complete", "validation-failed"];
    return t(code && known.includes(code) ? code : "failed");
  };
}

/**
 * A privacy officer logs a request that came by letter, phone or at the counter (task F5): the
 * person (searched by name or email), the right they use, what they ask and how their identity
 * was checked.
 */
export function LogRequestForm({ csrfToken }: { csrfToken: string }) {
  const t = useTranslations("privacy.officer.log");
  const tTypes = useTranslations("privacy.requestTypes");
  const problem = useProblem();
  const router = useRouter();
  const id = useId();
  const [query, setQuery] = useState("");
  const [people, setPeople] = useState<Person[]>([]);
  const [person, setPerson] = useState<Person | null>(null);
  const [type, setType] = useState<RequestType>("Access");
  const [details, setDetails] = useState("");
  const [identity, setIdentity] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function search() {
    if (query.trim().length < 2) return;
    try {
      const found = await getJson<{ items: Person[] }>(`/v1/users?${new URLSearchParams({ q: query.trim(), pageSize: "10" })}`);
      setPeople(found.items);
    } catch {
      setPeople([]);
    }
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!person || !identity.trim()) {
      setError(t(!person ? "choosePerson" : "identityNeeded"));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const created = await postJson<PrivacyRequest>("/v1/privacy/requests", { subjectId: person.id, type, details: details.trim() || null, identityCheck: identity.trim() }, csrfToken);
      router.push(`/privacy-requests/${created.id}`);
    } catch (e) {
      setError(problem(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-3 rounded-lg border border-line p-4" noValidate data-testid="log-privacy-request">
      <h2 className="text-xl font-bold">{t("heading")}</h2>
      <div className="space-y-1">
        <label htmlFor={`${id}-q`} className="block font-semibold">{t("person")}</label>
        <div className="flex gap-2">
          <input id={`${id}-q`} className={fieldInput} value={query} onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void search(); } }} />
          <button type="button" className={fieldButton} onClick={() => void search()}>{t("search")}</button>
        </div>
        {people.length > 0 && (
          <ul className="space-y-1" aria-label={t("results")}>
            {people.map((p) => (
              <li key={p.id}>
                <button type="button" className={`${fieldButton} w-full text-start`} aria-pressed={person?.id === p.id} onClick={() => setPerson(p)}>
                  {p.displayName ?? p.email ?? p.id}{p.email && p.displayName ? <span className="ms-2 text-sm text-ink-muted" dir="ltr">{p.email}</span> : null}
                </button>
              </li>
            ))}
          </ul>
        )}
        {person && <p role="status">{t("chosen", { name: person.displayName ?? person.email ?? person.id })}</p>}
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-type`} className="block font-semibold">{t("type")}</label>
        <select id={`${id}-type`} className={fieldInput} value={type} onChange={(e) => setType(e.target.value as RequestType)}>
          {requestTypes.map((r) => <option key={r} value={r}>{tTypes(r)}</option>)}
        </select>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-details`} className="block font-semibold">{t("details")}</label>
        <textarea id={`${id}-details`} className={fieldInput} rows={3} maxLength={4000} dir="auto" value={details} onChange={(e) => setDetails(e.target.value)} />
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-identity`} className="block font-semibold">{t("identity")}</label>
        <input id={`${id}-identity`} className={fieldInput} required maxLength={4000} value={identity} onChange={(e) => setIdentity(e.target.value)} aria-describedby={`${id}-identity-hint`} />
        <p id={`${id}-identity-hint`} className="text-sm text-ink-muted">{t("identityHint")}</p>
      </div>
      {error && <p role="alert" className="text-bad">{error}</p>}
      <button type="submit" className={primaryAction} disabled={busy}>{t("submit")}</button>
    </form>
  );
}

/**
 * What a privacy officer does with a request (task F5): start, download a copy of the person's
 * data, complete with what was done, refuse with the reason, or, for erasure requests, erase the
 * person after typing the request's number.
 */
export function PrivacyRequestActions({ initial, csrfToken }: { initial: PrivacyRequest; csrfToken: string }) {
  const t = useTranslations("privacy.officer.actions");
  const problem = useProblem();
  const router = useRouter();
  const id = useId();
  const [request, setRequest] = useState(initial);
  const [resolution, setResolution] = useState("");
  const [confirm, setConfirm] = useState("");
  const [message, setMessage] = useState<{ text: string; bad: boolean } | null>(null);
  const [busy, setBusy] = useState(false);

  async function act(path: string, body: unknown, done: string) {
    setBusy(true);
    setMessage(null);
    try {
      setRequest(await postJson<PrivacyRequest>(`/v1/privacy/requests/${request.id}/${path}`, body, csrfToken));
      setMessage({ text: done, bad: false });
      router.refresh();
    } catch (e) {
      setMessage({ text: problem(e), bad: true });
    } finally {
      setBusy(false);
    }
  }

  if (!isOpen(request)) {
    return <p role="status" data-testid="privacy-request-closed">{t("closed")}</p>;
  }

  const erasable = request.type === "Erasure" && request.holdings?.erasureRefusal === "None";
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="privacy-request-actions">
      <h2 id={`${id}-heading`} className="text-xl font-bold">{t("heading")}</h2>
      <div className="flex flex-wrap gap-2">
        {request.state === "Received" && <button type="button" className={fieldButton} disabled={busy} onClick={() => void act("start", {}, t("started"))}>{t("start")}</button>}
        {request.subject && !request.subject.erased && (
          <a href={exportUrl(request.id)} className={`${fieldButton} inline-block`} download data-testid="download-copy">{t("download")}</a>
        )}
      </div>

      <div className="space-y-2">
        <label htmlFor={`${id}-resolution`} className="block font-semibold">{t("resolution")}</label>
        <textarea id={`${id}-resolution`} className={fieldInput} rows={3} maxLength={4000} dir="auto" value={resolution} onChange={(e) => setResolution(e.target.value)}
          aria-describedby={`${id}-resolution-hint`} />
        <p id={`${id}-resolution-hint`} className="text-sm text-ink-muted">{t("resolutionHint")}</p>
        <div className="flex flex-wrap gap-2">
          {request.type !== "Erasure" && (
            <button type="button" className={primaryAction} disabled={busy || !resolution.trim()} onClick={() => void act("complete", { resolution }, t("completed"))}>{t("complete")}</button>
          )}
          <button type="button" className={fieldButton} disabled={busy || !resolution.trim()} onClick={() => void act("reject", { resolution }, t("rejected"))}>{t("reject")}</button>
        </div>
      </div>

      {request.type === "Erasure" && (
        <div className="space-y-2 rounded-lg border-2 border-bad p-4" data-testid="erase-person">
          <h3 className="font-bold">{t("eraseHeading")}</h3>
          <p>{t("eraseWarning")}</p>
          {!erasable && request.holdings && request.holdings.erasureRefusal !== "None" && <p role="alert" className="text-bad">{t(`refusal.${request.holdings.erasureRefusal}`)}</p>}
          <label htmlFor={`${id}-confirm`} className="block font-semibold">{t("confirm", { number: request.number })}</label>
          <input id={`${id}-confirm`} dir="ltr" className={fieldInput} value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="off" />
          <button type="button" className="rounded-md bg-bad px-4 py-2 font-semibold text-surface disabled:opacity-60"
            disabled={busy || !erasable || confirm.trim().toUpperCase() !== request.number.toUpperCase()}
            onClick={() => void act("erase", { confirmNumber: confirm.trim(), resolution: resolution.trim() || null }, t("erased"))}>
            {t("erase")}
          </button>
        </div>
      )}
      <p role="status" aria-live="polite" className={message?.bad ? "text-bad" : ""}>{message?.text ?? ""}</p>
    </section>
  );
}
