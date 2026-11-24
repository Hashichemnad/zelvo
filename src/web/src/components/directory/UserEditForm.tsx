"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { getJson, patchJson, putJson } from "@/lib/client-api";
import { languages, selectableNodes, textOf, type Department, type DirectoryUser, type Language, type Role, type Skill, type TreeNode, type UserStatus } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { Localized, SaveStatusLine, inputClass, primaryButton, useSaver } from "./common";

export interface UserEditFormProps {
  initial: DirectoryUser;
  /** Roles to choose from; null when the caller may not list roles (then roles are shown by key). */
  roles: Role[] | null;
  skills: Skill[];
  /** Task C1: departments and locations to choose from. */
  departments?: Department[];
  locations?: TreeNode[];
  csrfToken: string;
  /** users.manage */
  canManage: boolean;
}

const sameSet = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((x) => b.includes(x));

/**
 * One user (Settings → Users): name, status, Emirates ID, roles, the languages they handle, skills
 * and availability for assignment (task B2). Fields the caller's roles hide are not shown; only
 * holders of users.manage change anything.
 */
export function UserEditForm({ initial, roles, skills, departments = [], locations = [], csrfToken, canManage }: UserEditFormProps) {
  const t = useTranslations("directory.users");
  const tLanguages = useTranslations("directory.languages");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [displayName, setDisplayName] = useState(initial.displayName ?? "");
  const [status, setStatus] = useState<UserStatus>(initial.status ?? "Active");
  const [emiratesId, setEmiratesId] = useState(initial.emiratesId ?? "");
  const [roleKeys, setRoleKeys] = useState<string[]>(initial.roles ?? []);
  const [userLanguages, setUserLanguages] = useState<Language[]>(initial.languages ?? []);
  const [skillIds, setSkillIds] = useState<string[]>(initial.skills?.map((s) => s.id) ?? []);
  const [available, setAvailable] = useState(initial.availableForAssignment ?? true);
  const [departmentId, setDepartmentId] = useState(initial.department?.id ?? "");
  const [manager, setManager] = useState(initial.manager ?? null);
  const [managerQuery, setManagerQuery] = useState("");
  const [managerResults, setManagerResults] = useState<{ id: string; displayName: string | null }[] | null>(null);
  const [locationId, setLocationId] = useState(initial.location?.id ?? "");
  const [employeeId, setEmployeeId] = useState(initial.employeeId ?? "");
  const [phone, setPhone] = useState(initial.phone ?? "");
  const saver = useSaver();
  const readOnly = !canManage;

  async function save(event: React.FormEvent) {
    event.preventDefault();
    await saver.run(async () => {
      const patch: Record<string, unknown> = {};
      if (initial.displayName !== undefined && displayName.trim() !== (initial.displayName ?? "")) patch.displayName = displayName.trim();
      if (initial.status !== undefined && status !== initial.status) patch.status = status;
      if (initial.emiratesId !== undefined && emiratesId.trim() !== (initial.emiratesId ?? "")) patch.emiratesId = emiratesId.trim();
      if (initial.languages !== undefined && !sameSet(userLanguages, initial.languages)) patch.languages = userLanguages;
      if (initial.availableForAssignment !== undefined && available !== initial.availableForAssignment) patch.availableForAssignment = available;
      if (initial.department !== undefined && departmentId !== (initial.department?.id ?? "")) patch.departmentId = departmentId;
      if (initial.manager !== undefined && (manager?.id ?? "") !== (initial.manager?.id ?? "")) patch.managerId = manager?.id ?? "";
      if (initial.location !== undefined && locationId !== (initial.location?.id ?? "")) patch.locationId = locationId;
      if (initial.employeeId !== undefined && employeeId.trim() !== (initial.employeeId ?? "")) patch.employeeId = employeeId.trim();
      if (initial.phone !== undefined && phone.trim() !== (initial.phone ?? "")) patch.phone = phone.trim();
      if (Object.keys(patch).length > 0) await patchJson(`/v1/users/${initial.id}`, patch, csrfToken);
      if (initial.roles !== undefined && !sameSet(roleKeys, initial.roles)) await putJson(`/v1/users/${initial.id}/roles`, { roles: roleKeys }, csrfToken);
      if (initial.skills !== undefined && !sameSet(skillIds, initial.skills.map((s) => s.id))) await putJson(`/v1/users/${initial.id}/skills`, { skillIds }, csrfToken);
      router.refresh();
    });
  }

  const toggle = <T extends string,>(list: T[], value: T, on: boolean) => (on ? [...list, value] : list.filter((v) => v !== value));
  const activeSkills = skills.filter((s) => s.active || skillIds.includes(s.id));

  return (
    <form onSubmit={save} className="max-w-3xl space-y-6" data-testid="user-edit">
      {initial.displayName !== undefined && (
        <div className="space-y-1">
          <label htmlFor={`${id}-name`} className="block font-semibold">{t("displayName")}</label>
          <input id={`${id}-name`} dir="auto" value={displayName} maxLength={200} readOnly={readOnly} onChange={(e) => setDisplayName(e.target.value)} className={inputClass} />
        </div>
      )}
      {initial.email !== undefined && (
        <p>
          <span className="font-semibold">{t("email")}: </span>
          <span dir="ltr">{initial.email ?? "—"}</span>
        </p>
      )}
      {initial.status !== undefined && (
        <div className="space-y-1">
          <label htmlFor={`${id}-status`} className="block font-semibold">{t("status")}</label>
          <select id={`${id}-status`} value={status} disabled={readOnly} onChange={(e) => setStatus(e.target.value as UserStatus)} className={`${inputClass} max-w-xs`}>
            <option value="Active">{t("statuses.Active")}</option>
            <option value="Disabled">{t("statuses.Disabled")}</option>
          </select>
        </div>
      )}
      {initial.emiratesId !== undefined && (
        <div className="space-y-1">
          <label htmlFor={`${id}-eid`} className="block font-semibold">{t("emiratesId")}</label>
          <input id={`${id}-eid`} dir="ltr" inputMode="numeric" value={emiratesId} readOnly={readOnly} maxLength={18}
            aria-describedby={`${id}-eid-hint`} onChange={(e) => setEmiratesId(e.target.value)} className={`${inputClass} max-w-xs`} />
          <p id={`${id}-eid-hint`} className="text-sm text-ink-muted">{t("emiratesIdHint")}</p>
        </div>
      )}
      {(initial.department !== undefined || initial.manager !== undefined || initial.location !== undefined || initial.employeeId !== undefined || initial.phone !== undefined) && (
        <fieldset className="grid gap-4 sm:grid-cols-2">
          <legend className="font-semibold">{t("profile")}</legend>
          <p className="text-sm text-ink-muted sm:col-span-2">{t("profileHint")}</p>
          {initial.department !== undefined && (
            <div className="space-y-1">
              <label htmlFor={`${id}-department`} className="block">{t("department")}</label>
              <select id={`${id}-department`} value={departmentId} disabled={readOnly} onChange={(e) => setDepartmentId(e.target.value)} className={inputClass}>
                <option value="">{t("none")}</option>
                {initial.department && !departments.some((d) => d.id === initial.department!.id) && (
                  <option value={initial.department.id}>{textOf(initial.department.name, locale)}</option>
                )}
                {departments.filter((d) => d.active || d.id === departmentId).map((d) => <option key={d.id} value={d.id}>{textOf(d.name, locale)}</option>)}
              </select>
            </div>
          )}
          {initial.location !== undefined && (
            <div className="space-y-1">
              <label htmlFor={`${id}-location`} className="block">{t("location")}</label>
              <select id={`${id}-location`} value={locationId} disabled={readOnly} onChange={(e) => setLocationId(e.target.value)} className={inputClass}>
                <option value="">{t("none")}</option>
                {initial.location && !selectableNodes(locations).some((n) => n.id === initial.location!.id) && (
                  <option value={initial.location.id}>{textOf(initial.location.path, locale)}</option>
                )}
                {selectableNodes(locations).map((n) => <option key={n.id} value={n.id}>{textOf(n.path, locale) || textOf(n.name, locale)}</option>)}
              </select>
            </div>
          )}
          {initial.employeeId !== undefined && (
            <div className="space-y-1">
              <label htmlFor={`${id}-employee`} className="block">{t("employeeId")}</label>
              <input id={`${id}-employee`} dir="ltr" value={employeeId} maxLength={50} readOnly={readOnly} onChange={(e) => setEmployeeId(e.target.value)} className={inputClass} />
            </div>
          )}
          {initial.phone !== undefined && (
            <div className="space-y-1">
              <label htmlFor={`${id}-phone`} className="block">{t("phone")}</label>
              <input id={`${id}-phone`} dir="ltr" type="tel" autoComplete="off" value={phone} maxLength={30} readOnly={readOnly}
                aria-describedby={`${id}-phone-hint`} onChange={(e) => setPhone(e.target.value)} className={inputClass} />
              <p id={`${id}-phone-hint`} className="text-sm text-ink-muted">{t("phoneHint")}</p>
            </div>
          )}
          {initial.manager !== undefined && (
            <div className="space-y-1 sm:col-span-2">
              <label htmlFor={`${id}-manager`} className="block">{t("manager")}</label>
              <p>{manager?.displayName ?? (manager ? manager.id : t("none"))}</p>
              {!readOnly && (
                <div className="flex flex-wrap gap-2">
                  <input id={`${id}-manager`} dir="auto" value={managerQuery} placeholder={t("managerSearch")} onChange={(e) => setManagerQuery(e.target.value)} className={`${inputClass} max-w-xs`} />
                  <button type="button" className={primaryButton} disabled={managerQuery.trim().length < 2}
                    onClick={async () => setManagerResults(await getJson<{ id: string; displayName: string | null }[]>(`/v1/forms/people?q=${encodeURIComponent(managerQuery.trim())}`).catch(() => []))}>
                    {t("managerFind")}
                  </button>
                  {manager && <button type="button" className={primaryButton} onClick={() => setManager(null)}>{t("managerClear")}</button>}
                </div>
              )}
              {managerResults && (
                <ul className="space-y-1" aria-live="polite">
                  {managerResults.length === 0 && <li>{t("managerNone")}</li>}
                  {managerResults.filter((p) => p.id !== initial.id).map((p) => (
                    <li key={p.id}>
                      <button type="button" className="text-brand underline" onClick={() => { setManager(p); setManagerResults(null); setManagerQuery(""); }}>
                        {t("managerPick", { name: p.displayName ?? p.id })}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </fieldset>
      )}
      {initial.roles !== undefined && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("roles")}</legend>
          {roles ? (
            <div className="flex flex-wrap gap-4">
              {roles.map((role) => (
                <label key={role.key} className="flex items-center gap-2">
                  <input type="checkbox" disabled={readOnly} checked={roleKeys.includes(role.key)} onChange={(e) => setRoleKeys(toggle(roleKeys, role.key, e.target.checked))} />
                  <Localized text={role.name} locale={locale} />
                </label>
              ))}
            </div>
          ) : (
            <p dir="ltr">{roleKeys.join(", ") || "—"}</p>
          )}
        </fieldset>
      )}
      {initial.languages !== undefined && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("languages")}</legend>
          <p className="text-sm text-ink-muted">{t("languagesHint")}</p>
          <div className="flex flex-wrap gap-4">
            {languages.map((language) => (
              <label key={language} className="flex items-center gap-2">
                <input type="checkbox" disabled={readOnly} checked={userLanguages.includes(language)}
                  onChange={(e) => setUserLanguages(toggle(userLanguages, language, e.target.checked))} />
                {tLanguages(language)}
              </label>
            ))}
          </div>
        </fieldset>
      )}
      {initial.skills !== undefined && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("skills")}</legend>
          {activeSkills.length === 0 ? (
            <p>{t("noSkills")}</p>
          ) : (
            <div className="flex flex-wrap gap-4">
              {activeSkills.map((skill) => (
                <label key={skill.id} className="flex items-center gap-2">
                  <input type="checkbox" disabled={readOnly} checked={skillIds.includes(skill.id)} onChange={(e) => setSkillIds(toggle(skillIds, skill.id, e.target.checked))} />
                  <Localized text={skill.name} locale={locale} />
                </label>
              ))}
            </div>
          )}
        </fieldset>
      )}
      {initial.availableForAssignment !== undefined && (
        <label className="flex items-center gap-2">
          <input type="checkbox" disabled={readOnly} checked={available} onChange={(e) => setAvailable(e.target.checked)} aria-describedby={`${id}-available-hint`} />
          <span className="font-semibold">{t("available")}</span>
        </label>
      )}
      {initial.availableForAssignment !== undefined && <p id={`${id}-available-hint`} className="text-sm text-ink-muted">{t("availableHint")}</p>}
      {initial.groups !== undefined && (
        <section className="space-y-1" aria-labelledby={`${id}-groups`}>
          <h3 id={`${id}-groups`} className="font-semibold">{t("groups")}</h3>
          {initial.groups.length === 0 ? (
            <p>{t("noGroups")}</p>
          ) : (
            <ul className="list-disc ps-5">
              {initial.groups.map((group) => (
                <li key={group.id}>
                  <Localized text={group.name} locale={locale} />
                  {group.isLead && ` (${t("lead")})`}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
      {canManage && (
        <div className="flex flex-wrap items-center gap-4">
          <button type="submit" className={primaryButton} disabled={saver.busy}>{t("save")}</button>
          <SaveStatusLine status={saver.status} />
        </div>
      )}
    </form>
  );
}
