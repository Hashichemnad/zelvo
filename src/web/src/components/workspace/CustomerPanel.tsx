import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { formatDateTime, formatNumber, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { Customer } from "@/lib/workspace";
import { Localized, StateBadge } from "@/components/incidents/StateBadge";
import { PersonAssets } from "@/components/assets/PersonAssets";

/**
 * The customer beside the ticket (module 5 story): who the requester is, their language and
 * customer groups, and their other incidents, so the agent does not ask the same questions again.
 */
export function CustomerPanel({ customer, display }: { customer: Customer | null; display: DateDisplayOptions }) {
  const t = useTranslations("workspace.customer");
  const tLanguages = useTranslations("display.languages");
  const locale = display.locale as Locale;
  const number = (n: number) => formatNumber(n, display.locale, display.digits);
  return (
    <section aria-labelledby="customer-heading" className="space-y-3 rounded-lg border border-line bg-surface p-3">
      <h2 id="customer-heading" className="text-lg font-bold">{t("title")}</h2>
      {!customer ? (
        <p className="text-ink-muted">{t("unavailable")}</p>
      ) : (
        <>
          <dl className="space-y-1 text-sm">
            <div>
              <dt className="sr-only">{t("name")}</dt>
              <dd className="text-base font-semibold" dir="auto">{customer.person.displayName ?? t("noName")}</dd>
            </div>
            {customer.email && (
              <div className="flex flex-wrap gap-1">
                <dt className="font-semibold">{t("email")}</dt>
                <dd dir="ltr">{customer.email}</dd>
              </div>
            )}
            <div className="flex flex-wrap gap-1">
              <dt className="font-semibold">{t("language")}</dt>
              <dd>{customer.preferredLanguage ? tLanguages(customer.preferredLanguage) : t("languageDefault")}</dd>
            </div>
            {customer.customerGroups.length > 0 && (
              <div className="flex flex-wrap gap-1">
                <dt className="font-semibold">{t("groups")}</dt>
                <dd>
                  {customer.customerGroups.map((g, i) => (
                    <span key={g.id}>
                      {i > 0 && (locale === "ar" ? "، " : ", ")}
                      <Localized {...inLanguage(g.name, locale)} />
                    </span>
                  ))}
                </dd>
              </div>
            )}
          </dl>
          {customer.status === "Disabled" && <p className="text-sm font-semibold text-bad">{t("disabled")}</p>}
          <p className="text-sm">{t("counts", { open: number(customer.openCount), total: number(customer.totalCount) })}</p>
          <h3 className="font-semibold">{t("recent")}</h3>
          {customer.recent.length === 0 ? (
            <p className="text-sm text-ink-muted">{t("noOthers")}</p>
          ) : (
            <ul className="space-y-2 text-sm" data-testid="customer-history">
              {customer.recent.map((ticket) => (
                <li key={ticket.id} className="space-y-1">
                  <Link href={`/workspace/${ticket.id}`} className="font-semibold text-brand underline-offset-4 hover:underline">
                    <span dir="ltr" className="font-mono">{ticket.number}</span> <span dir="auto">{ticket.title}</span>
                  </Link>
                  <p className="flex flex-wrap items-center gap-2 text-ink-muted">
                    <StateBadge state={ticket.state} />
                    {formatDateTime(new Date(ticket.createdAt), display)}
                  </p>
                </li>
              ))}
            </ul>
          )}
          {customer.assets && (
            <>
              <h3 className="font-semibold">{t("assets")}</h3>
              <PersonAssets assets={customer.assets} display={display} staff />
            </>
          )}
        </>
      )}
    </section>
  );
}

/** Where the AI assistant will sit (module 9 features come with task E5). */
export function AiAssistantPlaceholder() {
  const t = useTranslations("workspace.ai");
  return (
    <section aria-labelledby="ai-heading" className="space-y-2 rounded-lg border border-dashed border-line p-3">
      <h2 id="ai-heading" className="text-lg font-bold">{t("title")}</h2>
      <p className="text-sm text-ink-muted">{t("comingSoon")}</p>
    </section>
  );
}
