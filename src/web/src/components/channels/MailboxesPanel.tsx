"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { errorCodeOf, type Mailbox } from "@/lib/channels";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import { cellClass, tableClass } from "../settings/integrations/ui";
import { MailboxForm } from "./MailboxForm";

/** Settings → Mailboxes: the organisation's mailboxes and a form to add one (task E2). */
export function MailboxesPanel({
  initial,
  csrfToken,
  categories,
  groups,
  display,
}: {
  initial: Mailbox[];
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  display: DateDisplayOptions;
}) {
  const t = useTranslations("channels.mailboxes");
  const [mailboxes, setMailboxes] = useState(initial);
  const [adding, setAdding] = useState(false);

  return (
    <div className="space-y-6" data-testid="mailboxes-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {mailboxes.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className={tableClass}>
            <caption className="sr-only">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col" className={cellClass}>{t("fields.name")}</th>
                <th scope="col" className={cellClass}>{t("fields.address")}</th>
                <th scope="col" className={cellClass}>{t("fields.provider")}</th>
                <th scope="col" className={cellClass}>{t("lastRead")}</th>
              </tr>
            </thead>
            <tbody>
              {mailboxes.map((m) => {
                const code = errorCodeOf(m.lastError);
                return (
                  <tr key={m.id} data-testid="mailbox-row">
                    <td className={cellClass}>
                      <Link href={`/settings/mailboxes/${m.id}`} className="font-semibold text-brand underline">{m.name}</Link>
                      {m.status === "Disabled" && <span className="block text-sm text-ink-muted">{t("statuses.Disabled")}</span>}
                    </td>
                    <td className={cellClass}><span dir="ltr">{m.address}</span></td>
                    <td className={cellClass}>{t(`providers.${m.provider}`)}</td>
                    <td className={cellClass}>
                      {m.lastCheckedAt ? formatDateTime(new Date(m.lastCheckedAt), display) : t("never")}
                      {m.lastCheckSucceeded === false && <span className="block text-bad">{t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other")}</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {adding ? (
        <section className="space-y-3 rounded-md border border-line p-4" aria-label={t("addTitle")}>
          <h2 className="text-lg font-semibold">{t("addTitle")}</h2>
          <MailboxForm mailbox={null} csrfToken={csrfToken} categories={categories} groups={groups} onSaved={(saved) => { setMailboxes([...mailboxes, saved]); setAdding(false); }} />
        </section>
      ) : (
        <button type="button" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface" onClick={() => setAdding(true)}>{t("addTitle")}</button>
      )}
    </div>
  );
}
