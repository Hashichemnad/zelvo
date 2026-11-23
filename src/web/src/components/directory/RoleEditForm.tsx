"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { deleteRequest, patchJson, putJson } from "@/lib/client-api";
import { fieldAccessLevels, permissionArea, toInput, type FieldAccess, type PermissionCatalog, type Role } from "@/lib/directory";
import { BilingualInputs, SaveStatusLine, cellClass, dangerButton, inputClass, primaryButton, tableClass, useSaver } from "./common";

/** Roles that always need a second factor (A2 guards); the switch is shown but locked. */
const mfaLocked = new Set(["agent", "team-lead", "tenant-admin"]);

/**
 * One role (Settings → Roles): its name, whether it needs a second factor, its permissions by area
 * and its field rules. Custom roles can be deleted while nobody has them.
 */
export function RoleEditForm({ initial, catalog, csrfToken }: { initial: Role; catalog: PermissionCatalog; csrfToken: string }) {
  const t = useTranslations("directory.roles");
  const tPermissions = useTranslations("directory.permissions");
  const router = useRouter();
  const id = useId();
  const [name, setName] = useState({ en: initial.name.en ?? "", ar: initial.name.ar ?? "" });
  const [requiresMfa, setRequiresMfa] = useState(initial.requiresMfa);
  const [permissions, setPermissions] = useState<string[]>(initial.permissions);
  const [rules, setRules] = useState<Record<string, FieldAccess | "">>(
    Object.fromEntries(initial.fieldRules.map((r) => [`${r.resource}.${r.field}`, r.access])),
  );
  const saver = useSaver();
  const areas = [...new Set(catalog.permissions.map(permissionArea))];
  const label = (key: string) => (tPermissions.has(key.replaceAll(".", "_")) ? tPermissions(key.replaceAll(".", "_")) : key);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    await saver.run(async () => {
      const base = `/v1/roles/${encodeURIComponent(initial.key)}`;
      await patchJson(base, { name: toInput(name.en, name.ar), requiresMfa }, csrfToken);
      await putJson(`${base}/permissions`, { permissions }, csrfToken);
      await putJson(`${base}/field-rules`, {
        rules: Object.entries(rules)
          .filter(([, access]) => access !== "")
          .map(([key, access]) => {
            const [resource, ...field] = key.split(".");
            return { resource, field: field.join("."), access };
          }),
      }, csrfToken);
      router.refresh();
    });
  }

  return (
    <form onSubmit={save} className="space-y-8" data-testid="role-edit">
      <section className="max-w-2xl space-y-3">
        <p className="font-mono text-ink-muted" dir="ltr">{initial.key}</p>
        <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} />
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={requiresMfa} disabled={mfaLocked.has(initial.key)} onChange={(e) => setRequiresMfa(e.target.checked)}
            aria-describedby={`${id}-mfa-hint`} />
          <span className="font-semibold">{t("requiresMfa")}</span>
        </label>
        <p id={`${id}-mfa-hint`} className="text-sm text-ink-muted">{mfaLocked.has(initial.key) ? t("mfaLocked") : t("mfaHint")}</p>
      </section>

      <section className="space-y-3" aria-labelledby={`${id}-permissions`}>
        <h3 id={`${id}-permissions`} className="text-xl font-bold">{t("permissions")}</h3>
        <div className="grid gap-4 md:grid-cols-2">
          {areas.map((area) => (
            <fieldset key={area} className="space-y-1 rounded-lg border border-line p-3">
              <legend className="px-1 font-semibold">{tPermissions.has(`areas.${area}`) ? tPermissions(`areas.${area}`) : area}</legend>
              {catalog.permissions.filter((p) => permissionArea(p) === area).map((permission) => (
                <label key={permission} className="flex items-start gap-2">
                  <input type="checkbox" className="mt-1" checked={permissions.includes(permission)}
                    onChange={(e) => setPermissions(e.target.checked ? [...permissions, permission] : permissions.filter((p) => p !== permission))} />
                  <span>
                    {label(permission)} <span className="font-mono text-xs text-ink-muted" dir="ltr">{permission}</span>
                  </span>
                </label>
              ))}
            </fieldset>
          ))}
        </div>
      </section>

      <section className="space-y-3" aria-labelledby={`${id}-fields`}>
        <h3 id={`${id}-fields`} className="text-xl font-bold">{t("fieldRules")}</h3>
        <p className="max-w-2xl">{t("fieldRulesIntro")}</p>
        {catalog.resources.map((resource) => (
          <table key={resource.key} className={tableClass}>
            <caption className="pb-1 text-start font-semibold">{tPermissions.has(`areas.${resource.key}`) ? tPermissions(`areas.${resource.key}`) : resource.key}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("field")}</th>
                <th scope="col" className={cellClass}>{t("access")}</th>
              </tr>
            </thead>
            <tbody>
              {resource.fields.map((field) => {
                const key = `${resource.key}.${field.key}`;
                return (
                  <tr key={key}>
                    <th scope="row" className={cellClass}>
                      <span dir="ltr" className="font-mono">{field.key}</span>
                      {field.sensitive && <span className="ms-2 text-xs text-bad">{t("sensitive")}</span>}
                    </th>
                    <td className={cellClass}>
                      <select aria-label={t("accessOf", { field: key })} value={rules[key] ?? ""} className={`${inputClass} max-w-xs`}
                        onChange={(e) => setRules({ ...rules, [key]: e.target.value as FieldAccess | "" })}>
                        <option value="">{field.sensitive ? t("defaultHidden") : t("defaultByPermission")}</option>
                        {fieldAccessLevels.filter((level) => level !== "Edit" || field.writable).map((level) => (
                          <option key={level} value={level}>{t(`levels.${level}`)}</option>
                        ))}
                      </select>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        ))}
      </section>

      <div className="flex flex-wrap items-center gap-4">
        <button type="submit" className={primaryButton} disabled={saver.busy}>{t("save")}</button>
        {!initial.isSystem && (
          <button type="button" className={dangerButton} disabled={saver.busy}
            onClick={() => void saver.run(async () => {
              await deleteRequest(`/v1/roles/${encodeURIComponent(initial.key)}`, csrfToken);
              router.push("/settings/roles");
            })}>
            {t("delete")}
          </button>
        )}
        <SaveStatusLine status={saver.status} />
      </div>
    </form>
  );
}
