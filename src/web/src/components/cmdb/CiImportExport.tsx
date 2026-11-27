"use client";

import { useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { ApiError, postForm } from "@/lib/client-api";
import { formatNumber, type DigitSystem } from "@/lib/format";
import type { BulkImportResult } from "@/lib/integrations";
import { cellClass, primaryButton, secondaryButton, tableClass } from "../settings/integrations/ui";

const columns = ["number", "class", "name", "descriptionEn", "descriptionAr", "status", "ownerGroup", "location", "attributes"] as const;

/**
 * Settings → CMDB → Import and export (task G2): configuration items to CSV or Excel, and an
 * import checked first and applied only when every row is right. Rows without a number are matched
 * by the class's identification rules; attribute values come as JSON or attr.&lt;key&gt; columns.
 */
export function CiImportExport({ csrfToken, canImport, locale, digits }: { csrfToken: string; canImport: boolean; locale: string; digits: DigitSystem }) {
  const t = useTranslations("cmdb.import");
  const tCommon = useTranslations("common");
  const id = useId();
  const file = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<"check" | "import" | null>(null);
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const number = (value: number) => formatNumber(value, locale, digits);
  const download = (format: "csv" | "xlsx", kind: "export" | "template") => `/bff/api/v1/bulk/configuration-items/${kind}?format=${format}`;

  async function send(dryRun: boolean) {
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
      setResult((await postForm<BulkImportResult>(`/v1/bulk/configuration-items/import?dryRun=${dryRun}`, form, csrfToken, [422])).body);
    } catch (refused) {
      setError(refused instanceof ApiError && refused.rateLimited ? tCommon("rateLimited") : refused instanceof ApiError && refused.status === 400 ? t("badFile") : t("failed"));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-4" data-testid="ci-import-export">
      <div className="flex flex-wrap gap-3">
        <a href={download("csv", "export")} className={primaryButton} download>{t("exportCsv")}</a>
        <a href={download("xlsx", "export")} className={secondaryButton} download>{t("exportExcel")}</a>
        <a href={download("csv", "template")} className={secondaryButton} download>{t("template")}</a>
      </div>
      <details className="max-w-3xl">
        <summary className="cursor-pointer font-semibold">{t("columnsTitle")}</summary>
        <dl className="mt-2 grid gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
          {columns.map((column) => (
            <div key={column} className="contents">
              <dt><code dir="ltr" lang="en">{column}</code></dt>
              <dd>{t(`columns.${column}`)}</dd>
            </div>
          ))}
          <div className="contents">
            <dt><code dir="ltr" lang="en">attr.&lt;key&gt;</code></dt>
            <dd>{t("columns.attr")}</dd>
          </div>
        </dl>
      </details>
      {canImport && (
        <form onSubmit={(e) => { e.preventDefault(); void send(false); }} className="max-w-3xl space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-import`}>
          <h4 id={`${id}-import`} className="font-semibold">{t("importTitle")}</h4>
          <p className="text-sm">{t("importHelp")}</p>
          <div>
            <label htmlFor={`${id}-file`} className="font-semibold">{t("file")}</label>
            <input id={`${id}-file`} ref={file} type="file" accept=".csv,.xlsx" className="block max-w-full" onChange={() => setResult(null)} />
          </div>
          <div className="flex flex-wrap gap-3">
            <button type="button" onClick={() => void send(true)} disabled={busy !== null} className={secondaryButton}>{busy === "check" ? t("checking") : t("check")}</button>
            <button type="submit" disabled={busy !== null} className={primaryButton}>{busy === "import" ? t("importing") : t("import")}</button>
          </div>
          <div role="status" aria-live="polite" data-testid="ci-import-result">
            {error && <p className="text-bad">{error}</p>}
            {result && (
              <p className={result.errors.length > 0 ? "text-bad" : "text-ok"}>
                {result.applied ? t("applied", { rows: number(result.rows) }) : result.dryRun ? t("checked", { rows: number(result.rows) }) : t("notApplied")}{" "}
                {t("counts", { created: number(result.created), updated: number(result.updated), unchanged: number(result.unchanged) })}
              </p>
            )}
          </div>
          {result && result.errors.length > 0 && (
            <div className="overflow-x-auto">
              <table className={tableClass}>
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
                      <td className={cellClass}>{t.has(`errorCodes.${problem.code}`) ? t(`errorCodes.${problem.code}`) : problem.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </form>
      )}
    </div>
  );
}
