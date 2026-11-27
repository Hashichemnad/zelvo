"use client";

import { useId, useState, type FormEvent, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { ApiError, deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import {
  connectorKindOf,
  discoverySourceKinds,
  type DiscoverySettings,
  type DiscoverySource,
  type DiscoverySourceKind,
  type DiscoveryTest,
  type ReconcileStats,
} from "@/lib/cmdb";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { cellClass, codeClass, inputClass, primaryButton, secondaryButton, tableClass } from "../settings/integrations/ui";

/** A connector a source may use (from Settings → Connectors). */
export interface ConnectorChoice {
  id: string;
  name: string;
  kind: string;
}

const syncChoices = [60, 360, 720, 1440, 10080] as const;
const statKeys: (keyof ReconcileStats)[] = ["received", "created", "updated", "unchanged", "linked", "review", "duplicates", "missing", "unknownClass", "valuesRefused", "software", "relationships"];

interface Draft {
  name: string;
  kind: DiscoverySourceKind;
  connectorId: string;
  active: boolean;
  priority: string;
  syncMinutes: number;
  attributePriorities: string;
  classMap: string;
  includeApps: boolean;
  subscriptionIds: string;
  resourceTypes: string;
  baseDn: string;
  computerFilter: string;
  url: string;
  username: string;
  thumbprint: string;
  credential: string;
}

const lines = (text: string) => text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);

/** "key=value" lines to a map; numbers when asked. */
function pairs(text: string): Record<string, string> {
  const result: Record<string, string> = {};
  for (const line of lines(text)) {
    const at = line.indexOf("=");
    if (at > 0) result[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return result;
}

const toPairs = (map: Record<string, string | number> | undefined) => Object.entries(map ?? {}).map(([k, v]) => `${k}=${v}`).join("\n");

function draftOf(source: DiscoverySource | null, kind: DiscoverySourceKind): Draft {
  const s: DiscoverySettings = source?.settings ?? {};
  return {
    name: source?.name ?? "",
    kind: source?.kind ?? kind,
    connectorId: source?.connectorId ?? "",
    active: source?.active ?? true,
    priority: String(source?.priority ?? 50),
    syncMinutes: source?.syncMinutes ?? 1440,
    attributePriorities: toPairs(source?.attributePriorities),
    classMap: toPairs(s.classMap),
    includeApps: s.includeApps ?? true,
    subscriptionIds: (s.subscriptionIds ?? []).join("\n"),
    resourceTypes: (s.resourceTypes ?? []).join("\n"),
    baseDn: s.baseDn ?? "",
    computerFilter: s.computerFilter ?? "(objectCategory=computer)",
    url: s.url ?? "",
    username: s.username ?? "",
    thumbprint: s.certificateThumbprint ?? "",
    credential: "",
  };
}

function settingsOf(d: Draft): DiscoverySettings {
  const settings: DiscoverySettings = { classMap: pairs(d.classMap) };
  if (d.kind === "Intune") settings.includeApps = d.includeApps;
  if (d.kind === "Azure") {
    settings.subscriptionIds = lines(d.subscriptionIds);
    if (lines(d.resourceTypes).length > 0) settings.resourceTypes = lines(d.resourceTypes).map((t) => t.toLowerCase());
  }
  if (d.kind === "ActiveDirectory") {
    settings.baseDn = d.baseDn.trim() || null;
    settings.computerFilter = d.computerFilter.trim();
  }
  if (d.kind === "VCenter") {
    settings.url = d.url.trim();
    settings.username = d.username.trim();
    settings.certificateThumbprint = d.thumbprint.trim() || null;
  }
  return settings;
}

/**
 * Settings → CMDB → Discovery (task G2, cmdb.discovery.manage): the discovery API for tools of
 * the organisation's own, Microsoft Intune, Entra ID devices and Azure (an Entra ID connector's
 * app), Active Directory computers (an LDAP connector) and VMware vCenter. Each source has its
 * precedence (lower wins, per attribute where set), a schedule, a test that changes nothing, "Sync
 * now" and its last sync's counts. The vCenter password is write-only.
 */
export function DiscoverySources({
  initial,
  connectors,
  csrfToken,
  display,
}: {
  initial: DiscoverySource[];
  connectors: ConnectorChoice[];
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("cmdb.discovery");
  const id = useId();
  const [sources, setSources] = useState(initial);
  const [editing, setEditing] = useState<{ source: DiscoverySource | null; draft: Draft } | null>(null);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [test, setTest] = useState<{ id: string; result: DiscoveryTest } | null>(null);
  const [busy, setBusy] = useState(false);
  const [newKind, setNewKind] = useState<DiscoverySourceKind>("DiscoveryApi");
  const number = (n: number) => formatNumber(n, display.locale, display.digits);

  const errorText = (error: unknown) => {
    if (error instanceof ApiError) {
      if (error.rateLimited) return t("errors.rate-limited");
      if (error.code && t.has(`errors.${error.code}`)) return t(`errors.${error.code}`);
    }
    return t("errors.failed");
  };

  async function act(action: () => Promise<void>, done: string) {
    setBusy(true);
    setStatus(null);
    try {
      await action();
      setSources(await getJson<DiscoverySource[]>("/v1/cmdb/discovery/sources"));
      setStatus({ ok: true, text: done });
    } catch (error) {
      setStatus({ ok: false, text: errorText(error) });
    } finally {
      setBusy(false);
    }
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (!editing) return;
    const d = editing.draft;
    const body: Record<string, unknown> = {
      name: d.name.trim(),
      active: d.active,
      priority: Number(d.priority),
      syncMinutes: d.syncMinutes,
      attributePriorities: Object.fromEntries(Object.entries(pairs(d.attributePriorities)).map(([k, v]) => [k, Number(v)])),
      settings: settingsOf(d),
    };
    if (connectorKindOf[d.kind]) body.connectorId = d.connectorId || null;
    if (d.credential) body.credential = d.credential;
    await act(async () => {
      if (editing.source) await patchJson(`/v1/cmdb/discovery/sources/${editing.source.id}`, body, csrfToken, { "If-Match": editing.source.etag });
      else await postJson("/v1/cmdb/discovery/sources", { ...body, kind: d.kind }, csrfToken);
      setEditing(null);
    }, t("saved"));
  }

  async function runTest(source: DiscoverySource) {
    setBusy(true);
    setStatus(null);
    try {
      setTest({ id: source.id, result: await postJson<DiscoveryTest>(`/v1/cmdb/discovery/sources/${source.id}/test`, {}, csrfToken) });
    } catch (error) {
      setStatus({ ok: false, text: errorText(error) });
    } finally {
      setBusy(false);
    }
  }

  const set = (patch: Partial<Draft>) => editing && setEditing({ ...editing, draft: { ...editing.draft, ...patch } });
  const d = editing?.draft;
  const connectorKind = d ? connectorKindOf[d.kind] : null;

  return (
    <div className="space-y-4" data-testid="discovery-sources">
      {sources.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("caption")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("name")}</th>
                <th scope="col" className={cellClass}>{t("kind")}</th>
                <th scope="col" className={cellClass}>{t("lastSync")}</th>
                <th scope="col" className={cellClass}>{t("records")}</th>
                <th scope="col" className={cellClass}><span className="sr-only">{t("actions")}</span></th>
              </tr>
            </thead>
            <tbody>
              {sources.map((s) => (
                <tr key={s.id}>
                  <td className={cellClass}>
                    <span className="font-semibold" dir="auto">{s.name}</span>
                    <span className="block text-sm text-ink-muted">{s.active ? t("on") : t("off")} · {t("priorityShort", { priority: number(s.priority) })}</span>
                    {s.connectorName && <span className="block text-sm text-ink-muted" dir="auto">{t("usesConnector", { name: s.connectorName })}</span>}
                  </td>
                  <td className={cellClass}>{t(`kinds.${s.kind}`)}</td>
                  <td className={cellClass}>
                    {s.lastSyncAt ? formatDateTime(new Date(s.lastSyncAt), display) : t("never")}
                    {s.lastSyncSucceeded === false && <span className="block text-sm font-semibold text-bad" dir="ltr">{s.lastError}</span>}
                    {s.lastSyncStats && (
                      <span className="block text-sm text-ink-muted">
                        {t("statsLine", { created: number(s.lastSyncStats.created), updated: number(s.lastSyncStats.updated), review: number(s.lastSyncStats.review) })}
                      </span>
                    )}
                  </td>
                  <td className={cellClass}>
                    {t("recordCounts", { records: number(s.recordCount), missing: number(s.missingCount), review: number(s.reviewCount) })}
                  </td>
                  <td className={cellClass}>
                    <div className="flex flex-wrap gap-2">
                      <button type="button" className={secondaryButton} disabled={busy} onClick={() => setEditing({ source: s, draft: draftOf(s, s.kind) })}>
                        {t("edit")}
                      </button>
                      {s.kind !== "DiscoveryApi" && (
                        <>
                          <button type="button" className={secondaryButton} disabled={busy} onClick={() => void runTest(s)}>{t("test")}</button>
                          <button type="button" className={secondaryButton} disabled={busy || !s.active}
                            onClick={() => void act(() => postJson(`/v1/cmdb/discovery/sources/${s.id}/sync`, {}, csrfToken).then(() => undefined), t("syncRequested"))}>
                            {t("syncNow")}
                          </button>
                        </>
                      )}
                      <button type="button" className={secondaryButton} disabled={busy}
                        onClick={() => {
                          if (window.confirm(t("confirmDelete", { name: s.name }))) void act(() => deleteRequest(`/v1/cmdb/discovery/sources/${s.id}`, csrfToken), t("deleted"));
                        }}>
                        {t("delete")}
                      </button>
                    </div>
                    {s.kind === "DiscoveryApi" && (
                      <p className="mt-2 text-sm">
                        {t("pushAddress")} <code className={codeClass} dir="ltr">POST /api/v1/cmdb/discovery/sources/{s.id}/records</code>
                      </p>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {test && (
        <section aria-labelledby={`${id}-test`} className="space-y-2 rounded-md border border-line p-3" data-testid="discovery-test">
          <h4 id={`${id}-test`} className="font-semibold">{t("testHeading")}</h4>
          {test.result.ok ? (
            <>
              <p>{t("testFound", { count: number(test.result.count) })}</p>
              <ul className="list-disc ps-5">
                {Object.entries(test.result.classes).map(([key, count]) => (
                  <li key={key}><code className={codeClass} dir="ltr">{key}</code>: {number(count)}</li>
                ))}
              </ul>
              {test.result.sample.length > 0 && (
                <p className="text-sm text-ink-muted">{t("testSample", { names: test.result.sample.map((r) => r.name).join(", ") })}</p>
              )}
            </>
          ) : (
            <p className="font-semibold text-bad" dir="ltr">{test.result.error}</p>
          )}
          <button type="button" className={secondaryButton} onClick={() => setTest(null)}>{t("close")}</button>
        </section>
      )}

      <p role="status" aria-live="polite" className={status?.ok === false ? "text-bad" : "text-ok"}>{status?.text ?? ""}</p>

      {!editing ? (
        <div className="flex flex-wrap items-end gap-2">
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("addKind")}</span>
            <select id={`${id}-new-kind`} className={inputClass} value={newKind} onChange={(e) => setNewKind(e.target.value as DiscoverySourceKind)}>
              {discoverySourceKinds.map((k) => <option key={k} value={k}>{t(`kinds.${k}`)}</option>)}
            </select>
          </label>
          <button type="button" className={primaryButton} onClick={() => setEditing({ source: null, draft: draftOf(null, newKind) })}>
            {t("add")}
          </button>
        </div>
      ) : (
        d && (
          <form onSubmit={(e) => void save(e)} className="max-w-3xl space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-form`}>
            <h4 id={`${id}-form`} className="text-lg font-semibold">
              {editing.source ? t("editHeading", { name: editing.source.name }) : t("addHeading", { kind: t(`kinds.${d.kind}`) })}
            </h4>
            <p className="text-sm text-ink-muted">{t(`kindHints.${d.kind}`)}</p>
            <Field id={`${id}-name`} label={t("name")}>
              <input id={`${id}-name`} required maxLength={100} dir="auto" value={d.name} onChange={(e) => set({ name: e.target.value })} className={`${inputClass} w-full max-w-md`} />
            </Field>
            {connectorKind && (
              <Field id={`${id}-connector`} label={t("connector")} hint={t(connectorKind === "EntraId" ? "connectorHintEntra" : "connectorHintLdap")}>
                <select id={`${id}-connector`} required value={d.connectorId} onChange={(e) => set({ connectorId: e.target.value })} className={inputClass}>
                  <option value="">{t("chooseConnector")}</option>
                  {connectors.filter((c) => c.kind === connectorKind).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
            )}
            {d.kind === "Intune" && (
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={d.includeApps} onChange={(e) => set({ includeApps: e.target.checked })} className="size-5 accent-brand" />
                {t("includeApps")}
              </label>
            )}
            {d.kind === "Azure" && (
              <>
                <Field id={`${id}-subs`} label={t("subscriptions")} hint={t("subscriptionsHint")}>
                  <textarea id={`${id}-subs`} rows={3} dir="ltr" value={d.subscriptionIds} onChange={(e) => set({ subscriptionIds: e.target.value })} className={`${inputClass} w-full font-mono`} />
                </Field>
                <Field id={`${id}-types`} label={t("resourceTypes")} hint={t("resourceTypesHint")}>
                  <textarea id={`${id}-types`} rows={4} dir="ltr" value={d.resourceTypes} onChange={(e) => set({ resourceTypes: e.target.value })} className={`${inputClass} w-full font-mono`} />
                </Field>
              </>
            )}
            {d.kind === "ActiveDirectory" && (
              <>
                <Field id={`${id}-base`} label={t("baseDn")} hint={t("baseDnHint")}>
                  <input id={`${id}-base`} dir="ltr" maxLength={500} value={d.baseDn} onChange={(e) => set({ baseDn: e.target.value })} className={`${inputClass} w-full font-mono`} />
                </Field>
                <Field id={`${id}-filter`} label={t("computerFilter")}>
                  <input id={`${id}-filter`} dir="ltr" maxLength={500} value={d.computerFilter} onChange={(e) => set({ computerFilter: e.target.value })} className={`${inputClass} w-full font-mono`} />
                </Field>
              </>
            )}
            {d.kind === "VCenter" && (
              <>
                <Field id={`${id}-url`} label={t("vcenterUrl")}>
                  <input id={`${id}-url`} type="url" dir="ltr" required value={d.url} onChange={(e) => set({ url: e.target.value })} className={`${inputClass} w-full max-w-md`} />
                </Field>
                <Field id={`${id}-user`} label={t("username")}>
                  <input id={`${id}-user`} dir="ltr" required autoComplete="off" value={d.username} onChange={(e) => set({ username: e.target.value })} className={`${inputClass} w-full max-w-md`} />
                </Field>
                <Field id={`${id}-password`} label={t("password")} hint={editing.source?.hasCredential ? t("passwordKept") : t("passwordNeeded")}>
                  <input id={`${id}-password`} type="password" dir="ltr" autoComplete="new-password" value={d.credential} onChange={(e) => set({ credential: e.target.value })}
                    className={`${inputClass} w-full max-w-md`} />
                </Field>
                <Field id={`${id}-thumb`} label={t("thumbprint")} hint={t("thumbprintHint")}>
                  <input id={`${id}-thumb`} dir="ltr" maxLength={100} value={d.thumbprint} onChange={(e) => set({ thumbprint: e.target.value })} className={`${inputClass} w-full font-mono`} />
                </Field>
              </>
            )}
            <div className="flex flex-wrap gap-4">
              <Field id={`${id}-priority`} label={t("priority")} hint={t("priorityHint")}>
                <input id={`${id}-priority`} type="number" min={1} max={100} dir="ltr" value={d.priority} onChange={(e) => set({ priority: e.target.value })} className={`${inputClass} w-28`} />
              </Field>
              {d.kind !== "DiscoveryApi" && (
                <Field id={`${id}-every`} label={t("syncEvery")}>
                  <select id={`${id}-every`} value={d.syncMinutes} onChange={(e) => set({ syncMinutes: Number(e.target.value) })} className={inputClass}>
                    {syncChoices.map((m) => <option key={m} value={m}>{t(`every.${m}`)}</option>)}
                  </select>
                </Field>
              )}
            </div>
            <Field id={`${id}-attr`} label={t("attributePriorities")} hint={t("attributePrioritiesHint")}>
              <textarea id={`${id}-attr`} rows={3} dir="ltr" value={d.attributePriorities} onChange={(e) => set({ attributePriorities: e.target.value })}
                className={`${inputClass} w-full font-mono`} />
            </Field>
            <Field id={`${id}-map`} label={t("classMap")} hint={t("classMapHint")}>
              <textarea id={`${id}-map`} rows={2} dir="ltr" value={d.classMap} onChange={(e) => set({ classMap: e.target.value })} className={`${inputClass} w-full font-mono`} />
            </Field>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={d.active} onChange={(e) => set({ active: e.target.checked })} className="size-5 accent-brand" />
              {t("active")}
            </label>
            <div className="flex flex-wrap gap-2">
              <button type="submit" className={primaryButton} disabled={busy || !d.name.trim()}>{t("save")}</button>
              <button type="button" className={secondaryButton} onClick={() => setEditing(null)}>{t("cancel")}</button>
            </div>
          </form>
        )
      )}
      <details className="max-w-3xl">
        <summary className="cursor-pointer font-semibold">{t("statsHelpTitle")}</summary>
        <dl className="mt-2 grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
          {statKeys.map((k) => (
            <div key={k} className="contents">
              <dt className="font-semibold">{t(`stats.${k}`)}</dt>
              <dd>{t(`statsHelp.${k}`)}</dd>
            </div>
          ))}
        </dl>
      </details>
    </div>
  );
}

function Field({ id, label, hint, children }: { id: string; label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="min-w-0 space-y-1">
      <label htmlFor={id} className="block font-semibold">{label}</label>
      {children}
      {hint && <p className="text-sm text-ink-muted">{hint}</p>}
    </div>
  );
}
