"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { ApiClient, ApiClientKind, ApiClientSecret, IntegrationInfo, Page } from "@/lib/integrations";
import { SecretReveal } from "./SecretReveal";
import { cellClass, codeClass, dangerButton, inputClass, primaryButton, secondaryButton, tableClass } from "./ui";

type Confirm = { id: string; action: "rotate" | "revoke" } | null;

/** The permission's translation key (dots are not allowed in message keys). */
export const permissionKey = (permission: string) => permission.replaceAll(".", "_");

/**
 * Settings → Integrations → API keys and OAuth clients: the list, creating one (its key or secret
 * shown once), changing its permissions and rate limit, replacing its key or secret, and revoking it.
 */
export function ApiClientsPanel({
  initial,
  info,
  csrfToken,
  display,
}: {
  initial: Page<ApiClient>;
  info: IntegrationInfo;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("settings.integrations");
  const tc = useTranslations("settings.integrations.clients");
  const ts = useTranslations("settings.integrations.secret");
  const tCommon = useTranslations("common");
  const locale = useLocale();
  const [clients, setClients] = useState(initial.items);
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const date = (value: string | null) => (value ? formatDateTime(new Date(value), display) : t("never"));
  const number = (value: number) => formatNumber(value, display.locale, display.digits);
  const roleName = (key: string | null) => {
    const role = info.roles.find((r) => r.key === key);
    return (role && (locale === "ar" ? role.name.ar : role.name.en)) ?? role?.name.en ?? key ?? "";
  };

  function failure(error: unknown) {
    setMessage({ ok: false, text: error instanceof ApiError && error.rateLimited ? tCommon("rateLimited") : t("failed") });
  }

  async function reload() {
    setClients((await getJson<Page<ApiClient>>("/v1/integrations/clients?pageSize=100")).items);
  }

  function showSecret(result: ApiClientSecret) {
    if (result.apiKey) {
      setSecret({ label: ts("apiKey"), value: result.apiKey });
    } else if (result.clientSecret) {
      setSecret({ label: ts("clientSecret"), value: result.clientSecret });
    }
  }

  async function run(id: string, action: "rotate" | "revoke") {
    setConfirm(null);
    setMessage(null);
    try {
      if (action === "rotate") {
        showSecret(await postJson<ApiClientSecret>(`/v1/integrations/clients/${id}/rotate`, undefined, csrfToken));
      } else {
        const revoked = await postJson<ApiClient>(`/v1/integrations/clients/${id}/revoke`, undefined, csrfToken);
        setMessage({ ok: true, text: tc("revoked", { name: revoked.name }) });
      }
      await reload();
    } catch (error) {
      failure(error);
    }
  }

  const statusOf = (client: ApiClient) => (client.status === "Revoked" ? "Revoked" : client.expired ? "Expired" : "Active");

  return (
    <div className="space-y-6" data-testid="api-clients-panel">
      <p className="max-w-3xl">{tc("intro", { header: info.apiKeyHeader })}</p>

      {secret && <SecretReveal label={secret.label} value={secret.value} onDone={() => setSecret(null)} />}
      <p role="status" aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ok"}>
        {message?.text ?? ""}
      </p>

      {clients.length === 0 ? (
        <p>{tc("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass} aria-label={tc("listLabel")}>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{tc("name")}</th>
                <th scope="col" className={cellClass}>{tc("kind")}</th>
                <th scope="col" className={cellClass}>{tc("role")}</th>
                <th scope="col" className={cellClass}>{tc("rateLimit")}</th>
                <th scope="col" className={cellClass}>{tc("status")}</th>
                <th scope="col" className={cellClass}>{tc("lastUsed")}</th>
                <th scope="col" className={cellClass}>{tc("actions")}</th>
              </tr>
            </thead>
            <tbody>
              {clients.map((client) => (
                <tr key={client.id} data-testid="api-client-row">
                  <td className={cellClass}>
                    <span className="block font-semibold">{client.name}</span>
                    <code dir="ltr" lang="en" className="text-xs text-ink-muted">
                      {client.keyHint ?? client.clientId}
                    </code>
                    <span className="block text-xs text-ink-muted">
                      {tc("scopesCount", { count: client.scopes.length })}
                    </span>
                    {client.tokenUrl && (
                      <p className="mt-2 text-xs">
                        <span className="block text-ink-muted">{tc("tokenUrl")}</span>
                        <code dir="ltr" lang="en" className={codeClass}>{client.tokenUrl}</code>
                      </p>
                    )}
                  </td>
                  <td className={cellClass}>{tc(`kinds.${client.kind}`)}</td>
                  <td className={cellClass}>{roleName(client.role)}</td>
                  <td className={cellClass}>{number(client.rateLimitPerMinute)}</td>
                  <td className={`${cellClass} ${statusOf(client) === "Active" ? "text-ok" : "text-bad"}`}>{tc(`statuses.${statusOf(client)}`)}</td>
                  <td className={cellClass}>{date(client.lastUsedAt)}</td>
                  <td className={cellClass}>
                    {client.status === "Active" && (
                      <div className="flex flex-wrap gap-2">
                        <button type="button" className={secondaryButton} onClick={() => setEditing(editing === client.id ? null : client.id)}>
                          {tc("edit")}
                        </button>
                        <button type="button" className={secondaryButton} onClick={() => setConfirm({ id: client.id, action: "rotate" })}>
                          {tc("rotate")}
                        </button>
                        <button type="button" className={dangerButton} onClick={() => setConfirm({ id: client.id, action: "revoke" })}>
                          {tc("revoke")}
                        </button>
                      </div>
                    )}
                    {confirm?.id === client.id && (
                      <div className="mt-2 space-y-2" role="group">
                        <p>{tc(confirm.action === "rotate" ? "rotateConfirmText" : "revokeConfirmText", { name: client.name })}</p>
                        <div className="flex flex-wrap gap-2">
                          <button type="button" className={confirm.action === "revoke" ? dangerButton : primaryButton} onClick={() => run(client.id, confirm.action)}>
                            {tc(confirm.action === "rotate" ? "rotateConfirm" : "revokeConfirm")}
                          </button>
                          <button type="button" className={secondaryButton} onClick={() => setConfirm(null)}>
                            {t("cancel")}
                          </button>
                        </div>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editing && clients.find((c) => c.id === editing) && (
        <ApiClientForm
          key={editing}
          info={info}
          csrfToken={csrfToken}
          existing={clients.find((c) => c.id === editing)!}
          onSaved={async () => {
            setEditing(null);
            setMessage({ ok: true, text: tc("saved") });
            await reload();
          }}
          onCancel={() => setEditing(null)}
          onError={failure}
        />
      )}

      <ApiClientForm
        info={info}
        csrfToken={csrfToken}
        onSaved={async (result) => {
          if (result) {
            showSecret(result);
          }
          await reload();
        }}
        onError={failure}
      />
    </div>
  );
}

/** Creating an integration, or changing one's name, permissions, rate limit and expiry. */
function ApiClientForm({
  info,
  csrfToken,
  existing,
  onSaved,
  onCancel,
  onError,
}: {
  info: IntegrationInfo;
  csrfToken: string;
  existing?: ApiClient;
  onSaved: (result: ApiClientSecret | null) => Promise<void>;
  onCancel?: () => void;
  onError: (error: unknown) => void;
}) {
  const t = useTranslations("settings.integrations");
  const tc = useTranslations("settings.integrations.clients");
  const locale = useLocale();
  const id = useId();
  const firstRole = info.roles.find((r) => r.scopes.length > 0)?.key ?? info.roles[0]?.key ?? "";
  const [kind, setKind] = useState<ApiClientKind>(existing?.kind ?? "ApiKey");
  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [role, setRole] = useState(existing?.role ?? firstRole);
  const [scopes, setScopes] = useState<string[]>(existing?.scopes ?? []);
  const [rateLimit, setRateLimit] = useState(String(existing?.rateLimitPerMinute ?? info.defaultRateLimit));
  const [expires, setExpires] = useState(existing?.expiresAt?.slice(0, 10) ?? "");
  const [busy, setBusy] = useState(false);

  const available = info.roles.find((r) => r.key === role)?.scopes ?? [];

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    const body = {
      name,
      description,
      role,
      scopes: scopes.filter((s) => available.includes(s)),
      rateLimitPerMinute: Number(rateLimit),
      expiresAt: expires ? new Date(`${expires}T23:59:59`).toISOString() : undefined,
    };
    try {
      if (existing) {
        await patchJson(`/v1/integrations/clients/${existing.id}`, { ...body, noExpiry: expires ? undefined : true }, csrfToken);
        await onSaved(null);
      } else {
        const result = await postJson<ApiClientSecret>("/v1/integrations/clients", { ...body, kind }, csrfToken);
        setName("");
        setDescription("");
        setScopes([]);
        await onSaved(result);
      }
    } catch (error) {
      onError(error);
    } finally {
      setBusy(false);
    }
  }

  const heading = existing ? `${tc("edit")}: ${existing.name}` : tc("createTitle");
  return (
    <form onSubmit={submit} className="max-w-3xl space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-title`}>
      <h3 id={`${id}-title`} className="text-lg font-semibold">
        {heading}
      </h3>

      {!existing && (
        <fieldset className="space-y-2">
          <legend className="font-semibold">{tc("kind")}</legend>
          {(["ApiKey", "OAuthClient"] as const).map((value) => (
            <label key={value} className="flex items-start gap-2">
              <input type="radio" name={`${id}-kind`} value={value} checked={kind === value} onChange={() => setKind(value)} className="mt-1" />
              <span>
                <span className="block font-semibold">{tc(`kinds.${value}`)}</span>
                <span className="block text-sm text-ink-muted">{tc(`kindHelp.${value}`)}</span>
              </span>
            </label>
          ))}
        </fieldset>
      )}

      <div>
        <label htmlFor={`${id}-name`} className="font-semibold">{tc("name")}</label>
        <input id={`${id}-name`} required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} className={inputClass} />
      </div>
      <div>
        <label htmlFor={`${id}-description`} className="font-semibold">{tc("description")}</label>
        <input id={`${id}-description`} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} className={inputClass} />
      </div>
      <div>
        <label htmlFor={`${id}-role`} className="font-semibold">{tc("role")}</label>
        <select id={`${id}-role`} value={role} onChange={(e) => setRole(e.target.value)} className={inputClass}>
          {info.roles.map((r) => (
            <option key={r.key} value={r.key}>
              {(locale === "ar" ? r.name.ar : r.name.en) ?? r.name.en ?? r.key}
            </option>
          ))}
        </select>
      </div>
      <fieldset className="space-y-2" aria-describedby={`${id}-scopes-help`}>
        <legend className="font-semibold">{tc("scopes")}</legend>
        <p id={`${id}-scopes-help`} className="text-sm text-ink-muted">{tc("scopesHelp")}</p>
        {available.length === 0 ? (
          <p>{tc("noScopes")}</p>
        ) : (
          available.map((scope) => (
            <label key={scope} className="flex flex-wrap items-center gap-x-2 min-w-0">
              <input
                type="checkbox"
                checked={scopes.includes(scope)}
                onChange={(e) => setScopes(e.target.checked ? [...scopes, scope] : scopes.filter((s) => s !== scope))}
              />
              <span className="min-w-0 break-words">{t.has(`permissions.${permissionKey(scope)}`) ? t(`permissions.${permissionKey(scope)}`) : scope}</span>
              <code dir="ltr" lang="en" className="min-w-0 break-all text-xs text-ink-muted">{scope}</code>
            </label>
          ))
        )}
      </fieldset>
      <div>
        <label htmlFor={`${id}-limit`} className="font-semibold">{tc("rateLimit")}</label>
        <input
          id={`${id}-limit`}
          type="number"
          required
          min={1}
          max={info.maxRateLimit}
          value={rateLimit}
          onChange={(e) => setRateLimit(e.target.value)}
          aria-describedby={`${id}-limit-help`}
          className={inputClass}
        />
        <p id={`${id}-limit-help`} className="text-sm text-ink-muted">{tc("rateLimitHelp", { max: formatNumber(info.maxRateLimit, locale, "latn") })}</p>
      </div>
      <div>
        <label htmlFor={`${id}-expires`} className="font-semibold">{tc("expiresAt")}</label>
        <input id={`${id}-expires`} type="date" value={expires} onChange={(e) => setExpires(e.target.value)} className={inputClass} />
      </div>
      <div className="flex flex-wrap gap-3">
        <button type="submit" disabled={busy} className={primaryButton}>
          {existing ? tc("save") : busy ? tc("creating") : tc("create")}
        </button>
        {onCancel && (
          <button type="button" onClick={onCancel} className={secondaryButton}>
            {t("cancel")}
          </button>
        )}
      </div>
    </form>
  );
}
