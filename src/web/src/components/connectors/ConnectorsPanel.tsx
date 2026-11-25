"use client";

import { useId, useState, type FormEvent, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import {
  connectorErrorCode,
  connectorKinds,
  crmKinds,
  directoryKinds,
  emptyOperation,
  emptySettings,
  restPlaceholders,
  type Connector,
  type ConnectorCall,
  type ConnectorKind,
  type ConnectorSettings,
  type GroupMapping,
  type RestOperation,
  type RestOutcome,
  type RestSettings,
} from "@/lib/connectors";
import type { Page } from "@/lib/channels";
import { textOf, type GroupSummary } from "@/lib/directory";
import type { Locale } from "@/i18n/routing";
import { cellClass, codeClass, inputClass, primaryButton, secondaryButton, tableClass } from "../settings/integrations/ui";

/**
 * Settings → Connectors (module 14, task E7): identity directories, CRMs and REST connectors.
 * Each connector shows its last sync and calls; credentials are write-only; the REST builder
 * tests an operation before it is saved.
 */
export function ConnectorsPanel({ initial, csrfToken, groups, display }: { initial: Connector[]; csrfToken: string; groups: GroupSummary[]; display: DateDisplayOptions }) {
  const t = useTranslations("connectors");
  const [connectors, setConnectors] = useState(initial);
  const [adding, setAdding] = useState<ConnectorKind | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const replace = (saved: Connector) => setConnectors((all) => (all.some((c) => c.id === saved.id) ? all.map((c) => (c.id === saved.id ? saved : c)) : [...all, saved]));

  return (
    <div className="space-y-6" data-testid="connectors-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {connectors.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("fields.name")}</th>
                <th scope="col" className={cellClass}>{t("fields.kind")}</th>
                <th scope="col" className={cellClass}>{t("lastSync")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{t("open")}</span></th>
              </tr>
            </thead>
            <tbody>
              {connectors.map((c) => {
                const code = connectorErrorCode(c.lastError);
                return (
                  <tr key={c.id} data-testid="connector-row">
                    <td className={cellClass}>
                      <span className="font-semibold">{c.name}</span>
                      {!c.active && <span className="block text-sm text-ink-muted">{t("off")}</span>}
                    </td>
                    <td className={cellClass}>{t(`kinds.${c.kind}`)}</td>
                    <td className={cellClass}>
                      {c.kind === "Rest" ? t("calledByRules") : c.lastSyncAt ? formatDateTime(new Date(c.lastSyncAt), display) : t("never")}
                      {code && <span className="block text-bad">{t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other")}</span>}
                    </td>
                    <td className={cellClass}>
                      <button type="button" className="text-brand underline" aria-expanded={open === c.id} aria-label={`${open === c.id ? t("close") : t("open")} ${c.name}`}
                        onClick={() => setOpen(open === c.id ? null : c.id)}>
                        {open === c.id ? t("close") : t("open")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {connectors.filter((c) => c.id === open).map((c) => (
        <ConnectorDetail key={c.id} connector={c} csrfToken={csrfToken} groups={groups} display={display} onSaved={replace} />
      ))}

      {adding ? (
        <section className="space-y-3 rounded-md border border-line p-4" aria-labelledby="add-connector-heading">
          <h3 id="add-connector-heading" className="text-lg font-semibold">{t("addKind", { kind: t(`kinds.${adding}`) })}</h3>
          <ConnectorForm connector={null} kind={adding} csrfToken={csrfToken} groups={groups}
            onSaved={(saved) => { replace(saved); setAdding(null); setOpen(saved.id); }} onCancel={() => setAdding(null)} />
        </section>
      ) : (
        <div className="space-y-2">
          <p className="font-semibold">{t("add")}</p>
          <div className="flex flex-wrap gap-2">
            {connectorKinds.map((kind) => (
              <button key={kind} type="button" className={secondaryButton} onClick={() => setAdding(kind)}>{t(`kinds.${kind}`)}</button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function ConnectorDetail({ connector, csrfToken, groups, display, onSaved }: {
  connector: Connector; csrfToken: string; groups: GroupSummary[]; display: DateDisplayOptions; onSaved: (c: Connector) => void;
}) {
  const t = useTranslations("connectors");
  const id = useId();
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [calls, setCalls] = useState<ConnectorCall[] | null>(null);
  const syncs = connector.kind !== "Rest";

  async function test() {
    const result = await postJson<{ ok: boolean; detail: string | null; error: string | null }>(`/v1/connectors/${connector.id}/test`, {}, csrfToken);
    const code = connectorErrorCode(result.error);
    setMessage(result.ok ? { ok: true, text: t("testOk", { detail: result.detail ?? "" }) } : { ok: false, text: t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other") });
  }

  async function syncNow() {
    onSaved(await postJson<Connector>(`/v1/connectors/${connector.id}/sync`, {}, csrfToken));
    setMessage({ ok: true, text: t("syncQueued") });
  }

  async function loadCalls() {
    setCalls((await getJson<Page<ConnectorCall>>(`/v1/connectors/${connector.id}/calls?pageSize=50`)).items);
  }

  async function resend(callId: number) {
    await postJson(`/v1/connectors/${connector.id}/calls/${callId}/resend`, {}, csrfToken);
    await loadCalls();
  }

  const stats = connector.lastSyncStats;
  return (
    <section className="space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-heading`} data-testid="connector-detail">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{connector.name}</h3>
      {syncs && (
        <dl className="grid gap-2 text-sm sm:grid-cols-3">
          <div><dt className="text-ink-muted">{t("lastSync")}</dt><dd>{connector.lastSyncAt ? formatDateTime(new Date(connector.lastSyncAt), display) : t("never")}</dd></div>
          <div><dt className="text-ink-muted">{t("nextSync")}</dt><dd>{connector.nextSyncAt ? formatDateTime(new Date(connector.nextSyncAt), display) : "—"}</dd></div>
          {stats && (
            <div data-testid="sync-stats">
              <dt className="text-ink-muted">{t("stats.title")}</dt>
              <dd>{t("stats.summary", { read: stats.read, created: stats.created, updated: stats.updated, disabled: stats.disabled })}</dd>
            </div>
          )}
        </dl>
      )}
      {connector.signInRedirectUri && (
        <div className="space-y-1">
          <p className="text-sm">{t("redirectUri")}</p>
          <code dir="ltr" className={codeClass}>{connector.signInRedirectUri}</code>
        </div>
      )}
      <div className="flex flex-wrap gap-2">
        <button type="button" className={secondaryButton} onClick={test}>{t("test")}</button>
        {syncs && <button type="button" className={secondaryButton} onClick={syncNow}>{t("syncNow")}</button>}
        {connector.kind !== "EntraId" && connector.kind !== "Ldap" && <button type="button" className={secondaryButton} onClick={loadCalls}>{t("calls.show")}</button>}
      </div>
      <p aria-live="polite" className={message?.ok ? "text-ok" : "text-bad"}>{message?.text ?? ""}</p>
      {calls && (
        <div className="overflow-x-auto">
          <table className={tableClass} data-testid="connector-calls">
            <caption className="text-start font-semibold">{t("calls.title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("calls.when")}</th>
                <th scope="col" className={cellClass}>{t("calls.operation")}</th>
                <th scope="col" className={cellClass}>{t("calls.ticket")}</th>
                <th scope="col" className={cellClass}>{t("calls.status")}</th>
              </tr>
            </thead>
            <tbody>
              {calls.length === 0 && <tr><td colSpan={4} className={cellClass}>{t("calls.empty")}</td></tr>}
              {calls.map((call) => (
                <tr key={call.id}>
                  <td className={cellClass}>{formatDateTime(new Date(call.createdAt), display)}</td>
                  <td className={cellClass}><span dir="ltr">{call.operation}</span></td>
                  <td className={cellClass}><span dir="ltr">{call.ticketNumber ?? "—"}</span></td>
                  <td className={cellClass}>
                    {t(`calls.statuses.${call.status}`)}
                    {call.error && <span className="block text-sm text-ink-muted" dir="ltr">{call.error}</span>}
                    {call.status === "Failed" && <button type="button" className="text-brand underline" onClick={() => resend(call.id)}>{t("calls.resend")}</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <details>
        <summary className="cursor-pointer font-semibold">{t("edit")}</summary>
        <div className="pt-3">
          <ConnectorForm connector={connector} kind={connector.kind} csrfToken={csrfToken} groups={groups} onSaved={onSaved} />
        </div>
      </details>
    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: (id: string, describedBy?: string) => ReactNode }) {
  const id = useId();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block font-semibold">{label}</label>
      {hint && <p id={`${id}-hint`} className="text-sm text-ink-muted">{hint}</p>}
      {children(id, hint ? `${id}-hint` : undefined)}
    </div>
  );
}

function TextInput({ label, hint, value, onChange, ltr = true, type = "text" }: { label: string; hint?: string; value: string; onChange: (v: string) => void; ltr?: boolean; type?: string }) {
  return (
    <Field label={label} hint={hint}>
      {(id, describedBy) => <input id={id} className={inputClass} dir={ltr ? "ltr" : undefined} type={type} value={value} autoComplete="off" aria-describedby={describedBy}
        onChange={(e) => onChange(e.target.value)} />}
    </Field>
  );
}

function MappingsEditor({ mappings, groups, onChange }: { mappings: GroupMapping[]; groups: GroupSummary[]; onChange: (m: GroupMapping[]) => void }) {
  const t = useTranslations("connectors");
  const locale = useLocale() as Locale;
  return (
    <fieldset className="space-y-2 rounded-md border border-line p-3">
      <legend className="font-semibold">{t("fields.groupMappings")}</legend>
      <p className="text-sm text-ink-muted">{t("hints.groupMappings")}</p>
      {mappings.map((m, i) => (
        <div key={i} className="grid items-end gap-2 sm:grid-cols-[1fr_1fr_auto]">
          <TextInput label={t("fields.directoryGroup")} value={m.externalId} onChange={(v) => onChange(mappings.map((x, j) => (j === i ? { ...x, externalId: v } : x)))} />
          <Field label={t("fields.zelvoGroup")}>
            {(id) => (
              <select id={id} className={inputClass} value={m.groupId} onChange={(e) => onChange(mappings.map((x, j) => (j === i ? { ...x, groupId: e.target.value } : x)))}>
                <option value="">{t("chooseGroup")}</option>
                {groups.map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
              </select>
            )}
          </Field>
          <button type="button" className={secondaryButton} onClick={() => onChange(mappings.filter((_, j) => j !== i))}>{t("remove")}</button>
        </div>
      ))}
      <button type="button" className={secondaryButton} onClick={() => onChange([...mappings, { externalId: "", groupId: "" }])}>{t("addMapping")}</button>
    </fieldset>
  );
}

/** Adds or changes a connector; the fields follow its kind. */
export function ConnectorForm({ connector, kind, csrfToken, groups, onSaved, onCancel }: {
  connector: Connector | null; kind: ConnectorKind; csrfToken: string; groups: GroupSummary[]; onSaved: (c: Connector) => void; onCancel?: () => void;
}) {
  const t = useTranslations("connectors");
  const [name, setName] = useState(connector?.name ?? "");
  const [settings, setSettings] = useState<ConnectorSettings>(connector?.settings ?? emptySettings(kind));
  const [credential, setCredential] = useState("");
  const [refreshToken, setRefreshToken] = useState("");
  const [active, setActive] = useState(connector?.active ?? true);
  const [syncMinutes, setSyncMinutes] = useState(String(connector?.syncMinutes ?? 60));
  const [errors, setErrors] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const keep = connector?.credentialSet ? ` ${t("keepHint")}` : "";

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setErrors([]);
    setSaved(false);
    const body: Record<string, unknown> = { name, active, ...settings };
    if (!connector) body.kind = kind;
    if (kind !== "Rest") body.syncMinutes = Number(syncMinutes);
    if (credential) body.credential = credential;
    if (refreshToken) body.refreshToken = refreshToken;
    try {
      const result = connector ? await patchJson<Connector>(`/v1/connectors/${connector.id}`, body, csrfToken) : await postJson<Connector>("/v1/connectors", body, csrfToken);
      setCredential("");
      setRefreshToken("");
      setSaved(true);
      onSaved(result);
    } catch (e) {
      const problems = e instanceof ApiError ? (e.details.errors as Record<string, string[]> | undefined) : undefined;
      setErrors(problems ? Object.values(problems).flat() : [e instanceof ApiError && e.code === "identity-provider-unavailable" ? t("errors.signInSetup") : t("errors.other")]);
    } finally {
      setBusy(false);
    }
  }

  const entra = settings.entra;
  const ldap = settings.ldap;
  const crm = settings.crm;
  return (
    <form onSubmit={submit} className="max-w-3xl space-y-4" noValidate data-testid="connector-form">
      <TextInput label={t("fields.name")} value={name} onChange={setName} ltr={false} />
      {entra && (
        <>
          <TextInput label={t("fields.directoryId")} value={entra.directoryId} onChange={(v) => setSettings({ entra: { ...entra, directoryId: v } })} />
          <TextInput label={t("fields.clientId")} hint={t("hints.entraApp")} value={entra.clientId} onChange={(v) => setSettings({ entra: { ...entra, clientId: v } })} />
          <TextInput label={t("fields.usersGroupId")} value={entra.usersGroupId ?? ""} onChange={(v) => setSettings({ entra: { ...entra, usersGroupId: v || null } })} />
          <MappingsEditor mappings={entra.groupMappings} groups={groups} onChange={(m) => setSettings({ entra: { ...entra, groupMappings: m } })} />
          <label className="flex items-center gap-2"><input type="checkbox" checked={entra.signIn} onChange={(e) => setSettings({ entra: { ...entra, signIn: e.target.checked } })} />{t("fields.signInEntra")}</label>
          <TextInput label={t("fields.clientSecret") + keep} type="password" value={credential} onChange={setCredential} />
        </>
      )}
      {ldap && (
        <>
          <div className="grid gap-2 sm:grid-cols-[2fr_1fr]">
            <TextInput label={t("fields.host")} value={ldap.host} onChange={(v) => setSettings({ ldap: { ...ldap, host: v } })} />
            <TextInput label={t("fields.port")} value={String(ldap.port)} onChange={(v) => setSettings({ ldap: { ...ldap, port: Number(v) || 0 } })} />
          </div>
          <Field label={t("fields.security")}>
            {(id) => (
              <select id={id} className={inputClass} value={ldap.security} onChange={(e) => setSettings({ ldap: { ...ldap, security: e.target.value as "Ldaps" | "StartTls" | "None" } })}>
                {(["Ldaps", "StartTls", "None"] as const).map((s) => <option key={s} value={s}>{t(`security.${s}`)}</option>)}
              </select>
            )}
          </Field>
          <TextInput label={t("fields.bindDn")} value={ldap.bindDn} onChange={(v) => setSettings({ ldap: { ...ldap, bindDn: v } })} />
          <TextInput label={t("fields.bindPassword") + keep} type="password" value={credential} onChange={setCredential} />
          <TextInput label={t("fields.baseDn")} value={ldap.baseDn} onChange={(v) => setSettings({ ldap: { ...ldap, baseDn: v } })} />
          <TextInput label={t("fields.userFilter")} value={ldap.userFilter} onChange={(v) => setSettings({ ldap: { ...ldap, userFilter: v } })} />
          <label className="flex items-center gap-2"><input type="checkbox" checked={ldap.activeDirectory} onChange={(e) => setSettings({ ldap: { ...ldap, activeDirectory: e.target.checked } })} />{t("fields.activeDirectory")}</label>
          <MappingsEditor mappings={ldap.groupMappings} groups={groups} onChange={(m) => setSettings({ ldap: { ...ldap, groupMappings: m } })} />
          <label className="flex items-center gap-2"><input type="checkbox" checked={ldap.signIn} onChange={(e) => setSettings({ ldap: { ...ldap, signIn: e.target.checked } })} />{t("fields.signInLdap")}</label>
        </>
      )}
      {crm && (
        <>
          {kind !== "HubSpot" && <TextInput label={t(`fields.crmUrl.${kind}`)} value={crm.url ?? ""} onChange={(v) => setSettings({ crm: { ...crm, url: v || null } })} />}
          {kind === "Zoho" && <TextInput label={t("fields.apiUrl")} value={crm.apiUrl ?? ""} onChange={(v) => setSettings({ crm: { ...crm, apiUrl: v || null } })} />}
          {kind === "Dynamics365" && <TextInput label={t("fields.directoryId")} value={crm.directoryId ?? ""} onChange={(v) => setSettings({ crm: { ...crm, directoryId: v || null } })} />}
          {kind !== "HubSpot" && <TextInput label={t("fields.clientId")} value={crm.clientId ?? ""} onChange={(v) => setSettings({ crm: { ...crm, clientId: v || null } })} />}
          <TextInput label={t(kind === "HubSpot" ? "fields.accessToken" : "fields.clientSecret") + keep} type="password" value={credential} onChange={setCredential} />
          {kind === "Zoho" && <TextInput label={t("fields.refreshToken")} type="password" value={refreshToken} onChange={setRefreshToken} />}
          <label className="flex items-center gap-2"><input type="checkbox" checked={crm.customerGroups} onChange={(e) => setSettings({ crm: { ...crm, customerGroups: e.target.checked } })} />{t("fields.customerGroups")}</label>
          <p className="text-sm text-ink-muted">{t("hints.crm")}</p>
        </>
      )}
      {settings.rest && (
        <RestBuilder settings={settings.rest} onChange={(rest) => setSettings({ rest })} credential={credential} onCredential={setCredential} keep={keep}
          connectorId={connector?.id ?? null} csrfToken={csrfToken} />
      )}
      {(directoryKinds.includes(kind) || crmKinds.includes(kind)) && (
        <TextInput label={t("fields.syncMinutes")} value={syncMinutes} onChange={setSyncMinutes} />
      )}
      <label className="flex items-center gap-2"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />{t("fields.active")}</label>
      <div aria-live="polite">
        {errors.length > 0 && <ul role="alert" className="list-disc ps-5 text-bad">{errors.map((e, i) => <li key={i} dir="auto">{e}</li>)}</ul>}
        {saved && <p className="text-ok">{t("saved")}</p>}
      </div>
      <div className="flex gap-2">
        <button type="submit" className={primaryButton} disabled={busy}>{t("save")}</button>
        {onCancel && <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>}
      </div>
    </form>
  );
}

/** The no-code REST connector builder: address, sign-in, operations, and a test of each before saving. */
export function RestBuilder({ settings, onChange, credential, onCredential, keep, connectorId, csrfToken }: {
  settings: RestSettings; onChange: (s: RestSettings) => void; credential: string; onCredential: (v: string) => void; keep: string; connectorId: string | null; csrfToken: string;
}) {
  const t = useTranslations("connectors");
  const [ticketNumber, setTicketNumber] = useState("");
  const [outcomes, setOutcomes] = useState<Record<number, RestOutcome | string>>({});
  const setOperation = (i: number, op: RestOperation) => onChange({ ...settings, operations: settings.operations.map((o, j) => (j === i ? op : o)) });

  async function test(i: number) {
    try {
      const outcome = await postJson<RestOutcome>("/v1/connectors/rest/test", {
        connectorId, settings, credential: credential || null, operation: settings.operations[i]!.key, ticketNumber: ticketNumber || null,
      }, csrfToken);
      setOutcomes({ ...outcomes, [i]: outcome });
    } catch (e) {
      const problems = e instanceof ApiError ? (e.details.errors as Record<string, string[]> | undefined) : undefined;
      setOutcomes({ ...outcomes, [i]: problems ? Object.values(problems).flat().join(" ") : t("errors.other") });
    }
  }

  return (
    <div className="space-y-4" data-testid="rest-builder">
      <TextInput label={t("rest.baseUrl")} value={settings.baseUrl} onChange={(v) => onChange({ ...settings, baseUrl: v })} />
      <Field label={t("rest.auth")}>
        {(id) => (
          <select id={id} className={inputClass} value={settings.auth} onChange={(e) => onChange({ ...settings, auth: e.target.value as RestSettings["auth"] })}>
            {(["None", "ApiKey", "Basic", "Bearer", "OAuth2"] as const).map((a) => <option key={a} value={a}>{t(`rest.auths.${a}`)}</option>)}
          </select>
        )}
      </Field>
      {settings.auth === "ApiKey" && <TextInput label={t("rest.authHeader")} value={settings.authHeader ?? ""} onChange={(v) => onChange({ ...settings, authHeader: v || null })} />}
      {settings.auth === "Basic" && <TextInput label={t("rest.username")} value={settings.username ?? ""} onChange={(v) => onChange({ ...settings, username: v || null })} />}
      {settings.auth === "OAuth2" && (
        <>
          <TextInput label={t("rest.tokenUrl")} value={settings.tokenUrl ?? ""} onChange={(v) => onChange({ ...settings, tokenUrl: v || null })} />
          <TextInput label={t("fields.clientId")} value={settings.clientId ?? ""} onChange={(v) => onChange({ ...settings, clientId: v || null })} />
          <TextInput label={t("rest.scope")} value={settings.scope ?? ""} onChange={(v) => onChange({ ...settings, scope: v || null })} />
        </>
      )}
      {settings.auth !== "None" && <TextInput label={t("rest.credential") + keep} type="password" value={credential} onChange={onCredential} />}
      <p className="text-sm text-ink-muted">{t("rest.placeholders")} <span dir="ltr">{restPlaceholders.map((p) => `{{${p}}}`).join(" ")}</span></p>
      <TextInput label={t("rest.testTicket")} value={ticketNumber} onChange={setTicketNumber} />

      {settings.operations.map((op, i) => {
        const outcome = outcomes[i];
        return (
          <fieldset key={i} className="space-y-3 rounded-md border border-line p-3" data-testid="rest-operation">
            <legend className="font-semibold">{t("rest.operation", { number: i + 1 })}</legend>
            <div className="grid gap-2 sm:grid-cols-3">
              <TextInput label={t("rest.key")} value={op.key} onChange={(v) => setOperation(i, { ...op, key: v })} />
              <TextInput label={t("rest.nameEn")} value={op.name.en ?? ""} ltr={false} onChange={(v) => setOperation(i, { ...op, name: { ...op.name, en: v || null } })} />
              <TextInput label={t("rest.nameAr")} value={op.name.ar ?? ""} ltr={false} onChange={(v) => setOperation(i, { ...op, name: { ...op.name, ar: v || null } })} />
            </div>
            <div className="grid gap-2 sm:grid-cols-[1fr_3fr]">
              <Field label={t("rest.method")}>
                {(id) => (
                  <select id={id} className={inputClass} value={op.method} onChange={(e) => setOperation(i, { ...op, method: e.target.value as RestOperation["method"] })}>
                    {(["GET", "POST", "PUT", "PATCH", "DELETE"] as const).map((m) => <option key={m} value={m}>{m}</option>)}
                  </select>
                )}
              </Field>
              <TextInput label={t("rest.path")} value={op.path} onChange={(v) => setOperation(i, { ...op, path: v })} />
            </div>
            {op.method !== "GET" && op.method !== "DELETE" && (
              <Field label={t("rest.body")}>
                {(id) => <textarea id={id} dir="ltr" rows={4} className={`${inputClass} font-mono`} value={op.body ?? ""} onChange={(e) => setOperation(i, { ...op, body: e.target.value || null })} />}
              </Field>
            )}
            <fieldset className="space-y-2">
              <legend className="text-sm font-semibold">{t("rest.results")}</legend>
              {op.results.map((r, k) => (
                <div key={k} className="grid items-end gap-2 sm:grid-cols-4">
                  <TextInput label={t("rest.key")} value={r.key} onChange={(v) => setOperation(i, { ...op, results: op.results.map((x, j) => (j === k ? { ...x, key: v } : x)) })} />
                  <TextInput label={t("rest.nameEn")} ltr={false} value={r.name.en ?? ""} onChange={(v) => setOperation(i, { ...op, results: op.results.map((x, j) => (j === k ? { ...x, name: { ...x.name, en: v || null } } : x)) })} />
                  <TextInput label={t("rest.nameAr")} ltr={false} value={r.name.ar ?? ""} onChange={(v) => setOperation(i, { ...op, results: op.results.map((x, j) => (j === k ? { ...x, name: { ...x.name, ar: v || null } } : x)) })} />
                  <TextInput label={t("rest.resultPath")} value={r.path} onChange={(v) => setOperation(i, { ...op, results: op.results.map((x, j) => (j === k ? { ...x, path: v } : x)) })} />
                </div>
              ))}
              <button type="button" className={secondaryButton} onClick={() => setOperation(i, { ...op, results: [...op.results, { key: "value", name: { en: null, ar: null }, path: "" }] })}>
                {t("rest.addResult")}
              </button>
            </fieldset>
            <TextInput label={t("rest.externalRefPath")} value={op.externalRefPath ?? ""} onChange={(v) => setOperation(i, { ...op, externalRefPath: v || null })} />
            <div className="flex flex-wrap gap-2">
              <button type="button" className={secondaryButton} onClick={() => test(i)}>{t("rest.test")}</button>
              {settings.operations.length > 1 && (
                <button type="button" className={secondaryButton} onClick={() => onChange({ ...settings, operations: settings.operations.filter((_, j) => j !== i) })}>{t("remove")}</button>
              )}
            </div>
            <div aria-live="polite" data-testid="rest-outcome">
              {typeof outcome === "string" && <p role="alert" className="text-bad">{outcome}</p>}
              {outcome && typeof outcome !== "string" && (
                <div className="space-y-2 text-sm">
                  <p className={outcome.ok ? "text-ok" : "text-bad"}>
                    {outcome.ok ? t("rest.ok", { status: outcome.status ?? 0, ms: outcome.durationMs }) : t("rest.failed", { status: outcome.status ?? 0 })}
                  </p>
                  <p className="font-semibold">{t("rest.sent")}</p>
                  <pre dir="ltr" className={codeClass}>{outcome.request}</pre>
                  {outcome.response && (<><p className="font-semibold">{t("rest.answer")}</p><pre dir="ltr" className={codeClass}>{outcome.response}</pre></>)}
                  {Object.keys(outcome.results).length > 0 && (
                    <dl className="grid gap-1 sm:grid-cols-2">
                      {Object.entries(outcome.results).map(([key, value]) => (
                        <div key={key}><dt className="text-ink-muted" dir="ltr">{key}</dt><dd dir="auto">{value ?? "—"}</dd></div>
                      ))}
                    </dl>
                  )}
                  {outcome.error && <p className="text-bad" dir="ltr">{outcome.error}</p>}
                </div>
              )}
            </div>
          </fieldset>
        );
      })}
      <button type="button" className={secondaryButton} onClick={() => onChange({ ...settings, operations: [...settings.operations, { ...emptyOperation(), key: `operation-${settings.operations.length + 1}` }] })}>
        {t("rest.addOperation")}
      </button>
    </div>
  );
}
