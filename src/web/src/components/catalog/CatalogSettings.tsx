"use client";

import { useId, useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { BilingualInputs, Localized, cellClass, smallButton, tableClass, useSaver } from "@/components/directory/common";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import { textOf, toInput, type Department, type GroupSummary } from "@/lib/directory";
import { serviceStates, type Catalog, type ServiceState, type ServiceSummary, type ServiceTemplate } from "@/lib/catalog";
import type { Locale, Person } from "@/lib/incidents";
import { CatalogStatusLine, ServiceStateBadge } from "./common";

export interface CatalogSettingsProps {
  services: ServiceSummary[];
  catalogs: Catalog[];
  /** Versions waiting for the caller's review; null without catalog.publish. */
  reviewQueue: ServiceSummary[] | null;
  /** Ready-made and saved templates; null when the caller may not create services. */
  templates: ServiceTemplate[] | null;
  departments: Department[];
  groups: GroupSummary[];
  permissions: string[];
  csrfToken: string;
}

/**
 * Settings → Catalog (task C2): services with their state and versions, the catalogs and their
 * editors, the review queue and the templates. Services open in the service editor.
 */
export function CatalogSettings({ services, catalogs, reviewQueue, templates, departments, groups, permissions, csrfToken }: CatalogSettingsProps) {
  const t = useTranslations("catalog.settings");
  const manager = permissions.includes("catalog.manage");
  const mayCreate = manager || catalogs.some((c) => c.youEdit);
  return (
    <div className="space-y-10">
      <ServicesSection services={services} catalogs={catalogs} templates={templates} mayCreate={mayCreate} manager={manager} csrfToken={csrfToken} />
      {reviewQueue && (
        <section aria-labelledby="catalog-review-heading" className="space-y-3">
          <h3 id="catalog-review-heading" className="text-xl font-semibold">{t("reviewTitle")}</h3>
          {reviewQueue.length === 0 ? <p>{t("reviewEmpty")}</p> : <ServiceTable services={reviewQueue} catalogs={catalogs} />}
        </section>
      )}
      <CatalogsSection catalogs={catalogs} departments={departments} groups={groups} manager={manager} csrfToken={csrfToken} />
      {templates && manager && <TemplatesSection templates={templates} csrfToken={csrfToken} />}
    </div>
  );
}

function ServiceTable({ services, catalogs }: { services: ServiceSummary[]; catalogs: Catalog[] }) {
  const t = useTranslations("catalog.settings");
  const locale = useLocale() as Locale;
  const names = new Map(catalogs.map((c) => [c.id, c.name]));
  return (
    <table className={tableClass} data-testid="catalog-services">
      <thead>
        <tr>
          <th scope="col" className={cellClass}>{t("code")}</th>
          <th scope="col" className={cellClass}>{t("name")}</th>
          <th scope="col" className={cellClass}>{t("state")}</th>
          <th scope="col" className={cellClass}>{t("catalogs")}</th>
          <th scope="col" className={cellClass}>{t("versions")}</th>
        </tr>
      </thead>
      <tbody>
        {services.map((s) => (
          <tr key={s.id}>
            <td className={cellClass}>
              <Link href={`/settings/catalog/services/${s.id}`} className="font-mono font-semibold text-brand underline" dir="ltr">{s.code}</Link>
            </td>
            <td className={cellClass}><Localized text={s.name} locale={locale} /></td>
            <td className={cellClass}><ServiceStateBadge state={s.state} /></td>
            <td className={cellClass}>{s.catalogIds.map((c) => textOf(names.get(c), locale)).filter(Boolean).join(locale === "ar" ? "، " : ", ")}</td>
            <td className={cellClass}>
              {s.publishedVersion !== null && <span className="block">{t("publishedVersion", { number: s.publishedVersion })}</span>}
              {s.workingVersion !== null && (
                <span className="block text-sm text-ink-muted">
                  {t(s.workingState === "UnderReview" ? "versionInReview" : "versionDraft", { number: s.workingVersion })}
                </span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function ServicesSection({ services, catalogs, templates, mayCreate, manager, csrfToken }: {
  services: ServiceSummary[]; catalogs: Catalog[]; templates: ServiceTemplate[] | null; mayCreate: boolean; manager: boolean; csrfToken: string;
}) {
  const t = useTranslations("catalog.settings");
  const tStates = useTranslations("catalog.states");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [state, setState] = useState<ServiceState | "">("");
  const [catalogId, setCatalogId] = useState("");
  const [q, setQ] = useState("");
  const [code, setCode] = useState("");
  const [templateId, setTemplateId] = useState("");
  const [newCatalog, setNewCatalog] = useState(manager ? "" : (catalogs.find((c) => c.youEdit)?.id ?? ""));
  const { status, run, busy } = useSaver();
  const shown = useMemo(() => {
    const text = q.trim().toLowerCase();
    return services.filter((s) =>
      (!state || s.state === state) && (!catalogId || s.catalogIds.includes(catalogId))
      && (!text || s.code.toLowerCase().includes(text) || (s.name.en ?? "").toLowerCase().includes(text) || (s.name.ar ?? "").includes(q.trim())));
  }, [services, state, catalogId, q]);
  const editable = manager ? catalogs : catalogs.filter((c) => c.youEdit);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4">
      <h3 id={`${id}-heading`} className="text-xl font-semibold">{t("servicesTitle")}</h3>
      {mayCreate && (
        <form className="space-y-3 rounded-lg border border-line p-3" aria-label={t("create")}
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              const created = await postJson<{ id: string }>("/v1/catalog/services", {
                code: code.trim().toUpperCase(), templateId: templateId || undefined, catalogIds: newCatalog ? [newCatalog] : undefined,
              }, csrfToken);
              router.push(`/settings/catalog/services/${created.id}`);
            });
          }}>
          <h4 className="font-semibold">{t("create")}</h4>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="space-y-1">
              <label htmlFor={`${id}-code`} className="block text-sm font-semibold">{t("code")}</label>
              <input id={`${id}-code`} value={code} onChange={(e) => setCode(e.target.value)} required maxLength={30} dir="ltr"
                aria-describedby={`${id}-code-hint`} className={`${fieldInput} font-mono uppercase`} />
              <p id={`${id}-code-hint`} className="text-sm text-ink-muted">{t("codeHint")}</p>
            </div>
            {templates && (
              <div className="space-y-1">
                <label htmlFor={`${id}-template`} className="block text-sm font-semibold">{t("template")}</label>
                <select id={`${id}-template`} value={templateId} onChange={(e) => setTemplateId(e.target.value)} className={fieldInput}>
                  <option value="">{t("noTemplate")}</option>
                  {templates.map((tpl) => (
                    <option key={tpl.id} value={tpl.id}>{textOf(tpl.name, locale)}{tpl.builtIn ? ` (${t("builtIn")})` : ""}</option>
                  ))}
                </select>
              </div>
            )}
            <div className="space-y-1">
              <label htmlFor={`${id}-catalog`} className="block text-sm font-semibold">{t("catalog")}</label>
              <select id={`${id}-catalog`} value={newCatalog} onChange={(e) => setNewCatalog(e.target.value)} className={fieldInput} required={!manager}>
                {manager && <option value="">{t("noCatalog")}</option>}
                {editable.map((c) => <option key={c.id} value={c.id}>{textOf(c.name, locale)}</option>)}
              </select>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button type="submit" className={primaryAction} disabled={busy || code.trim().length < 2}>{t("createButton")}</button>
            <CatalogStatusLine status={status} />
          </div>
        </form>
      )}
      <div className="flex flex-wrap items-end gap-3" role="search" aria-label={t("filters")}>
        <div className="space-y-1">
          <label htmlFor={`${id}-q`} className="block text-sm">{t("search")}</label>
          <input id={`${id}-q`} type="search" value={q} onChange={(e) => setQ(e.target.value)} className={fieldInput} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-state`} className="block text-sm">{t("state")}</label>
          <select id={`${id}-state`} value={state} onChange={(e) => setState(e.target.value as ServiceState | "")} className={fieldInput}>
            <option value="">{t("all")}</option>
            {serviceStates.map((s) => <option key={s} value={s}>{tStates(s)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-filter-catalog`} className="block text-sm">{t("catalog")}</label>
          <select id={`${id}-filter-catalog`} value={catalogId} onChange={(e) => setCatalogId(e.target.value)} className={fieldInput}>
            <option value="">{t("all")}</option>
            {catalogs.map((c) => <option key={c.id} value={c.id}>{textOf(c.name, locale)}</option>)}
          </select>
        </div>
      </div>
      {shown.length === 0 ? <p>{t("servicesEmpty")}</p> : <ServiceTable services={shown} catalogs={catalogs} />}
    </section>
  );
}

interface CatalogDraft {
  key: string;
  name: { en: string; ar: string };
  description: { en: string; ar: string };
  departmentId: string;
  active: boolean;
  editors: { userId: string | null; groupId: string | null; name: string | null }[];
}

function draftOf(c: Catalog | null): CatalogDraft {
  return {
    key: c?.key ?? "",
    name: { en: c?.name.en ?? "", ar: c?.name.ar ?? "" },
    description: { en: c?.description.en ?? "", ar: c?.description.ar ?? "" },
    departmentId: c?.departmentId ?? "",
    active: c?.active ?? true,
    editors: c?.editors ?? [],
  };
}

function CatalogsSection({ catalogs, departments, groups, manager, csrfToken }: {
  catalogs: Catalog[]; departments: Department[]; groups: GroupSummary[]; manager: boolean; csrfToken: string;
}) {
  const t = useTranslations("catalog.settings");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [editing, setEditing] = useState<Catalog | "new" | null>(null);
  const { status, run } = useSaver();
  const departmentName = new Map(departments.map((d) => [d.id, d.name]));
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3">
      <h3 id={`${id}-heading`} className="text-xl font-semibold">{t("catalogsTitle")}</h3>
      <p className="max-w-2xl text-ink-muted">{t("catalogsIntro")}</p>
      {catalogs.length === 0 ? <p>{t("catalogsEmpty")}</p> : (
        <table className={tableClass} data-testid="catalog-catalogs">
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("name")}</th>
              <th scope="col" className={cellClass}>{t("key")}</th>
              <th scope="col" className={cellClass}>{t("department")}</th>
              <th scope="col" className={cellClass}>{t("editors")}</th>
              <th scope="col" className={cellClass}>{t("serviceCount")}</th>
              {manager && <th scope="col" className={cellClass}><span className="sr-only">{t("actions")}</span></th>}
            </tr>
          </thead>
          <tbody>
            {catalogs.map((c) => (
              <tr key={c.id}>
                <td className={cellClass}>
                  <Localized text={c.name} locale={locale} />
                  {!c.active && <span className="ms-2 text-sm text-ink-muted">({t("inactive")})</span>}
                </td>
                <td className={cellClass}><span className="font-mono" dir="ltr">{c.key}</span></td>
                <td className={cellClass}>{c.departmentId ? textOf(departmentName.get(c.departmentId), locale) : ""}</td>
                <td className={cellClass}>{c.editors.map((e) => e.name ?? "").filter(Boolean).join(", ")}</td>
                <td className={cellClass}>{c.serviceCount}</td>
                {manager && (
                  <td className={cellClass}>
                    <div className="flex flex-wrap gap-2">
                      <button type="button" className={smallButton} onClick={() => setEditing(c)} aria-label={t("editCatalog", { name: textOf(c.name, locale) })}>{t("edit")}</button>
                      <button type="button" className={smallButton} aria-label={t("deleteCatalog", { name: textOf(c.name, locale) })}
                        onClick={() => void run(async () => { await deleteRequest(`/v1/catalog/catalogs/${c.id}`, csrfToken); router.refresh(); })}>{t("delete")}</button>
                    </div>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <CatalogStatusLine status={status} />
      {manager && editing === null && (
        <button type="button" className={smallButton} onClick={() => setEditing("new")}>{t("addCatalog")}</button>
      )}
      {manager && editing !== null && (
        <CatalogForm key={editing === "new" ? "new" : editing.id} catalog={editing === "new" ? null : editing} departments={departments} groups={groups}
          csrfToken={csrfToken} onDone={() => { setEditing(null); router.refresh(); }} onCancel={() => setEditing(null)} />
      )}
    </section>
  );
}

/** Create or change a catalog: key, names, department, active and editors (users or groups). */
export function CatalogForm({ catalog, departments, groups, csrfToken, onDone, onCancel }: {
  catalog: Catalog | null; departments: Department[]; groups: GroupSummary[]; csrfToken: string; onDone: () => void; onCancel: () => void;
}) {
  const t = useTranslations("catalog.settings");
  const locale = useLocale() as Locale;
  const id = useId();
  const [draft, setDraft] = useState<CatalogDraft>(draftOf(catalog));
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<Person[]>([]);
  const [groupId, setGroupId] = useState("");
  const { status, run, busy } = useSaver();
  const set = (patch: Partial<CatalogDraft>) => setDraft((d) => ({ ...d, ...patch }));
  return (
    <form className="space-y-3 rounded-lg border border-line p-4" aria-label={catalog ? t("editCatalog", { name: textOf(catalog.name, locale) }) : t("addCatalog")}
      onSubmit={(e) => {
        e.preventDefault();
        void run(async () => {
          const body = {
            key: draft.key.trim(),
            name: toInput(draft.name.en, draft.name.ar),
            description: draft.description.en.trim() || draft.description.ar.trim() ? toInput(draft.description.en, draft.description.ar) : undefined,
            departmentId: draft.departmentId || null,
            active: draft.active,
            editors: draft.editors.map((e) => ({ userId: e.userId, groupId: e.groupId })),
          };
          if (catalog) await putJson(`/v1/catalog/catalogs/${catalog.id}`, body, csrfToken, { "If-Match": catalog.etag });
          else await postJson("/v1/catalog/catalogs", body, csrfToken);
          onDone();
        });
      }}>
      <div className="space-y-1">
        <label htmlFor={`${id}-key`} className="block text-sm font-semibold">{t("key")}</label>
        <input id={`${id}-key`} value={draft.key} onChange={(e) => set({ key: e.target.value })} required maxLength={60} dir="ltr"
          aria-describedby={`${id}-key-hint`} className={`${fieldInput} font-mono`} />
        <p id={`${id}-key-hint`} className="text-sm text-ink-muted">{t("keyHint")}</p>
      </div>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={draft.name.en} ar={draft.name.ar} maxLength={150} onChange={(name) => set({ name })} />
      <BilingualInputs id={`${id}-description`} label={t("description")} en={draft.description.en} ar={draft.description.ar} maxLength={1000} onChange={(description) => set({ description })} />
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-department`} className="block text-sm font-semibold">{t("department")}</label>
          <select id={`${id}-department`} value={draft.departmentId} onChange={(e) => set({ departmentId: e.target.value })} className={fieldInput}>
            <option value="">{t("none")}</option>
            {departments.map((d) => <option key={d.id} value={d.id}>{textOf(d.name, locale)}</option>)}
          </select>
        </div>
        <label className="flex items-center gap-2 self-end">
          <input type="checkbox" className="size-6" checked={draft.active} onChange={(e) => set({ active: e.target.checked })} />
          {t("active")}
        </label>
      </div>
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("editors")}</legend>
        <p className="text-sm text-ink-muted">{t("editorsHint")}</p>
        <ul className="space-y-1">
          {draft.editors.map((e, i) => (
            <li key={`${e.userId ?? e.groupId}`} className="flex items-center gap-2">
              <span>{e.name ?? e.userId ?? e.groupId}</span>
              <button type="button" className={smallButton} aria-label={t("removeEditor", { name: e.name ?? "" })}
                onClick={() => set({ editors: draft.editors.filter((_, j) => j !== i) })}>{t("remove")}</button>
            </li>
          ))}
        </ul>
        <div className="flex flex-wrap items-end gap-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-person`} className="block text-sm">{t("findPerson")}</label>
            <input id={`${id}-person`} type="search" value={query} onChange={(e) => setQuery(e.target.value)} className={fieldInput} />
          </div>
          <button type="button" className={smallButton} disabled={query.trim().length < 2}
            onClick={() => void getJson<Person[]>(`/v1/forms/people?${new URLSearchParams({ q: query.trim() })}`).then(setFound).catch(() => setFound([]))}>{t("find")}</button>
        </div>
        {found.length > 0 && (
          <ul className="space-y-1" aria-label={t("found")}>
            {found.map((p) => (
              <li key={p.id}>
                <button type="button" className={smallButton} disabled={draft.editors.some((e) => e.userId === p.id)}
                  onClick={() => set({ editors: [...draft.editors, { userId: p.id, groupId: null, name: p.displayName }] })}>
                  {t("addEditor", { name: p.displayName ?? p.id })}
                </button>
              </li>
            ))}
          </ul>
        )}
        {groups.length > 0 && (
          <div className="flex flex-wrap items-end gap-2">
            <div className="space-y-1">
              <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
              <select id={`${id}-group`} value={groupId} onChange={(e) => setGroupId(e.target.value)} className={fieldInput}>
                <option value="">{t("none")}</option>
                {groups.map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
              </select>
            </div>
            <button type="button" className={smallButton} disabled={!groupId || draft.editors.some((e) => e.groupId === groupId)}
              onClick={() => {
                const g = groups.find((x) => x.id === groupId);
                set({ editors: [...draft.editors, { userId: null, groupId, name: g ? textOf(g.name, locale) : null }] });
                setGroupId("");
              }}>{t("addGroup")}</button>
          </div>
        )}
      </fieldset>
      <div className="flex flex-wrap items-center gap-3">
        <button type="submit" className={primaryAction} disabled={busy}>{t("save")}</button>
        <button type="button" className={smallButton} onClick={onCancel}>{t("cancel")}</button>
        <CatalogStatusLine status={status} />
      </div>
    </form>
  );
}

function TemplatesSection({ templates, csrfToken }: { templates: ServiceTemplate[]; csrfToken: string }) {
  const t = useTranslations("catalog.settings");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run } = useSaver();
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3">
      <h3 id={`${id}-heading`} className="text-xl font-semibold">{t("templatesTitle")}</h3>
      <p className="max-w-2xl text-ink-muted">{t("templatesIntro")}</p>
      <ul className="space-y-2">
        {templates.map((tpl) => (
          <li key={tpl.id} className="flex flex-wrap items-center gap-3 rounded-md border border-line p-2">
            <span className="font-semibold"><Localized text={tpl.name} locale={locale} /></span>
            <span className="text-sm text-ink-muted">{tpl.builtIn ? t("builtIn") : t("savedTemplate")}</span>
            {!tpl.builtIn && (
              <button type="button" className={smallButton} aria-label={t("deleteTemplate", { name: textOf(tpl.name, locale) })}
                onClick={() => void run(async () => { await deleteRequest(`/v1/catalog/templates/${tpl.id}`, csrfToken); router.refresh(); })}>{t("delete")}</button>
            )}
          </li>
        ))}
      </ul>
      <CatalogStatusLine status={status} />
    </section>
  );
}
