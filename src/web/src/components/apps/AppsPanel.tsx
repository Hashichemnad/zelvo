"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, SaveStatusLine, smallButton, useSaver } from "@/components/directory/common";
import { fieldInput } from "@/components/forms/ui";
import { buttonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import { appKeyPattern, maxPackageBytes, type App, type AppImport, type AppImportSummary } from "@/lib/apps";
import { iconGlyph, serviceIcons } from "@/lib/catalog";
import { ApiError, postJson } from "@/lib/client-api";
import { inLanguage, type Locale } from "@/lib/incidents";

/** Sends a package file's text as it is, so the API's hash is the file's. */
async function uploadPackage(file: File, csrfToken: string): Promise<AppImport> {
  const response = await fetch(`/bff/api/v1/app-imports?${new URLSearchParams({ fileName: file.name })}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken },
    body: await file.text(),
  });
  if (!response.ok) {
    let code: string | null = null;
    try {
      const body = (await response.json()) as { code?: string };
      code = body.code ?? null;
    } catch {
      // No problem details.
    }
    throw new ApiError(`POST /v1/app-imports failed with ${response.status}`, response.status, code);
  }
  return (await response.json()) as AppImport;
}

/**
 * Settings → Apps (task K3): the tenant's apps, a new app, and importing an app package, which is
 * checked and previewed and stays inactive until an administrator approves it.
 */
export function AppsPanel({ apps, imports, csrfToken }: { apps: App[]; imports: AppImportSummary[]; csrfToken: string }) {
  const t = useTranslations("apps.settings");
  const tIcons = useTranslations("catalog.editor.icons");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const importer = useSaver();
  const [creating, setCreating] = useState(false);
  const [key, setKey] = useState("");
  const [name, setName] = useState({ en: "", ar: "" });
  const [icon, setIcon] = useState("service");
  const [file, setFile] = useState<File | null>(null);
  const tooLarge = !!file && file.size > maxPackageBytes;
  const valid = appKeyPattern.test(key) && (name.en.trim() || name.ar.trim());

  function create() {
    void run(async () => {
      const app = await postJson<App>("/v1/apps", { key, name: { en: name.en.trim() || null, ar: name.ar.trim() || null }, icon }, csrfToken);
      router.push(`/settings/apps/${app.key}`);
    });
  }

  function upload() {
    if (!file || tooLarge) return;
    void importer.run(async () => {
      const result = await uploadPackage(file, csrfToken);
      router.push(`/settings/apps/imports/${result.id}`);
    });
  }

  return (
    <div className="space-y-8">
      <section aria-labelledby={`${id}-apps`} className="space-y-3">
        <h3 id={`${id}-apps`} className="text-xl font-semibold">{t("apps")}</h3>
        {apps.length === 0 ? <p>{t("none")}</p> : (
          <ul className="grid gap-3 sm:grid-cols-2" data-testid="apps">
            {apps.map((app) => (
              <li key={app.id} className="space-y-1 rounded-md border border-line p-3">
                <p className="flex flex-wrap items-center gap-2">
                  <span aria-hidden="true">{iconGlyph[app.icon] ?? "◆"}</span>
                  <Link href={`/settings/apps/${app.key}`} className="font-semibold text-brand underline-offset-4 hover:underline">{inLanguage(app.name, locale).value}</Link>
                  <span className="font-mono text-sm text-ink-muted" dir="ltr">{app.key}</span>
                  <span className={app.active ? "text-sm text-ok" : "text-sm text-ink-muted"}>{app.active ? t("active") : t("inactive")}</span>
                </p>
                {app.members && (
                  <p className="text-sm text-ink-muted">
                    {t("contents", { types: app.members.recordTypes.length, lists: app.members.dataLists.length, roles: app.members.roles.length, rules: app.members.rules.length })}
                  </p>
                )}
              </li>
            ))}
          </ul>
        )}
        {!creating ? <button type="button" className={buttonClass} onClick={() => setCreating(true)}>{t("new")}</button> : (
          <form className="space-y-3 rounded-lg border border-line p-4" aria-label={t("new")} onSubmit={(e) => (e.preventDefault(), valid && create())}>
            <div className="max-w-sm space-y-1">
              <label htmlFor={`${id}-key`} className="block font-semibold">{t("key")}</label>
              <input id={`${id}-key`} dir="ltr" value={key} maxLength={40} aria-describedby={`${id}-key-hint`} onChange={(e) => setKey(e.target.value.toLowerCase())} className={fieldInput} />
              <span id={`${id}-key-hint`} className="block text-sm text-ink-muted">{t("keyHint")}</span>
            </div>
            <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} />
            <label className="block max-w-sm space-y-1">
              <span className="block font-semibold">{t("icon")}</span>
              <select value={icon} onChange={(e) => setIcon(e.target.value)} className={fieldInput}>
                {serviceIcons.map((i) => <option key={i} value={i}>{tIcons.has(i) ? tIcons(i) : i}</option>)}
              </select>
            </label>
            <div className="flex flex-wrap gap-2">
              <button type="submit" className={buttonClass} disabled={busy || !valid}>{t("create")}</button>
              <button type="button" className={smallButton} onClick={() => setCreating(false)}>{t("cancel")}</button>
            </div>
          </form>
        )}
        <SaveStatusLine status={status} />
      </section>

      <section aria-labelledby={`${id}-import`} className="space-y-3">
        <h3 id={`${id}-import`} className="text-xl font-semibold">{t("import")}</h3>
        <p className="max-w-2xl">{t("importIntro")}</p>
        <form className="flex flex-wrap items-end gap-3" onSubmit={(e) => (e.preventDefault(), upload())}>
          <div className="space-y-1">
            <label htmlFor={`${id}-file`} className="block font-semibold">{t("packageFile")}</label>
            <input id={`${id}-file`} type="file" accept="application/json,.json" aria-describedby={`${id}-file-hint`}
              onChange={(e) => setFile(e.target.files?.[0] ?? null)} className="block max-w-full text-sm" />
            <span id={`${id}-file-hint`} className="block text-sm text-ink-muted">{t("packageHint")}</span>
          </div>
          <button type="submit" className={buttonClass} disabled={!file || tooLarge || importer.busy}>{t("check")}</button>
        </form>
        {tooLarge && <p role="alert" className="text-sm text-bad">{t("tooLarge")}</p>}
        <SaveStatusLine status={importer.status} />
        {imports.length > 0 && (
          <>
            <h4 className="font-semibold">{t("imports")}</h4>
            <ul className="space-y-1" data-testid="app-imports">
              {imports.map((i) => (
                <li key={i.id} className="flex flex-wrap gap-2 text-sm">
                  <Link href={`/settings/apps/imports/${i.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">{inLanguage(i.appName, locale).value}</Link>
                  {i.fileName && <span dir="ltr" className="text-ink-muted">{i.fileName}</span>}
                  <span>{t(`status.${i.status}`)}</span>
                </li>
              ))}
            </ul>
          </>
        )}
      </section>
    </div>
  );
}
