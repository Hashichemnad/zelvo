"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { postJson } from "@/lib/client-api";
import { toInput, type Role } from "@/lib/directory";
import { BilingualInputs, SaveStatusLine, inputClass, primaryButton, useSaver } from "./common";

/** Adds a custom role; it starts with no permissions (roles.manage). */
export function AddRoleForm({ csrfToken }: { csrfToken: string }) {
  const t = useTranslations("directory.roles");
  const router = useRouter();
  const id = useId();
  const [key, setKey] = useState("");
  const [name, setName] = useState({ en: "", ar: "" });
  const [requiresMfa, setRequiresMfa] = useState(false);
  const { status, run, busy } = useSaver();
  const validKey = /^[a-z][a-z0-9-]{1,49}$/.test(key);

  return (
    <form
      className="max-w-2xl space-y-3 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      onSubmit={(e) => {
        e.preventDefault();
        void run(() => postJson<Role>("/v1/roles", { key, name: toInput(name.en, name.ar), requiresMfa }, csrfToken)).then((created) => {
          if (created) router.push(`/settings/roles/${created.key}`);
        });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("add")}</h3>
      <div className="space-y-1">
        <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
        <input id={`${id}-key`} dir="ltr" value={key} maxLength={50} onChange={(e) => setKey(e.target.value.toLowerCase())}
          aria-describedby={`${id}-key-hint`} aria-invalid={key && !validKey ? true : undefined} className={`${inputClass} max-w-xs`} />
        <p id={`${id}-key-hint`} className="text-sm text-ink-muted">{t("keyHint")}</p>
      </div>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={requiresMfa} onChange={(e) => setRequiresMfa(e.target.checked)} />
        {t("requiresMfa")}
      </label>
      <button type="submit" className={primaryButton} disabled={busy || !validKey || (!name.en.trim() && !name.ar.trim())}>{t("addButton")}</button>
      <SaveStatusLine status={status} />
    </form>
  );
}
