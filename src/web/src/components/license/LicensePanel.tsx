"use client";

import { useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { fieldButton } from "@/components/forms/ui";
import { ApiError, postForm } from "@/lib/client-api";
import { formatDateTime, type CalendarSystem, type DigitSystem } from "@/lib/format";
import { problemKey, type LicenseState } from "@/lib/license";

/**
 * The installation's license (module 12, task F3): whether it is activated, the customer, the
 * licensed modules and version, these servers' fingerprint, and the two steps of an activation:
 * download the activation request (sent to Zelvo) and import the license file Zelvo returns. Used
 * by Settings → License and by the first step of the setup wizard (`compact`).
 */
export function LicensePanel({ initial, csrfToken, calendar, digits, timeZone = "Asia/Dubai", compact = false }: {
  initial: LicenseState;
  csrfToken: string;
  calendar: CalendarSystem;
  digits: DigitSystem;
  timeZone?: string;
  compact?: boolean;
}) {
  const t = useTranslations("license");
  const locale = useLocale();
  const id = useId();
  const [state, setState] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const when = (iso: string | null) => (iso ? formatDateTime(new Date(iso), { locale, calendar, digits, timeZone }) : "—");
  const name = state.licensedTo ? (locale === "ar" ? state.licensedTo.ar ?? state.licensedTo.en : state.licensedTo.en ?? state.licensedTo.ar) : null;

  async function download() {
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch("/bff/api/v1/license/activation-request", {
        method: "POST",
        headers: { Accept: "application/json", "X-CSRF-Token": csrfToken },
      });
      if (!response.ok) throw new ApiError("activation request", response.status, null);
      const disposition = response.headers.get("Content-Disposition") ?? "";
      const fileName = /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? "zelvo-activation-request.json";
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName;
      link.click();
      URL.revokeObjectURL(url);
      setMessage({ ok: true, text: t("requestDownloaded", { file: fileName }) });
    } catch {
      setMessage({ ok: false, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  async function importFile(file: File) {
    setBusy(true);
    setMessage(null);
    try {
      const form = new FormData();
      form.append("file", file, file.name);
      const { status, body } = await postForm<LicenseState & { code?: string; type?: string }>("/v1/license/import", form, csrfToken, [422]);
      if (status === 422) {
        const code = body.code ?? body.type?.replace("urn:zelvo:problem:", "") ?? null;
        setMessage({ ok: false, text: t(`problems.${problemKey(code)}`) });
      } else {
        setState(body);
        setMessage({ ok: true, text: t("imported") });
      }
    } catch {
      setMessage({ ok: false, text: t("failed") });
    } finally {
      setBusy(false);
      if (fileInput.current) fileInput.current.value = "";
    }
  }

  return (
    <div className="space-y-6" data-testid="license-panel">
      <div className={`rounded-lg border-2 p-4 ${state.activated ? "border-ok" : "border-warn"}`} role="status" data-testid="license-status">
        <p className={`text-lg font-bold ${state.activated ? "text-ok" : "text-warn"}`}>
          {state.activated ? (state.kind === "Development" ? t("statusDevelopment") : t("statusActivated")) : t("statusNotActivated")}
        </p>
        {!state.activated && <p className="mt-1">{t(`problems.${problemKey(state.problem)}`)}</p>}
        {state.activated && name && <p className="mt-1">{t("licensedTo", { name })}</p>}
      </div>

      {!compact && state.activated && state.kind !== "Development" && (
        <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
          <dt className="font-semibold">{t("licenseId")}</dt><dd dir="ltr" className="text-start">{state.licenseId}</dd>
          <dt className="font-semibold">{t("kind")}</dt><dd>{t(`kinds.${state.kind}`)}</dd>
          <dt className="font-semibold">{t("customerId")}</dt><dd dir="ltr" className="text-start">{state.customerId}</dd>
          <dt className="font-semibold">{t("licensedVersion")}</dt>
          <dd><bdi dir="ltr">{[state.licensedVersion, ...state.upgrades].filter(Boolean).join(", ")}</bdi></dd>
          <dt className="font-semibold">{t("issuedAt")}</dt><dd>{when(state.issuedAt)}</dd>
          <dt className="font-semibold">{t("installedVersion")}</dt><dd><bdi dir="ltr">{state.installedVersion}</bdi></dd>
        </dl>
      )}

      <section aria-labelledby={`${id}-activate`} className="space-y-3">
        <h3 id={`${id}-activate`} className="text-xl font-bold">{t(state.activated ? "replaceTitle" : "activateTitle")}</h3>
        <ol className="list-decimal space-y-3 ps-6">
          <li>
            <p>{t("stepRequest")}</p>
            <button type="button" className={`${fieldButton} mt-2`} disabled={busy} onClick={() => void download()} data-testid="license-download-request">
              {t("downloadRequest")}
            </button>
          </li>
          <li><p>{t("stepSend")}</p></li>
          <li>
            <label htmlFor={`${id}-file`} className="block">{t("stepImport")}</label>
            <input id={`${id}-file`} ref={fileInput} type="file" accept=".json,application/json" disabled={busy}
              className="mt-2 block text-sm file:me-3 file:rounded-md file:border file:border-line file:bg-surface file:px-3 file:py-2"
              onChange={(e) => { const file = e.target.files?.[0]; if (file) void importFile(file); }} data-testid="license-file" />
          </li>
        </ol>
        <p className="text-sm text-ink-muted">{t("upgradeHint")}</p>
        {message && (
          <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-ok" : "text-danger"} data-testid="license-message">{message.text}</p>
        )}
      </section>

      {!compact && (
        <>
          <section aria-labelledby={`${id}-modules`} className="space-y-2">
            <h3 id={`${id}-modules`} className="text-xl font-bold">{t("modulesTitle")}</h3>
            <ul className="grid gap-1 sm:grid-cols-2" data-testid="license-modules">
              {state.modules.map((m) => (
                <li key={m.key} className="flex items-center gap-2">
                  <span aria-hidden="true" className={m.licensed ? "text-ok" : "text-ink-muted"}>{m.licensed ? "✓" : "–"}</span>
                  <span>{t(`modules.${m.key}`)}</span>
                  <span className="sr-only">: {t(m.licensed ? "licensed" : "notLicensed")}</span>
                </li>
              ))}
            </ul>
          </section>

          <section aria-labelledby={`${id}-fingerprint`} className="space-y-2">
            <h3 id={`${id}-fingerprint`} className="text-xl font-bold">{t("fingerprintTitle")}</h3>
            <p className="max-w-2xl text-sm">{t("fingerprintIntro")}</p>
            <table className="w-full max-w-3xl text-start text-sm">
              <thead>
                <tr className="border-b border-line">
                  <th scope="col" className="py-2 text-start">{t("part")}</th>
                  <th scope="col" className="py-2 text-start">{t("value")}</th>
                  <th scope="col" className="py-2 text-start">{t("matches")}</th>
                </tr>
              </thead>
              <tbody>
                {state.fingerprint.map((p) => (
                  <tr key={p.key} className="border-b border-line">
                    <th scope="row" className="py-2 text-start font-semibold">{t(`parts.${p.key}`)}</th>
                    <td className="py-2"><bdi dir="ltr" className="break-all">{p.value ?? "—"}</bdi></td>
                    <td className="py-2">{p.matches === null ? "—" : t(p.matches ? "yes" : "no")}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          {state.files.length > 0 && (
            <section aria-labelledby={`${id}-files`} className="space-y-2">
              <h3 id={`${id}-files`} className="text-xl font-bold">{t("filesTitle")}</h3>
              <ul className="space-y-1 text-sm">
                {state.files.map((f) => (
                  <li key={`${f.fileType}-${f.licenseId}-${f.importedAt}`}>
                    {t(f.fileType === "Upgrade" ? "fileUpgrade" : "fileLicense")} <bdi dir="ltr">{f.licenseId}</bdi> · {when(f.importedAt)}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}
      {!compact && <p className="text-sm text-ink-muted">{t("auditNote")}</p>}
    </div>
  );
}
