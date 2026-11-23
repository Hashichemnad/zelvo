"use client";

import { useId, useRef, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, postForm } from "@/lib/client-api";
import { formatNumber, type DigitSystem } from "@/lib/format";
import type { BulkImportResult } from "@/lib/integrations";
import { cellClass, primaryButton, secondaryButton, tableClass } from "./integrations/ui";

const columns = ["email", "displayName", "emiratesId", "roles", "status"] as const;
const errorCodes = new Set([
  "email-required", "email-invalid", "email-duplicate", "email-ambiguous", "display-name-too-long", "emirates-id-invalid", "emirates-id-in-use",
  "emirates-id-duplicate", "role-unknown", "status-invalid", "cannot-disable-self", "last-tenant-admin", "column-unknown", "column-duplicate",
  "column-missing", "field-read-only",
]);

/**
 * Settings → Import and export (module 14): users to CSV or Excel, an empty template, and an
 * import that is checked first and applied only when every row is right.
 */
export function ImportExportPanel({
  csrfToken,
  canImport,
  locale,
  digits,
}: {
  csrfToken: string;
  canImport: boolean;
  locale: string;
  digits: DigitSystem;
}) {
  const t = useTranslations("settings.importExport");
  const tCommon = useTranslations("common");
  const id = useId();
  const file = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<"check" | "import" | null>(null);
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const number = (value: number) => formatNumber(value, locale, digits);

  async function send(dryRun: boolean, event?: FormEvent) {
    event?.preventDefault();
    const chosen = file.current?.files?.[0];
    if (!chosen) {
      setError(t("chooseFile"));
      return;
    }
    setBusy(dryRun ? "check" : "import");
    setError(null);
    setResult(null);
    const form = new FormData();
    form.append("file", chosen);
    try {
      const response = await postForm<BulkImportResult>(`/v1/bulk/users/import?dryRun=${dryRun}`, form, csrfToken, [422]);
      setResult(response.body);
    } catch (refused) {
      setError(
        refused instanceof ApiError && refused.rateLimited
          ? tCommon("rateLimited")
          : refused instanceof ApiError && refused.status === 400
            ? t("badFile")
            : t("failed"),
      );
    } finally {
      setBusy(null);
    }
  }

  const download = (format: "csv" | "xlsx", kind: "export" | "template") => `/bff/api/v1/bulk/users/${kind}?format=${format}`;

  return (
    <div className="space-y-6" data-testid="import-export-panel">
      <p className="max-w-3xl">{t("intro")}</p>

      <section aria-labelledby={`${id}-export`} className="max-w-3xl space-y-3 rounded-md border border-line p-4">
        <h3 id={`${id}-export`} className="text-lg font-semibold">{t("exportTitle")}</h3>
        <p>{t("exportHelp")}</p>
        <div className="flex flex-wrap gap-3">
          <a href={download("csv", "export")} className={primaryButton} download>{t("exportCsv")}</a>
          <a href={download("xlsx", "export")} className={secondaryButton} download>{t("exportExcel")}</a>
        </div>
      </section>

      <section aria-labelledby={`${id}-columns`} className="max-w-3xl space-y-3">
        <h3 id={`${id}-columns`} className="text-lg font-semibold">{t("templateTitle")}</h3>
        <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
          {columns.map((column) => (
            <div key={column} className="contents">
              <dt><code dir="ltr" lang="en">{column}</code></dt>
              <dd>{t(`columns.${column}`)}</dd>
            </div>
          ))}
        </dl>
        <div className="flex flex-wrap gap-3">
          <a href={download("csv", "template")} className={secondaryButton} download>{t("template")} (CSV)</a>
          <a href={download("xlsx", "template")} className={secondaryButton} download>{t("template")} (Excel)</a>
        </div>
      </section>

      {canImport ? (
        <form onSubmit={(e) => send(false, e)} className="max-w-3xl space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-import`}>
          <h3 id={`${id}-import`} className="text-lg font-semibold">{t("importTitle")}</h3>
          <p>{t("importHelp")}</p>
          <div>
            <label htmlFor={`${id}-file`} className="font-semibold">{t("file")}</label>
            <input id={`${id}-file`} ref={file} type="file" accept=".csv,.xlsx" className="block" onChange={() => setResult(null)} />
          </div>
          <div className="flex flex-wrap gap-3">
            <button type="button" onClick={() => send(true)} disabled={busy !== null} className={secondaryButton}>
              {busy === "check" ? t("checking") : t("check")}
            </button>
            <button type="submit" disabled={busy !== null} className={primaryButton}>
              {busy === "import" ? t("importing") : t("import")}
            </button>
          </div>
          <div role="status" aria-live="polite" data-testid="import-result">
            {error && <p className="text-bad">{error}</p>}
            {result && (
              <div className="space-y-2">
                <p className={result.errors.length > 0 ? "text-bad" : "text-ok"}>
                  {result.applied ? t("applied", { rows: result.rows }) : result.dryRun ? t("checked", { rows: result.rows }) : t("notApplied")}
                </p>
                <ul className="list-disc ps-5">
                  <li>{t("created", { count: number(result.created) })}</li>
                  <li>{t("updated", { count: number(result.updated) })}</li>
                  <li>{t("unchanged", { count: number(result.unchanged) })}</li>
                </ul>
              </div>
            )}
          </div>
          {result && result.errors.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass} data-testid="import-errors">
                <caption className="text-start font-semibold">{t("errorsTitle")}</caption>
                <thead>
                  <tr>
                    <th scope="col" className={cellClass}>{t("row")}</th>
                    <th scope="col" className={cellClass}>{t("column")}</th>
                    <th scope="col" className={cellClass}>{t("problem")}</th>
                  </tr>
                </thead>
                <tbody>
                  {result.errors.map((problem, index) => (
                    <tr key={`${problem.row}-${problem.column}-${index}`}>
                      <td className={cellClass}>{number(problem.row)}</td>
                      <td className={cellClass}><code dir="ltr" lang="en">{problem.column ?? ""}</code></td>
                      <td className={cellClass}>{errorCodes.has(problem.code) ? t(`errorCodes.${problem.code}`) : problem.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </form>
      ) : (
        <p>{t("noImport")}</p>
      )}
    </div>
  );
}
