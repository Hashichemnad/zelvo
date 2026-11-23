"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import type { DirectoryUser, Role } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { Localized, SaveStatusLine, inputClass, primaryButton, useSaver } from "./common";

/** "Add user" (users.manage): creates the person in the tenant's sign-in realm and in Zelvo. */
export function AddUserForm({ roles, csrfToken }: { roles: Role[] | null; csrfToken: string }) {
  const t = useTranslations("directory.users");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roleKeys, setRoleKeys] = useState<string[]>(["requester"]);
  const { status, run, busy } = useSaver();

  return (
    <form
      className="max-w-2xl space-y-3 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      onSubmit={(e) => {
        e.preventDefault();
        void run(() =>
          postJson<DirectoryUser>("/v1/users", { email: email.trim(), displayName: displayName.trim() || undefined, roles: roleKeys }, csrfToken),
        ).then((created) => {
          if (created) router.push(`/settings/users/${created.id}`);
        });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("add")}</h3>
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-email`} className="block font-semibold">{t("email")}</label>
          <input id={`${id}-email`} type="email" dir="ltr" required autoComplete="off" value={email} maxLength={254}
            onChange={(e) => setEmail(e.target.value)} className={inputClass} />
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-name`} className="block font-semibold">{t("displayName")}</label>
          <input id={`${id}-name`} dir="auto" value={displayName} maxLength={200} onChange={(e) => setDisplayName(e.target.value)} className={inputClass} />
        </div>
      </div>
      {roles && (
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("roles")}</legend>
          <div className="flex flex-wrap gap-4">
            {roles.map((role) => (
              <label key={role.key} className="flex items-center gap-2">
                <input type="checkbox" checked={roleKeys.includes(role.key)}
                  onChange={(e) => setRoleKeys(e.target.checked ? [...roleKeys, role.key] : roleKeys.filter((k) => k !== role.key))} />
                <Localized text={role.name} locale={locale} />
              </label>
            ))}
          </div>
        </fieldset>
      )}
      <button type="submit" className={primaryButton} disabled={busy || !email.trim()}>{t("addButton")}</button>
      <SaveStatusLine status={status} />
    </form>
  );
}
