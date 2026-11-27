"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { textIn, type CiReview, type CiReviewPage, type CiReviewSide } from "@/lib/cmdb";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import type { JsonValue } from "@/lib/forms/types";
import { primaryButton, secondaryButton } from "../settings/integrations/ui";
import { CiStatusBadge } from "./common";

function show(value: JsonValue | undefined): string {
  if (value === undefined || value === null) return "—";
  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/** The values that tell two items apart: the keys they were matched on first, then the others both have. */
function compareKeys(review: CiReview, sides: { attributes: Record<string, JsonValue> | null }[]): string[] {
  const matched = review.matchedOn.split("+").filter((k) => k !== "name");
  const others = new Set<string>();
  for (const side of sides) for (const key of Object.keys(side.attributes ?? {})) if (!matched.includes(key)) others.add(key);
  return [...matched, ...[...others].sort()].slice(0, 12);
}

/**
 * The reconciliation review queue (task G2, cmdb.manage): items that share an identifying value
 * (merge into one, or keep apart, which is never asked again) and records a source reported that
 * match several items (link to one, or make a new item). The values are side by side; each
 * decision is audited.
 */
export function ReviewQueue({ initial, csrfToken, display }: { initial: CiReviewPage; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("cmdb.review");
  const locale = display.locale as Locale;
  const [page, setPage] = useState(initial);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const number = (n: number) => formatNumber(n, display.locale, display.digits);

  async function decide(review: CiReview, path: string, body: object, done: string) {
    setBusy(review.id);
    setStatus(null);
    try {
      await postJson(`/v1/cmdb/review/${review.id}/${path}`, body, csrfToken);
      setPage(await getJson<CiReviewPage>("/v1/cmdb/review?pageSize=25"));
      setStatus({ ok: true, text: done });
    } catch (error) {
      setStatus({
        ok: false,
        text: error instanceof ApiError && error.rateLimited ? t("errors.rate-limited") : error instanceof ApiError && error.status === 404 ? t("errors.gone") : t("errors.failed"),
      });
    } finally {
      setBusy(null);
    }
  }

  const sideName = (side: CiReviewSide) => `${side.number} ${side.name}`;

  return (
    <div className="space-y-6" data-testid="ci-review-queue">
      <p role="status" aria-live="polite" className={status?.ok === false ? "font-semibold text-bad" : "text-ok"}>{status?.text ?? ""}</p>
      <p>{t("count", { count: page.totalCount, shown: number(page.totalCount) })}</p>
      {page.items.length === 0 && <p>{t("empty")}</p>}
      <ul className="space-y-6">
        {page.items.map((review) => {
          const record = review.record;
          const columns: { key: string; heading: string; attributes: Record<string, JsonValue> | null; side?: CiReviewSide }[] = [
            ...(record ? [{ key: "record", heading: t("recordHeading", { source: record.source }), attributes: record.attributes }] : []),
            ...review.items.map((side) => ({ key: side.id, heading: sideName(side), attributes: side.attributes, side })),
          ];
          const keys = compareKeys(review, columns);
          return (
            <li key={review.id} className="space-y-3 rounded-lg border border-line p-4">
              <h3 className="text-lg font-semibold">
                {review.kind === "PossibleDuplicate" ? t("duplicateHeading") : t("ambiguousHeading", { name: record?.name ?? "" })}
              </h3>
              <p className="text-sm">
                {t("matchedOn", { keys: review.matchedOn.replaceAll("+", " + ") })}{" "}
                {review.matchedValue && <code dir="ltr">{review.matchedValue}</code>}{" "}
                <span className="text-ink-muted">{formatDateTime(new Date(review.createdAt), display)}</span>
              </p>
              <div className="overflow-x-auto">
                <table className="min-w-full border-collapse text-sm">
                  <caption className="sr-only">{t("compareCaption")}</caption>
                  <thead>
                    <tr>
                      <th scope="col" className="border-b border-line p-2 text-start">{t("value")}</th>
                      {columns.map((c) => (
                        <th scope="col" key={c.key} className="border-b border-line p-2 text-start">
                          {c.side ? (
                            <Link href={`/cmdb/${c.side.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="auto">{c.heading}</Link>
                          ) : (
                            <span dir="auto">{c.heading}</span>
                          )}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <th scope="row" className="border-b border-line p-2 text-start font-normal">{t("class")}</th>
                      {columns.map((c) => (
                        <td key={c.key} className="border-b border-line p-2">
                          {c.side ? textIn(c.side.class?.path, locale) : textIn(record?.class?.path, locale)}
                        </td>
                      ))}
                    </tr>
                    <tr>
                      <th scope="row" className="border-b border-line p-2 text-start font-normal">{t("statusTickets")}</th>
                      {columns.map((c) => (
                        <td key={c.key} className="border-b border-line p-2">
                          {c.side ? (
                            <>
                              <CiStatusBadge status={c.side.status} /> {t("tickets", { count: c.side.ticketCount, shown: number(c.side.ticketCount) })}
                              {c.side.sources.length > 0 && <span className="block text-ink-muted" dir="auto">{c.side.sources.join(", ")}</span>}
                            </>
                          ) : "—"}
                        </td>
                      ))}
                    </tr>
                    {keys.map((key) => (
                      <tr key={key}>
                        <th scope="row" className="border-b border-line p-2 text-start font-normal"><code dir="ltr">{key}</code></th>
                        {columns.map((c) => <td key={c.key} className="border-b border-line p-2" dir="ltr">{show(c.attributes?.[key])}</td>)}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="flex flex-wrap gap-2">
                {review.kind === "PossibleDuplicate" ? (
                  <>
                    {review.items.map((side) => (
                      <button key={side.id} type="button" className={primaryButton} disabled={busy !== null}
                        onClick={() => void decide(review, "merge", { keepId: side.id }, t("merged"))}>
                        {t("keep", { name: sideName(side) })}
                      </button>
                    ))}
                    <button type="button" className={secondaryButton} disabled={busy !== null} onClick={() => void decide(review, "keep-apart", {}, t("keptApart"))}>
                      {t("keepApart")}
                    </button>
                  </>
                ) : (
                  <>
                    {review.items.map((side) => (
                      <button key={side.id} type="button" className={primaryButton} disabled={busy !== null}
                        onClick={() => void decide(review, "link", { itemId: side.id }, t("linked"))}>
                        {t("link", { name: sideName(side) })}
                      </button>
                    ))}
                    <button type="button" className={secondaryButton} disabled={busy !== null} onClick={() => void decide(review, "create", {}, t("created"))}>
                      {t("createNew")}
                    </button>
                  </>
                )}
              </div>
              {review.kind === "PossibleDuplicate" && <p className="text-sm text-ink-muted">{t("mergeHint")}</p>}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
