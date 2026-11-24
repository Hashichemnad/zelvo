"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, deleteRequest, postJson, putJson } from "@/lib/client-api";
import type { GroupSummary } from "@/lib/directory";
import { inLanguage, levelName, ticketStates, type IncidentSettings, type Locale, type TicketState } from "@/lib/incidents";
import { builtInViews, sameFilters, type SavedView, type ViewFilters, type WorkspaceScope } from "@/lib/workspace";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";

export interface ViewControlsProps {
  filters: ViewFilters;
  views: SavedView[];
  settings: IncidentSettings | null;
  groups: GroupSummary[];
  csrfToken: string;
  permissions: string[];
  onChange: (filters: ViewFilters) => void;
  onViewsChanged: () => void;
}

/** The workspace's views (built-in and saved) and filters of the incident list. */
export function ViewControls({ filters, views, settings, groups, csrfToken, permissions, onChange, onViewsChanged }: ViewControlsProps) {
  const t = useTranslations("workspace.views");
  const tFilters = useTranslations("incidents.filters");
  const tStates = useTranslations("incidents.states");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const id = useId();
  const active = builtInViews.find((v) => sameFilters(v.filters, filters))?.key
    ?? views.find((v) => sameFilters(v.filters, filters))?.id
    ?? "";
  const activeSaved = views.find((v) => v.id === active);
  const [saving, setSaving] = useState<null | { en: string; ar: string; scope: WorkspaceScope; groupId: string; editing: string | null }>(null);
  const [message, setMessage] = useState<string | null>(null);
  const set = <K extends keyof ViewFilters>(key: K, value: ViewFilters[K] | "") => {
    const next = { ...filters };
    if (value === "" || value === undefined || value === false) delete next[key];
    else next[key] = value as ViewFilters[K];
    onChange(next);
  };

  function pick(key: string) {
    const builtIn = builtInViews.find((v) => v.key === key);
    const saved = views.find((v) => v.id === key);
    onChange(builtIn?.filters ?? saved?.filters ?? {});
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!saving) return;
    const body = {
      name: { en: saving.en.trim() || null, ar: saving.ar.trim() || null },
      scope: saving.scope,
      groupId: saving.scope === "Group" ? saving.groupId || null : null,
      filters,
    };
    try {
      if (saving.editing) await putJson(`/v1/workspace/views/${saving.editing}`, body, csrfToken);
      else await postJson("/v1/workspace/views", body, csrfToken);
      setSaving(null);
      setMessage(t("saved"));
      onViewsChanged();
    } catch (error) {
      setMessage(error instanceof ApiError && error.rateLimited ? tCommon("rateLimited") : error instanceof ApiError && error.status === 400 ? t("refused") : t("failed"));
    }
  }

  async function remove(view: SavedView) {
    try {
      await deleteRequest(`/v1/workspace/views/${view.id}`, csrfToken);
      setMessage(t("removed"));
      onChange({ open: true, assignee: "me", sort: "due" });
      onViewsChanged();
    } catch {
      setMessage(t("failed"));
    }
  }

  const select = `${inputClass} w-full`;
  const mayShareAll = permissions.includes("workspace.views.share");
  return (
    <div className="space-y-3">
      <label className="block space-y-1 text-sm">
        <span className="block font-semibold">{t("label")}</span>
        <select id="workspace-view" value={active} onChange={(e) => pick(e.target.value)} className={select} data-testid="view-picker">
          {!active && <option value="">{t("custom")}</option>}
          <optgroup label={t("builtInGroup")}>
            {builtInViews.map((v) => <option key={v.key} value={v.key}>{t(`builtIn.${v.key}`)}</option>)}
          </optgroup>
          {views.length > 0 && (
            <optgroup label={t("savedGroup")}>
              {views.map((v) => {
                const name = inLanguage(v.name, locale);
                return <option key={v.id} value={v.id} lang={name.lang}>{name.value}{v.scope !== "Personal" ? ` · ${t(`scopes.${v.scope}`)}` : ""}</option>;
              })}
            </optgroup>
          )}
        </select>
      </label>
      <details className="rounded-lg border border-line p-2 text-sm">
        <summary className="cursor-pointer font-semibold">{t("filters")}</summary>
        <div className="mt-2 grid gap-2">
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("state")}</span>
            <select value={filters.state ?? ""} onChange={(e) => set("state", e.target.value as TicketState | "")} className={select}>
              <option value="">{tFilters("anyState")}</option>
              {ticketStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
            </select>
          </label>
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("priority")}</span>
            <select value={filters.priority ?? ""} onChange={(e) => set("priority", e.target.value ? Number(e.target.value) : "")} className={select}>
              <option value="">{tFilters("anyPriority")}</option>
              {settings?.priorityMatrix.priorities.map((p) => (
                <option key={p.level} value={p.level}>{levelName(settings.priorityMatrix.priorities, p.level, locale)}</option>
              ))}
            </select>
          </label>
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("assignee")}</span>
            <select value={filters.assignee ?? ""} onChange={(e) => set("assignee", e.target.value)} className={select}>
              <option value="">{tFilters("anyone")}</option>
              <option value="me">{tFilters("me")}</option>
              <option value="none">{tFilters("nobody")}</option>
            </select>
          </label>
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("group")}</span>
            <select value={filters.group ?? ""} onChange={(e) => set("group", e.target.value)} className={select}>
              <option value="">{tFilters("anyGroup")}</option>
              <option value="mine">{tFilters("myGroups")}</option>
              <option value="none">{tFilters("noGroup")}</option>
              {groups.map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
            </select>
          </label>
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("sla")}</span>
            <select value={filters.sla ?? ""} onChange={(e) => set("sla", e.target.value as ViewFilters["sla"] | "")} className={select}>
              <option value="">{tFilters("anySla")}</option>
              <option value="atRisk">{tFilters("slaAtRisk")}</option>
              <option value="breached">{tFilters("slaBreached")}</option>
            </select>
          </label>
          <label className="space-y-1">
            <span className="block font-semibold">{tFilters("sort")}</span>
            <select value={filters.sort ?? ""} onChange={(e) => set("sort", e.target.value as ViewFilters["sort"] | "")} className={select}>
              <option value="">{tFilters("sortNewest")}</option>
              <option value="due">{tFilters("sortDue")}</option>
            </select>
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={filters.open === true} onChange={(e) => set("open", e.target.checked)} />
            {tFilters("openOnly")}
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={filters.major === true} onChange={(e) => set("major", e.target.checked)} />
            {tFilters("major")}
          </label>
        </div>
      </details>
      <div className="flex flex-wrap gap-2 text-sm">
        <button type="button" className={secondaryButtonClass} onClick={() => setSaving({ en: "", ar: "", scope: "Personal", groupId: "", editing: null })}>
          {t("save")}
        </button>
        {activeSaved?.canEdit && (
          <>
            <button type="button" className={secondaryButtonClass} onClick={() => setSaving({
              en: activeSaved.name.en ?? "", ar: activeSaved.name.ar ?? "", scope: activeSaved.scope, groupId: activeSaved.group?.id ?? "", editing: activeSaved.id,
            })}>
              {t("edit")}
            </button>
            <button type="button" className={secondaryButtonClass} onClick={() => void remove(activeSaved)}>{t("remove")}</button>
          </>
        )}
      </div>
      {saving && (
        <form onSubmit={save} className="space-y-2 rounded-lg border border-line p-2 text-sm" aria-label={t("saveForm")}>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("nameEn")}</span>
            <input value={saving.en} onChange={(e) => setSaving({ ...saving, en: e.target.value })} lang="en" dir="ltr" maxLength={100} className={select} />
          </label>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("nameAr")}</span>
            <input value={saving.ar} onChange={(e) => setSaving({ ...saving, ar: e.target.value })} lang="ar" dir="rtl" maxLength={100} className={select} />
          </label>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("scope")}</span>
            <select value={saving.scope} onChange={(e) => setSaving({ ...saving, scope: e.target.value as WorkspaceScope })} className={select}>
              <option value="Personal">{t("scopes.Personal")}</option>
              <option value="Group">{t("scopes.Group")}</option>
              {mayShareAll && <option value="Tenant">{t("scopes.Tenant")}</option>}
            </select>
          </label>
          {saving.scope === "Group" && (
            <label className="block space-y-1">
              <span className="block font-semibold">{t("group")}</span>
              <select value={saving.groupId} onChange={(e) => setSaving({ ...saving, groupId: e.target.value })} required className={select}>
                <option value="">{t("chooseGroup")}</option>
                {groups.map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
              </select>
            </label>
          )}
          <p className="text-xs text-ink-muted">{t("shareHint")}</p>
          <div className="flex gap-2">
            <button type="submit" className="rounded-md bg-brand px-3 py-1.5 font-semibold text-surface">{t("confirmSave")}</button>
            <button type="button" className={secondaryButtonClass} onClick={() => setSaving(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <p aria-live="polite" className="text-sm text-ink-muted" id={`${id}-message`}>{message}</p>
    </div>
  );
}
