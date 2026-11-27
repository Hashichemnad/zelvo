"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { smallButton } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { deleteRequest, getJson, putJson } from "@/lib/client-api";
import { textIn, type TicketCi } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";
import { CiLookup } from "./CiLookup";
import { AssetScanner } from "@/components/assets/AssetScanner";
import { CiStatusBadge, CmdbStatusLine, useSaver } from "./common";

/**
 * The configuration items a ticket is about (task G1), on staff incident and service request
 * pages and in the agent workspace: linked to the item's page for holders of cmdb.read, added by
 * number or name and removed by people who may change the ticket.
 */
export function TicketCiPanel({
  ticketId,
  kind,
  canEdit,
  canOpenItems,
  canScan = false,
  csrfToken,
}: {
  ticketId: string;
  kind: "incidents" | "service-requests" | "problems";
  canEdit: boolean;
  /** cmdb.read: item names link to their pages. */
  canOpenItems: boolean;
  /** Task G3 (assets.read): a scanned asset label adds its item to the ticket. */
  canScan?: boolean;
  csrfToken: string;
}) {
  const t = useTranslations("cmdb.ticketPanel");
  const locale = useLocale() as Locale;
  const id = useId();
  const [links, setLinks] = useState<TicketCi[] | null>(null);
  const [failed, setFailed] = useState(false);
  const { status, run, busy } = useSaver();
  const base = `/v1/${kind}/${ticketId}/configuration-items`;

  useEffect(() => {
    let live = true;
    getJson<TicketCi[]>(base)
      .then((list) => live && (setLinks(list), setFailed(false)))
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [base]);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="ticket-ci-panel">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      {failed && <p role="alert">{t("failed")}</p>}
      {links && links.length === 0 && <p>{t("none")}</p>}
      {links && links.length > 0 && (
        <ul className="space-y-2">
          {links.map(({ item }) => {
            const name = `${item.number} ${item.name}`;
            return (
              <li key={item.id} className="flex flex-wrap items-center gap-2">
                {canOpenItems ? (
                  <Link href={`/cmdb/${item.id}`} className="font-semibold text-brand underline-offset-4 hover:underline" dir="auto">{name}</Link>
                ) : (
                  <span className="font-semibold" dir="auto">{name}</span>
                )}
                <span className="text-sm text-ink-muted">{textIn(item.class.name, locale)}</span>
                {item.status !== "Active" && <CiStatusBadge status={item.status} />}
                {canEdit && (
                  <button type="button" className={smallButton} disabled={busy}
                    onClick={() => void run(async () => {
                      await deleteRequest(`${base}/${item.id}`, csrfToken);
                      setLinks((current) => current?.filter((l) => l.item.id !== item.id) ?? null);
                    })}>
                    {t("remove", { name })}
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {canEdit && links && (
        <CiLookup id={`${id}-add`} label={t("add")} exclude={links.map((l) => l.item.id)} disabled={busy}
          onPick={(item) => void run(async () => setLinks(await putJson<TicketCi[]>(`${base}/${item.id}`, {}, csrfToken)))} />
      )}
      {canEdit && canScan && links && (
        <details>
          <summary className="cursor-pointer text-sm font-semibold">{t("scan")}</summary>
          <div className="mt-2">
            <AssetScanner onFound={(asset) => void run(async () => setLinks(await putJson<TicketCi[]>(`${base}/${asset.id}`, {}, csrfToken)))} />
          </div>
        </details>
      )}
      <CmdbStatusLine status={status} />
    </section>
  );
}
