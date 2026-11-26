"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, patchJson, postJson } from "@/lib/client-api";
import { toInput, type Skill } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { BilingualInputs, Localized, SaveStatusLine, cellClass, primaryButton, smallButton, tableClass, useSaver } from "./common";

/** Settings → Skills: skills that agents have and assignment rules can require (round-robin). */
export function SkillsPanel({ initial, csrfToken, canManage }: { initial: Skill[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("directory.skills");
  const locale = useLocale() as Locale;
  const id = useId();
  const [skills, setSkills] = useState(initial);
  const [name, setName] = useState({ en: "", ar: "" });
  const [renaming, setRenaming] = useState<{ id: string; en: string; ar: string } | null>(null);
  const { status, run, busy } = useSaver();

  const change = (action: () => Promise<unknown>) =>
    run(async () => {
      await action();
      setSkills(await getJson<Skill[]>("/v1/skills?includeInactive=true"));
    });

  return (
    <div className="space-y-4">
      {skills.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("caption")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("name")}</th>
                <th scope="col" className={cellClass}>{t("users")}</th>
                <th scope="col" className={cellClass}>{t("state")}</th>
                {canManage && <th scope="col" className={cellClass}>{t("actions")}</th>}
              </tr>
            </thead>
            <tbody>
              {skills.map((skill) => {
                const label = skill.name[locale] ?? skill.name[locale === "ar" ? "en" : "ar"] ?? "";
                return (
                  <tr key={skill.id}>
                    <td className={cellClass}>
                      {renaming?.id === skill.id ? (
                        <form
                          className="space-y-2"
                          aria-label={t("rename", { name: label })}
                          onSubmit={(e) => {
                            e.preventDefault();
                            void change(() => patchJson(`/v1/skills/${skill.id}`, { name: toInput(renaming.en, renaming.ar) }, csrfToken)).then(() => setRenaming(null));
                          }}
                        >
                          <BilingualInputs id={`${id}-${skill.id}`} label={t("name")} en={renaming.en} ar={renaming.ar} onChange={(v) => setRenaming({ ...renaming, ...v })} />
                          <button type="submit" className={smallButton} disabled={busy}>{t("save")}</button>
                        </form>
                      ) : (
                        <Localized text={skill.name} locale={locale} />
                      )}
                    </td>
                    <td className={cellClass}>{skill.userCount}</td>
                    <td className={cellClass}>{skill.active ? t("active") : t("inactive")}</td>
                    {canManage && (
                      <td className={cellClass}>
                        <span className="flex flex-wrap gap-1">
                          <button type="button" className={smallButton} disabled={busy} onClick={() => setRenaming({ id: skill.id, en: skill.name.en ?? "", ar: skill.name.ar ?? "" })}>
                            {t("rename", { name: label })}
                          </button>
                          <button type="button" className={smallButton} disabled={busy} onClick={() => void change(() => patchJson(`/v1/skills/${skill.id}`, { active: !skill.active }, csrfToken))}>
                            {skill.active ? t("deactivate", { name: label }) : t("activate", { name: label })}
                          </button>
                          <button type="button" className={smallButton} disabled={busy} onClick={() => void change(() => deleteRequest(`/v1/skills/${skill.id}`, csrfToken))}>
                            {t("delete", { name: label })}
                          </button>
                        </span>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {canManage && (
        <form
          className="space-y-2 rounded-lg border border-line p-3"
          aria-label={t("add")}
          onSubmit={(e) => {
            e.preventDefault();
            void change(() => postJson("/v1/skills", { name: toInput(name.en, name.ar) }, csrfToken)).then(() => setName({ en: "", ar: "" }));
          }}
        >
          <BilingualInputs id={`${id}-new`} label={t("newName")} en={name.en} ar={name.ar} onChange={setName} />
          <button type="submit" className={primaryButton} disabled={busy || (!name.en.trim() && !name.ar.trim())}>
            {t("add")}
          </button>
        </form>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}
