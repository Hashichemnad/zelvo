"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { patchJson, putJson } from "@/lib/client-api";
import { languages, type DirectoryUser, type Language, type Role, type Skill, type UserStatus } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { Localized, SaveStatusLine, inputClass, primaryButton, useSaver } from "./common";

export interface UserEditFormProps {
  initial: DirectoryUser;
  /** Roles to choose from; null when the caller may not list roles (then roles are shown by key). */
  roles: Role[] | null;
  skills: Skill[];
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
export function UserEditForm({ initial, roles, skills, csrfToken, canManage }: UserEditFormProps) {
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
