"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, patchJson, putJson } from "@/lib/client-api";
import { toInput, type DirectoryUser, type Group, type UserPage } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { BilingualInputs, Localized, SaveStatusLine, cellClass, inputClass, primaryButton, secondaryButton, smallButton, tableClass, useSaver } from "./common";

export interface GroupDetailPanelProps {
  initial: Group;
  csrfToken: string;
  /** directory.manage: edit the group, leads and every member. */
  canManage: boolean;
  /** The signed-in user's id; leads of the group may add and remove members who are not leads. */
  meId: string;
}

/**
 * One group: name, description and whether it is active (administrators), and its members with
 * the team lead flag and availability for assignment. Team leads of the group manage its members
 * who are not leads (decision 2026-11-23).
 */
export function GroupDetailPanel({ initial, csrfToken, canManage, meId }: GroupDetailPanelProps) {
  const t = useTranslations("directory.groups");
  const tLanguages = useTranslations("directory.languages");
  const locale = useLocale() as Locale;
  const id = useId();
  const [group, setGroup] = useState(initial);
  const [name, setName] = useState({ en: initial.name.en ?? "", ar: initial.name.ar ?? "" });
  const [description, setDescription] = useState({ en: initial.description?.en ?? "", ar: initial.description?.ar ?? "" });
  const [active, setActive] = useState(initial.active);
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<DirectoryUser[] | null>(null);
  const [candidate, setCandidate] = useState("");
  const { status, run, busy } = useSaver();
  const isLead = group.members.some((m) => m.userId === meId && m.isLead);
  const canManageMembers = canManage || isLead;
  const base = `/v1/groups/${group.id}`;

  const memberChange = (action: () => Promise<unknown>) =>
    run(async () => {
      await action();
      setGroup(await getJson<Group>(base));
    });

  return (
    <div className="space-y-8">
      {canManage && (
        <form
          className="space-y-3"
          aria-labelledby={`${id}-details`}
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              setGroup(await patchJson<Group>(base, { name: toInput(name.en, name.ar), description: toInput(description.en, description.ar), active }, csrfToken));
            });
          }}
        >
          <h3 id={`${id}-details`} className="text-xl font-bold">{t("details")}</h3>
          <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} />
          <BilingualInputs id={`${id}-description`} label={t("description")} en={description.en} ar={description.ar} onChange={setDescription} maxLength={500} />
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
            {t("activeLabel")}
          </label>
          <button type="submit" className={primaryButton} disabled={busy}>{t("save")}</button>
        </form>
      )}

      <section aria-labelledby={`${id}-members`} className="space-y-3">
        <h3 id={`${id}-members`} className="text-xl font-bold">{t("members")}</h3>
        {group.members.length === 0 ? (
          <p>{t("noMembers")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className={tableClass} data-testid="group-members">
              <caption className="sr-only">{t("membersCaption")}</caption>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("member")}</th>
                  <th scope="col" className={cellClass}>{t("lead")}</th>
                  <th scope="col" className={cellClass}>{t("available")}</th>
                  <th scope="col" className={cellClass}>{t("memberLanguages")}</th>
                  <th scope="col" className={cellClass}>{t("memberSkills")}</th>
                  {canManageMembers && <th scope="col" className={cellClass}>{t("actions")}</th>}
                </tr>
              </thead>
              <tbody>
                {group.members.map((member) => {
                  const who = member.displayName ?? member.userId;
                  const mayChange = canManage || (isLead && !member.isLead);
                  return (
                    <tr key={member.userId}>
                      <td className={cellClass}>
                        {who}
                        {member.status === "Disabled" && <span className="ms-2 text-xs text-ink-muted">{t("disabled")}</span>}
                      </td>
                      <td className={cellClass}>
                        {canManage ? (
                          <input type="checkbox" aria-label={t("leadOf", { name: who })} checked={member.isLead} disabled={busy}
                            onChange={(e) => void memberChange(() => putJson(`${base}/members/${member.userId}`, { isLead: e.target.checked }, csrfToken))} />
                        ) : member.isLead ? t("yes") : t("no")}
                      </td>
                      <td className={cellClass}>
                        {mayChange ? (
                          <input type="checkbox" aria-label={t("availableFor", { name: who })} checked={member.availableForAssignment} disabled={busy}
                            onChange={(e) => void memberChange(() => putJson(`/v1/users/${member.userId}/availability`, { available: e.target.checked }, csrfToken))} />
                        ) : member.availableForAssignment ? t("yes") : t("no")}
                      </td>
                      <td className={cellClass}>{member.languages.map((l) => tLanguages(l)).join(locale === "ar" ? "، " : ", ") || "—"}</td>
                      <td className={cellClass}>
                        {member.skills.length === 0 ? "—" : member.skills.map((s, i) => (
                          <span key={s.id}>
                            {i > 0 && (locale === "ar" ? "، " : ", ")}
                            <Localized text={s.name} locale={locale} />
                          </span>
                        ))}
                      </td>
                      {canManageMembers && (
                        <td className={cellClass}>
                          {mayChange && (
                            <button type="button" className={smallButton} disabled={busy}
                              onClick={() => void memberChange(() => deleteRequest(`${base}/members/${member.userId}`, csrfToken))}>
                              {t("remove", { name: who })}
                            </button>
                          )}
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {canManageMembers && (
          <div className="space-y-2 rounded-lg border border-line p-3">
            <form
              className="flex flex-wrap items-end gap-2"
              role="search"
              aria-label={t("findPeople")}
              onSubmit={(e) => {
                e.preventDefault();
                void run(async () => {
                  const page = await getJson<UserPage>(`/v1/users?${new URLSearchParams({ q: query.trim(), status: "Active", pageSize: "20" })}`);
                  setFound(page.items.filter((u) => !group.members.some((m) => m.userId === u.id)));
                  setCandidate("");
                });
              }}
            >
              <div className="space-y-1">
                <label htmlFor={`${id}-q`} className="block font-semibold">{t("findPeople")}</label>
                <input id={`${id}-q`} type="search" dir="auto" value={query} onChange={(e) => setQuery(e.target.value)} className={inputClass} />
              </div>
              <button type="submit" className={secondaryButton} disabled={busy}>{t("find")}</button>
            </form>
            {found && (
              found.length === 0 ? (
                <p>{t("nobodyFound")}</p>
              ) : (
                <form
                  className="flex flex-wrap items-end gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (!candidate) return;
                    void memberChange(() => putJson(`${base}/members/${candidate}`, { isLead: false }, csrfToken)).then(() => setFound(null));
                  }}
                >
                  <div className="space-y-1">
                    <label htmlFor={`${id}-candidate`} className="block font-semibold">{t("person")}</label>
                    <select id={`${id}-candidate`} value={candidate} onChange={(e) => setCandidate(e.target.value)} className={inputClass}>
                      <option value="">{t("choosePerson")}</option>
                      {found.map((u) => (
                        <option key={u.id} value={u.id}>{u.displayName ?? u.email ?? u.id}</option>
                      ))}
                    </select>
                  </div>
                  <button type="submit" className={primaryButton} disabled={busy || !candidate}>{t("addMember")}</button>
                </form>
              )
            )}
          </div>
        )}
      </section>
      <SaveStatusLine status={status} />
    </div>
  );
}
