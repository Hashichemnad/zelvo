"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import { toInput, type Group, type GroupSummary } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { BilingualInputs, Localized, SaveStatusLine, cellClass, primaryButton, tableClass, useSaver } from "./common";

/** Settings → Groups: the tenant's assignment groups; administrators add new ones. */
export function GroupsPanel({ groups, csrfToken, canManage }: { groups: GroupSummary[]; csrfToken: string; canManage: boolean }) {
  const t = useTranslations("directory.groups");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [name, setName] = useState({ en: "", ar: "" });
  const { status, run, busy } = useSaver();

  return (
    <div className="space-y-4">
      {groups.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <table className={tableClass}>
          <caption className="sr-only">{t("caption")}</caption>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("name")}</th>
              <th scope="col" className={cellClass}>{t("members")}</th>
              <th scope="col" className={cellClass}>{t("leads")}</th>
              <th scope="col" className={cellClass}>{t("state")}</th>
            </tr>
          </thead>
          <tbody>
            {groups.map((group) => (
              <tr key={group.id}>
                <td className={cellClass}>
                  <Link href={`/settings/groups/${group.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                    <Localized text={group.name} locale={locale} />
                  </Link>
                </td>
                <td className={cellClass}>{group.memberCount}</td>
                <td className={cellClass}>{group.leadCount}</td>
                <td className={cellClass}>{group.active ? t("active") : t("inactive")}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {canManage && (
        <form
          className="space-y-2 rounded-lg border border-line p-3"
          aria-label={t("add")}
          onSubmit={(e) => {
            e.preventDefault();
            void run(() => postJson<Group>("/v1/groups", { name: toInput(name.en, name.ar) }, csrfToken)).then((created) => {
              if (created) router.push(`/settings/groups/${created.id}`);
            });
          }}
        >
          <BilingualInputs id={`${id}-name`} label={t("newName")} en={name.en} ar={name.ar} onChange={setName} />
          <button type="submit" className={primaryButton} disabled={busy || (!name.en.trim() && !name.ar.trim())}>
            {t("add")}
          </button>
        </form>
      )}
      <SaveStatusLine status={status} />
    </div>
  );
}
