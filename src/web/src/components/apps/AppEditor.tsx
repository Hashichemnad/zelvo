"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, SaveStatusLine, smallButton, useSaver } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { buttonClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { appKeyPattern, type App, type AppDefinition, type AppListView, type AppMenuEntry, type AppPortalEntry, type DataList, type Text } from "@/lib/apps";
import { iconGlyph, serviceIcons } from "@/lib/catalog";
import { patchJson, postJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { RecordType } from "@/lib/records";

export interface RoleChoice {
  key: string;
  name: { en: string | null; ar: string | null };
  isSystem: boolean;
}

const empty = (value: Text | null | undefined) => ({ en: value?.en ?? "", ar: value?.ar ?? "" });
const clean = (value: { en: string; ar: string }): Text => ({ en: value.en.trim() || null, ar: value.ar.trim() || null });

/** Why an app's entries cannot be saved, as a message key, or null. */
export function definitionProblem(definition: AppDefinition, types: RecordType[]): string | null {
  const known = new Map(types.map((t) => [t.key, t]));
  const groups: { key: string; name: Text; recordType: string }[][] = [
    definition.listViews.map((v) => ({ key: v.key, name: v.name, recordType: v.recordType })),
    definition.menuEntries.map((e) => ({ key: e.key, name: e.label, recordType: e.recordType })),
    definition.portalEntries.map((e) => ({ key: e.key, name: e.label, recordType: e.recordType })),
  ];
  for (const entries of groups) {
    if (entries.some((e) => !appKeyPattern.test(e.key))) return "entryKey";
    if (new Set(entries.map((e) => e.key)).size !== entries.length) return "entryKeyTwice";
    if (entries.some((e) => !e.name.en?.trim() && !e.name.ar?.trim())) return "entryName";
    if (entries.some((e) => !known.has(e.recordType))) return "entryType";
  }
  if (definition.listViews.some((v) => v.state && !known.get(v.recordType)?.states.some((s) => s.key === v.state))) return "viewState";
  if (definition.menuEntries.some((e) => e.view && !definition.listViews.some((v) => v.key === e.view && v.recordType === e.recordType))) return "menuView";
  return null;
}

/**
 * One app in Settings → Apps (task K3): its names and icon, the record types, data lists and custom
 * roles it groups (forms and workflow rules come with its record types), its list views, menu
 * entries and portal entries, switching it off and on, and exporting it as a package.
 */
export function AppEditor({ app, types, lists, roles, csrfToken }: {
  app: App;
  types: RecordType[];
  lists: DataList[];
  roles: RoleChoice[];
  csrfToken: string;
}) {
  const t = useTranslations("apps.editor");
  const tIcons = useTranslations("catalog.editor.icons");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [name, setName] = useState(empty(app.name));
  const [description, setDescription] = useState(empty(app.description));
  const [icon, setIcon] = useState(app.icon);
  const [memberTypes, setMemberTypes] = useState(app.members?.recordTypes.map((m) => m.key) ?? []);
  const [memberLists, setMemberLists] = useState(app.members?.dataLists.map((m) => m.key) ?? []);
  const [memberRoles, setMemberRoles] = useState(app.members?.roles.map((m) => m.key) ?? []);
  const [views, setViews] = useState<AppListView[]>(app.listViews);
  const [menu, setMenu] = useState<AppMenuEntry[]>(app.menuEntries);
  const [portal, setPortal] = useState<AppPortalEntry[]>(app.portalEntries);
  const chosenTypes = types.filter((type) => memberTypes.includes(type.key));
  const definition: AppDefinition = { listViews: views, menuEntries: menu, portalEntries: portal };
  const problem = !name.en.trim() && !name.ar.trim() ? "name" : definitionProblem(definition, chosenTypes);
  const firstType = chosenTypes[0]?.key ?? "";

  function toggle(list: string[], set: (value: string[]) => void, key: string, on: boolean) {
    set(on ? [...list, key] : list.filter((k) => k !== key));
  }

  function save() {
    void run(async () => {
      await patchJson(`/v1/apps/${app.key}`, {
        name: clean(name), description: clean(description), icon,
        recordTypes: memberTypes, dataLists: memberLists, roles: memberRoles,
        definition: {
          listViews: views.map((v) => ({ ...v, name: clean(empty(v.name)), state: v.state || null, assignee: v.assignee || null, open: v.open || null })),
          menuEntries: menu.map((e) => ({ ...e, label: clean(empty(e.label)), view: e.target === "records" && e.view ? e.view : null })),
          portalEntries: portal.map((e) => ({ ...e, label: clean(empty(e.label)), description: clean(empty(e.description)) })),
        },
      }, csrfToken, { "If-Match": app.etag });
      router.refresh();
    });
  }

  function switchApp() {
    void run(async () => {
      await postJson(`/v1/apps/${app.key}/${app.active ? "deactivate" : "activate"}`, undefined, csrfToken);
      router.refresh();
    });
  }

  const typeSelect = (value: string, onChange: (value: string) => void, label: string) => (
    <label className="space-y-1 text-sm">
      <span className="block">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)} className={fieldInput}>
        {chosenTypes.map((type) => <option key={type.key} value={type.key}>{inLanguage(type.name, locale).value}</option>)}
      </select>
    </label>
  );

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-center gap-3">
        <span className={app.active ? "font-semibold text-ok" : "font-semibold text-ink-muted"}>{app.active ? t("active") : t("inactive")}</span>
        <button type="button" className={secondaryButtonClass} disabled={busy} onClick={switchApp}>{app.active ? t("deactivate") : t("activate")}</button>
        <a href={`/bff/api/v1/apps/${app.key}/export`} download={`${app.key}.zelvo-app.json`} className={secondaryButtonClass}>{t("export")}</a>
      </div>
      <p className="max-w-2xl text-sm text-ink-muted">{app.active ? t("deactivateHint") : t("activateHint")}</p>

      <form className="space-y-8" aria-labelledby={`${id}-heading`} onSubmit={(e) => (e.preventDefault(), !problem && save())}>
        <h3 id={`${id}-heading`} className="sr-only">{t("heading")}</h3>
        <section className="space-y-3">
          <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} />
          <BilingualInputs id={`${id}-description`} label={t("description")} maxLength={1000} en={description.en} ar={description.ar} onChange={setDescription} />
          <label className="block max-w-sm space-y-1">
            <span className="block font-semibold">{t("icon")}</span>
            <select value={icon} onChange={(e) => setIcon(e.target.value)} className={fieldInput}>
              {serviceIcons.map((i) => <option key={i} value={i}>{`${iconGlyph[i] ?? ""} ${tIcons.has(i) ? tIcons(i) : i}`}</option>)}
            </select>
          </label>
        </section>

        <section className="grid gap-4 md:grid-cols-3">
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("recordTypes")}</legend>
            {types.length === 0 && <p className="text-sm">{t("noTypes")}</p>}
            {types.map((type) => (
              <label key={type.key} className="flex items-center gap-2 text-sm">
                <input type="checkbox" className="size-5 accent-brand" checked={memberTypes.includes(type.key)} onChange={(e) => toggle(memberTypes, setMemberTypes, type.key, e.target.checked)} />
                {inLanguage(type.name, locale).value}
              </label>
            ))}
          </fieldset>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("dataLists")}</legend>
            {lists.length === 0 && <p className="text-sm">{t("noLists")}</p>}
            {lists.map((list) => (
              <label key={list.key} className="flex items-center gap-2 text-sm">
                <input type="checkbox" className="size-5 accent-brand" checked={memberLists.includes(list.key)} onChange={(e) => toggle(memberLists, setMemberLists, list.key, e.target.checked)} />
                {inLanguage(list.name, locale).value}
              </label>
            ))}
          </fieldset>
          <fieldset className="space-y-1">
            <legend className="font-semibold">{t("roles")}</legend>
            {roles.filter((r) => !r.isSystem).length === 0 && <p className="text-sm">{t("noRoles")}</p>}
            {roles.filter((r) => !r.isSystem).map((role) => (
              <label key={role.key} className="flex items-center gap-2 text-sm">
                <input type="checkbox" className="size-5 accent-brand" checked={memberRoles.includes(role.key)} onChange={(e) => toggle(memberRoles, setMemberRoles, role.key, e.target.checked)} />
                {(locale === "ar" ? role.name.ar ?? role.name.en : role.name.en ?? role.name.ar) ?? role.key}
              </label>
            ))}
          </fieldset>
        </section>
        <p className="text-sm text-ink-muted">{t("membersHint")}</p>
        {app.members && (app.members.forms.length > 0 || app.members.rules.length > 0) && (
          <section className="space-y-1 text-sm">
            <h4 className="font-semibold">{t("comesWith")}</h4>
            <p>{t("forms")}: {app.members.forms.map((f) => inLanguage(f.name, locale).value).join(locale === "ar" ? "، " : ", ") || "—"}</p>
            <p>{t("rules")}: {app.members.rules.map((r) => inLanguage(r.name, locale).value).join(locale === "ar" ? "، " : ", ") || "—"}</p>
          </section>
        )}

        {chosenTypes.length === 0 ? <p>{t("entriesNeedTypes")}</p> : (
          <>
            <fieldset className="space-y-2">
              <legend className="text-lg font-semibold">{t("listViews")}</legend>
              <p className="text-sm text-ink-muted">{t("listViewsHint")}</p>
              {views.map((view, i) => {
                const type = chosenTypes.find((x) => x.key === view.recordType);
                const set = (change: Partial<AppListView>) => setViews(views.map((v, j) => (j === i ? { ...v, ...change } : v)));
                return (
                  <div key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-3 sm:items-end">
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("entryKey", { n: i + 1 })}</span>
                      <input dir="ltr" value={view.key} maxLength={40} className={fieldInput} onChange={(e) => set({ key: e.target.value.toLowerCase() })} />
                    </label>
                    <BilingualInputs id={`${id}-view-${i}`} label={t("viewName", { n: i + 1 })} en={view.name.en ?? ""} ar={view.name.ar ?? ""} onChange={(v) => set({ name: v })} />
                    {typeSelect(view.recordType, (recordType) => set({ recordType, state: null }), t("recordType", { n: i + 1 }))}
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("viewState", { n: i + 1 })}</span>
                      <select value={view.state ?? ""} onChange={(e) => set({ state: e.target.value || null })} className={fieldInput}>
                        <option value="">{t("anyState")}</option>
                        {(type?.states ?? []).map((s) => <option key={s.key} value={s.key}>{inLanguage(s.name, locale).value}</option>)}
                      </select>
                    </label>
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("viewAssignee", { n: i + 1 })}</span>
                      <select value={view.assignee ?? ""} onChange={(e) => set({ assignee: (e.target.value || null) as AppListView["assignee"] })} className={fieldInput}>
                        <option value="">{t("anyone")}</option>
                        <option value="me">{t("me")}</option>
                        <option value="none">{t("nobody")}</option>
                      </select>
                    </label>
                    <div className="flex flex-wrap items-center gap-2">
                      <label className="flex items-center gap-2 text-sm">
                        <input type="checkbox" className="size-5 accent-brand" checked={!!view.open} onChange={(e) => set({ open: e.target.checked })} />
                        {t("viewOpen", { n: i + 1 })}
                      </label>
                      <button type="button" className={smallButton} onClick={() => setViews(views.filter((_, j) => j !== i))}>{t("removeEntry", { n: i + 1 })}</button>
                    </div>
                  </div>
                );
              })}
              <button type="button" className={smallButton}
                onClick={() => setViews([...views, { key: `view-${views.length + 1}`, name: { en: "", ar: "" }, recordType: firstType, open: true }])}>{t("addView")}</button>
            </fieldset>

            <fieldset className="space-y-2">
              <legend className="text-lg font-semibold">{t("menuEntries")}</legend>
              <p className="text-sm text-ink-muted">{t("menuEntriesHint")}</p>
              {menu.map((entry, i) => {
                const set = (change: Partial<AppMenuEntry>) => setMenu(menu.map((e, j) => (j === i ? { ...e, ...change } : e)));
                return (
                  <div key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-3 sm:items-end">
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("entryKey", { n: i + 1 })}</span>
                      <input dir="ltr" value={entry.key} maxLength={40} className={fieldInput} onChange={(e) => set({ key: e.target.value.toLowerCase() })} />
                    </label>
                    <BilingualInputs id={`${id}-menu-${i}`} label={t("entryLabel", { n: i + 1 })} en={entry.label.en ?? ""} ar={entry.label.ar ?? ""} onChange={(v) => set({ label: v })} />
                    {typeSelect(entry.recordType, (recordType) => set({ recordType, view: null }), t("recordType", { n: i + 1 }))}
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("menuTarget", { n: i + 1 })}</span>
                      <select value={entry.target} onChange={(e) => set({ target: e.target.value as AppMenuEntry["target"] })} className={fieldInput}>
                        <option value="records">{t("targetRecords")}</option>
                        <option value="new">{t("targetNew")}</option>
                      </select>
                    </label>
                    {entry.target === "records" && (
                      <label className="space-y-1 text-sm">
                        <span className="block">{t("menuView", { n: i + 1 })}</span>
                        <select value={entry.view ?? ""} onChange={(e) => set({ view: e.target.value || null })} className={fieldInput}>
                          <option value="">{t("allRecords")}</option>
                          {views.filter((v) => v.recordType === entry.recordType).map((v) => <option key={v.key} value={v.key}>{inLanguage(v.name, locale).value || v.key}</option>)}
                        </select>
                      </label>
                    )}
                    <button type="button" className={smallButton} onClick={() => setMenu(menu.filter((_, j) => j !== i))}>{t("removeEntry", { n: i + 1 })}</button>
                  </div>
                );
              })}
              <button type="button" className={smallButton}
                onClick={() => setMenu([...menu, { key: `entry-${menu.length + 1}`, label: { en: "", ar: "" }, target: "records", recordType: firstType }])}>{t("addMenuEntry")}</button>
            </fieldset>

            <fieldset className="space-y-2">
              <legend className="text-lg font-semibold">{t("portalEntries")}</legend>
              <p className="text-sm text-ink-muted">{t("portalEntriesHint")}</p>
              {portal.map((entry, i) => {
                const set = (change: Partial<AppPortalEntry>) => setPortal(portal.map((e, j) => (j === i ? { ...e, ...change } : e)));
                return (
                  <div key={i} className="grid gap-2 rounded-md border border-line p-2 sm:grid-cols-2 sm:items-end">
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("entryKey", { n: i + 1 })}</span>
                      <input dir="ltr" value={entry.key} maxLength={40} className={fieldInput} onChange={(e) => set({ key: e.target.value.toLowerCase() })} />
                    </label>
                    {typeSelect(entry.recordType, (recordType) => set({ recordType }), t("recordType", { n: i + 1 }))}
                    <BilingualInputs id={`${id}-portal-${i}`} label={t("entryLabel", { n: i + 1 })} en={entry.label.en ?? ""} ar={entry.label.ar ?? ""} onChange={(v) => set({ label: v })} />
                    <BilingualInputs id={`${id}-portal-d-${i}`} label={t("entryDescription", { n: i + 1 })} maxLength={1000} en={entry.description.en ?? ""} ar={entry.description.ar ?? ""}
                      onChange={(v) => set({ description: v })} />
                    <button type="button" className={smallButton} onClick={() => setPortal(portal.filter((_, j) => j !== i))}>{t("removeEntry", { n: i + 1 })}</button>
                  </div>
                );
              })}
              <button type="button" className={smallButton}
                onClick={() => setPortal([...portal, { key: `portal-${portal.length + 1}`, label: { en: "", ar: "" }, description: { en: "", ar: "" }, recordType: firstType }])}>{t("addPortalEntry")}</button>
            </fieldset>
          </>
        )}
        {problem && <p className="text-sm font-semibold text-bad">{t(`problems.${problem}`)}</p>}
        <div className="flex flex-wrap gap-2">
          <button type="submit" className={buttonClass} disabled={busy || !!problem}>{t("save")}</button>
        </div>
      </form>
      <SaveStatusLine status={status} />
    </div>
  );
}
