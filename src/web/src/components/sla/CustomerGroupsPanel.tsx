"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import { textOf, toInput, type UserPage } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { CustomerGroup } from "@/lib/sla";
import { BilingualInputs, Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "@/components/directory/common";

/** Add or change a customer group: name, active and email domains (one per line or comma-separated). */
function CustomerGroupForm({ group, onSubmit, onCancel, busy }: {
  group: CustomerGroup | null;
  onSubmit: (input: { name: { en: string | null; ar: string | null }; active: boolean; emailDomains: string[] }) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const t = useTranslations("sla.customerGroups");
  const id = useId();
  const [name, setName] = useState({ en: group?.name.en ?? "", ar: group?.name.ar ?? "" });
  const [active, setActive] = useState(group?.active ?? true);
  const [domains, setDomains] = useState((group?.emailDomains ?? []).join("\n"));
  return (
    <form
      className="space-y-4 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      data-testid="customer-group-form"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({ name: toInput(name.en, name.ar), active, emailDomains: domains.split(/[\s,]+/).map((d) => d.trim()).filter(Boolean) });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{group ? t("editGroup") : t("addGroup")}</h3>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} maxLength={150} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
        {t("active")}
      </label>
      <div className="space-y-1">
        <label htmlFor={`${id}-domains`} className="block font-semibold">{t("domains")}</label>
        <textarea id={`${id}-domains`} dir="ltr" rows={3} value={domains} onChange={(e) => setDomains(e.target.value)} aria-describedby={`${id}-domains-hint`} className={inputClass} />
        <p id={`${id}-domains-hint`} className="text-sm text-ink-muted">{t("domainsHint")}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || (!name.en.trim() && !name.ar.trim())}>{t("save")}</button>
        <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

/** Requesters added by hand: search people and add, or remove. */
function Members({ group, onChange, csrfToken, canManage }: {
  group: CustomerGroup;
  onChange: (group: CustomerGroup) => void;
  csrfToken: string;
  canManage: boolean;
}) {
  const t = useTranslations("sla.customerGroups");
  const id = useId();
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<UserPage["items"] | null>(null);
  const [candidate, setCandidate] = useState("");
  const saver = useSaver();
  const base = `/v1/sla/customer-groups/${group.id}`;
  return (
    <section aria-labelledby={`${id}-members`} className="space-y-2">
      <h4 id={`${id}-members`} className="font-semibold">{t("members")}</h4>
      {group.members.length === 0 ? (
        <p className="text-sm">{t("noMembers")}</p>
      ) : (
        <ul className="flex flex-wrap gap-2 text-sm">
          {group.members.map((m) => (
            <li key={m.userId} className="flex items-center gap-1 rounded-full border border-line px-2 py-0.5">
              {m.displayName ?? m.userId}
              {canManage && (
                <button type="button" className="font-semibold text-bad" disabled={saver.busy}
                  aria-label={t("removeMember", { name: m.displayName ?? m.userId })}
                  onClick={() => void saver.run(async () => {
                    await deleteRequest(`${base}/members/${m.userId}`, csrfToken);
                    const all = await getJson<CustomerGroup[]>("/v1/sla/customer-groups");
                    onChange(all.find((g) => g.id === group.id) ?? group);
                  })}>
                  ×
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canManage && (
        <div className="flex flex-wrap items-end gap-2">
          <form
            className="flex flex-wrap items-end gap-2"
            role="search"
            onSubmit={(e) => {
              e.preventDefault();
              void saver.run(async () => setFound((await getJson<UserPage>(`/v1/users?${new URLSearchParams({ q: query.trim(), status: "Active", pageSize: "20" })}`)).items));
            }}
          >
            <div className="space-y-1">
              <label htmlFor={`${id}-search`} className="block text-sm">{t("findPerson")}</label>
              <input id={`${id}-search`} value={query} maxLength={100} onChange={(e) => setQuery(e.target.value)} className={inputClass} />
            </div>
            <button type="submit" className={smallButton} disabled={saver.busy || query.trim().length < 2}>{t("search")}</button>
          </form>
          {found && (
            <form
              className="flex flex-wrap items-end gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                void saver.run(async () => {
                  onChange(await putJson<CustomerGroup>(`${base}/members/${candidate}`, {}, csrfToken));
                  setCandidate("");
                });
              }}
            >
              <div className="space-y-1">
                <label htmlFor={`${id}-candidate`} className="block text-sm">{t("person")}</label>
                <select id={`${id}-candidate`} value={candidate} onChange={(e) => setCandidate(e.target.value)} className={inputClass}>
                  <option value="">{found.length === 0 ? t("nobodyFound") : t("choosePerson")}</option>
                  {found.map((u) => (
                    <option key={u.id} value={u.id}>{u.displayName ?? u.id}</option>
                  ))}
                </select>
              </div>
              <button type="submit" className={smallButton} disabled={saver.busy || !candidate}>{t("addMember")}</button>
            </form>
          )}
        </div>
      )}
      <SaveStatusLine status={saver.status} />
    </section>
  );
}

/**
 * Settings → Customer groups (module 8; product owner decision 2026-11-24): groups of requesters
 * with their own SLA policies, by email domain or added by hand.
 */
export function CustomerGroupsPanel({ initial, csrfToken, canManage }: { initial: CustomerGroup[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("sla.customerGroups");
  const locale = useLocale() as Locale;
  const [groups, setGroups] = useState(initial);
  const [editing, setEditing] = useState<CustomerGroup | "new" | null>(null);
  const saver = useSaver();
  const reload = async () => setGroups(await getJson<CustomerGroup[]>("/v1/sla/customer-groups"));
  return (
    <div className="space-y-6">
      {groups.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ul className="space-y-4" data-testid="customer-groups">
          {groups.map((group) => {
            const name = textOf(group.name, locale);
            return (
              <li key={group.id} className="space-y-3 rounded-lg border border-line p-4">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="text-lg font-bold"><Localized text={group.name} locale={locale} /></h3>
                  {!group.active && <span className="rounded-full border border-line px-2 text-xs">{t("inactive")}</span>}
                  {canManage && (
                    <span className="ms-auto flex flex-wrap gap-1">
                      <button type="button" className={smallButton} onClick={() => setEditing(group)}>{t("edit", { name })}</button>
                      <button type="button" className={smallButton} disabled={saver.busy}
                        onClick={() => void saver.run(async () => {
                          await deleteRequest(`/v1/sla/customer-groups/${group.id}`, csrfToken);
                          await reload();
                        })}>
                        {t("delete", { name })}
                      </button>
                    </span>
                  )}
                </div>
                <p className="text-sm">
                  {group.emailDomains.length === 0 ? t("noDomains") : t("domainsList", { domains: group.emailDomains.join(", ") })}
                </p>
                <p className="text-sm text-ink-muted">{t("usedBy", { count: group.policyCount })}</p>
                <Members group={group} csrfToken={csrfToken} canManage={canManage}
                  onChange={(changed) => setGroups(groups.map((g) => (g.id === changed.id ? changed : g)))} />
              </li>
            );
          })}
        </ul>
      )}
      {canManage &&
        (editing ? (
          <CustomerGroupForm
            key={editing === "new" ? "new" : editing.id}
            group={editing === "new" ? null : editing}
            busy={saver.busy}
            onCancel={() => setEditing(null)}
            onSubmit={(input) =>
              void saver.run(async () => {
                if (editing === "new") await postJson("/v1/sla/customer-groups", input, csrfToken);
                else await putJson(`/v1/sla/customer-groups/${editing.id}`, input, csrfToken);
                await reload();
                setEditing(null);
              })
            }
          />
        ) : (
          <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("addGroup")}</button>
        ))}
      <SaveStatusLine status={saver.status} />
    </div>
  );
}
