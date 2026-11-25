"use client";

import { useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { AlertFormat, MonitoringAlert, MonitoringSource } from "@/lib/connectors";
import type { Page } from "@/lib/channels";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/i18n/routing";
import { cellClass, codeClass, inputClass, primaryButton, secondaryButton, tableClass } from "../settings/integrations/ui";

type Preset = "alertmanager" | "zabbix" | "datadog";

/**
 * Settings → Monitoring (module 14, task E7): monitoring tools that send alerts. A new alert raises
 * an incident, the same alert again adds to it, a cleared alert adds a note and can resolve it.
 * The token is shown once, right after it is made.
 */
export function MonitoringPanel({ initial, csrfToken, categories, groups, display }: {
  initial: MonitoringSource[]; csrfToken: string; categories: TreeNode[]; groups: GroupSummary[]; display: DateDisplayOptions;
}) {
  const t = useTranslations("connectors.monitoring");
  const locale = useLocale() as Locale;
  const [sources, setSources] = useState(initial);
  const [token, setToken] = useState<{ id: string; token: string } | null>(null);
  const [alerts, setAlerts] = useState<{ id: string; items: MonitoringAlert[] } | null>(null);
  const [name, setName] = useState("");
  const [preset, setPreset] = useState<Preset>("alertmanager");
  const [groupId, setGroupId] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [resolveOnClear, setResolveOnClear] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function add(event: FormEvent) {
    event.preventDefault();
    setError(null);
    const format: AlertFormat = preset === "alertmanager" ? "Alertmanager" : "Generic";
    try {
      const created = await postJson<{ source: MonitoringSource; token: string }>("/v1/monitoring/sources", {
        name, format, preset: format === "Generic" ? preset : null, groupId: groupId || null, categoryId: categoryId || null, resolveOnClear,
      }, csrfToken);
      setSources([...sources, created.source]);
      setToken({ id: created.source.id, token: created.token });
      setName("");
    } catch (e) {
      setError(e instanceof ApiError && e.status === 400 ? t("invalid") : t("error"));
    }
  }

  async function renew(id: string) {
    const renewed = await postJson<{ source: MonitoringSource; token: string }>(`/v1/monitoring/sources/${id}/token`, {}, csrfToken);
    setToken({ id, token: renewed.token });
  }

  async function toggle(source: MonitoringSource, field: "active" | "resolveOnClear") {
    const saved = await patchJson<MonitoringSource>(`/v1/monitoring/sources/${source.id}`, { [field]: !source[field] }, csrfToken);
    setSources(sources.map((s) => (s.id === saved.id ? saved : s)));
  }

  async function showAlerts(id: string) {
    setAlerts({ id, items: (await getJson<Page<MonitoringAlert>>(`/v1/monitoring/sources/${id}/alerts?pageSize=50`)).items });
  }

  return (
    <div className="space-y-6" data-testid="monitoring-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {sources.length === 0 && <p>{t("empty")}</p>}
      {sources.map((s) => (
        <section key={s.id} className="space-y-2 rounded-md border border-line p-4" aria-label={s.name} data-testid="monitoring-source">
          <h3 className="text-lg font-semibold">{s.name}</h3>
          <p className="text-sm text-ink-muted">
            {t(`formats.${s.format}`)} · {s.lastAlertAt ? t("lastAlert", { when: formatDateTime(new Date(s.lastAlertAt), display) }) : t("noAlerts")}
          </p>
          <p className="text-sm">{t("webhook")}</p>
          <code dir="ltr" className={codeClass}>{s.webhookUrl}</code>
          {token?.id === s.id && (
            <div role="status" className="space-y-1 rounded-md border border-warn p-2">
              <p className="font-semibold">{t("tokenOnce")}</p>
              <code dir="ltr" className={codeClass} data-testid="monitoring-token">{token.token}</code>
            </div>
          )}
          <div className="flex flex-wrap gap-3">
            <label className="flex items-center gap-2"><input type="checkbox" checked={s.active} onChange={() => toggle(s, "active")} />{t("active")}</label>
            <label className="flex items-center gap-2"><input type="checkbox" checked={s.resolveOnClear} onChange={() => toggle(s, "resolveOnClear")} />{t("resolveOnClear")}</label>
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="button" className={secondaryButton} onClick={() => renew(s.id)}>{t("renew")}</button>
            <button type="button" className={secondaryButton} onClick={() => showAlerts(s.id)}>{t("showAlerts")}</button>
          </div>
          {alerts?.id === s.id && (
            <div className="overflow-x-auto">
              <table className={tableClass}>
                <caption className="text-start font-semibold">{t("alerts.title")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("alerts.ticket")}</th>
                    <th scope="col" className={cellClass}>{t("alerts.state")}</th>
                    <th scope="col" className={cellClass}>{t("alerts.count")}</th>
                    <th scope="col" className={cellClass}>{t("alerts.last")}</th>
                  </tr>
                </thead>
                <tbody>
                  {alerts.items.length === 0 && <tr><td colSpan={4} className={cellClass}>{t("noAlerts")}</td></tr>}
                  {alerts.items.map((a) => (
                    <tr key={a.id}>
                      <td className={cellClass}><span dir="ltr">{a.ticketNumber}</span> <span dir="auto">{a.ticketTitle}</span></td>
                      <td className={cellClass}>{t(`alerts.states.${a.state}`)}</td>
                      <td className={cellClass}>{a.count}</td>
                      <td className={cellClass}>{formatDateTime(new Date(a.lastSeenAt), display)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      ))}

      <form onSubmit={add} className="max-w-2xl space-y-3 rounded-md border border-line p-4" aria-labelledby="add-source-heading" noValidate>
        <h3 id="add-source-heading" className="text-lg font-semibold">{t("add")}</h3>
        <label className="block">
          <span className="block font-semibold">{t("name")}</span>
          <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("tool")}</legend>
          {(["alertmanager", "zabbix", "datadog"] as const).map((p) => (
            <label key={p} className="flex items-center gap-2">
              <input type="radio" name="monitoring-preset" checked={preset === p} onChange={() => setPreset(p)} />
              {t(`presets.${p}`)}
            </label>
          ))}
          <p className="text-sm text-ink-muted">{t(`presetHints.${preset}`)}</p>
        </fieldset>
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="block">
            <span className="block font-semibold">{t("group")}</span>
            <select className={inputClass} value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              <option value="">{t("byRules")}</option>
              {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
            </select>
          </label>
          <label className="block">
            <span className="block font-semibold">{t("category")}</span>
            <select className={inputClass} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">{t("none")}</option>
              {selectableNodes(categories).map((c) => <option key={c.id} value={c.id}>{textOf(c.path, locale)}</option>)}
            </select>
          </label>
        </div>
        <label className="flex items-center gap-2"><input type="checkbox" checked={resolveOnClear} onChange={(e) => setResolveOnClear(e.target.checked)} />{t("resolveOnClear")}</label>
        {error && <p role="alert" className="text-bad">{error}</p>}
        <button type="submit" className={primaryButton}>{t("add")}</button>
      </form>
    </div>
  );
}
