import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { Localized } from "@/components/directory/common";
import { MarkdownView } from "@/components/knowledge/MarkdownView";
import { inLanguage, type Locale } from "@/lib/incidents";
import { textOf } from "@/lib/directory";
import { formatNumber, type DigitSystem } from "@/lib/format";
import type { PublishedService } from "@/lib/catalog";
import { FeeText, ServiceIcon, ServiceTile } from "./CatalogBrowser";

/**
 * A service's card for requesters (module 2): what it is, documents with templates, the steps of
 * the journey, fee and time, channels and audience, partners, the FAQ, its main service and
 * sub-services, related and prerequisite services, and how to request it.
 */
export function ServiceView({ service, locale, digits, signInHref }: { service: PublishedService; locale: Locale; digits: DigitSystem; signInHref: string }) {
  const t = useTranslations("catalog.service");
  const tEditor = useTranslations("catalog.editor");
  const description = inLanguage(service.description, locale);
  const fileUrl = (fileId: string) => `/bff/api/v1/catalog/published/${service.id}/files/${fileId}`;
  const missingPrerequisite = service.prerequisites.some((p) => !p.completed);
  return (
    <article aria-labelledby="service-heading" className="space-y-8" data-testid="service-view">
      {service.parent && (
        <Link href={`/catalog/${service.parent.id}`} className="text-sm font-semibold text-brand underline-offset-4 hover:underline">
          <Localized text={service.parent.name} locale={locale} />
        </Link>
      )}
      <header className="flex gap-4">
        <ServiceIcon service={service} />
        <div className="space-y-1">
          <p className="font-mono text-sm text-ink-muted" dir="ltr">{service.code}</p>
          <h1 id="service-heading" className="text-3xl font-bold text-brand"><Localized text={service.name} locale={locale} /></h1>
          {textOf(service.summary, locale) && <p className="text-lg"><Localized text={service.summary} locale={locale} /></p>}
        </div>
      </header>

      {service.state === "Suspended" && (
        <p role="alert" className="rounded-md border border-warn p-3">
          <span className="font-semibold">{t("suspended")}</span> <Localized text={service.suspendedMessage} locale={locale} />
        </p>
      )}

      <div className="flex flex-wrap items-center gap-3">
        {service.type === "External" && service.externalUrl ? (
          <a href={service.externalUrl} rel="noopener noreferrer" target="_blank" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">
            {t("openExternal")} <span className="sr-only">{t("newWindow")}</span>
          </a>
        ) : service.mayRequest && !missingPrerequisite ? (
          <Link href={`/catalog/${service.id}/request`} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("request")}</Link>
        ) : service.signInToRequest ? (
          <a href={signInHref} className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("signInToRequest")}</a>
        ) : null}
        {missingPrerequisite && service.mayRequest && <p className="text-warn">{t("prerequisiteFirst")}</p>}
      </div>

      {description.value && (
        <section aria-labelledby="service-description" className="space-y-2">
          <h2 id="service-description" className="text-xl font-semibold">{t("about")}</h2>
          <MarkdownView markdown={description.value} articleId={null} lang={description.lang} dir={description.dir} />
        </section>
      )}

      <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
        {service.type === "Internal" && (
          <div>
            <dt className="font-semibold">{t("fee")}</dt>
            <dd>
              <FeeText paid={service.fee.paid} amountAed={service.fee.amountAed} locale={locale} digits={digits} />
              {textOf(service.fee.notes, locale) && <span className="block text-sm text-ink-muted"><Localized text={service.fee.notes} locale={locale} /></span>}
            </dd>
          </div>
        )}
        {service.days !== null && (
          <div>
            <dt className="font-semibold">{t("time")}</dt>
            <dd>{t("days", { count: service.days, days: formatNumber(service.days, locale, digits) })}</dd>
          </div>
        )}
        {service.channels.length > 0 && (
          <div>
            <dt className="font-semibold">{t("channels")}</dt>
            <dd>{service.channels.map((c) => tEditor(`channelNames.${c}`)).join(locale === "ar" ? "، " : ", ")}</dd>
          </div>
        )}
        {service.audiences.length > 0 && (
          <div>
            <dt className="font-semibold">{t("audience")}</dt>
            <dd>{service.audiences.map((a) => tEditor(`audienceNames.${a}`)).join(locale === "ar" ? "، " : ", ")}</dd>
          </div>
        )}
        {service.department && (
          <div>
            <dt className="font-semibold">{t("department")}</dt>
            <dd><Localized text={service.department} locale={locale} /></dd>
          </div>
        )}
        {service.partners.length > 0 && (
          <div>
            <dt className="font-semibold">{t("partners")}</dt>
            <dd>
              <ul>
                {service.partners.map((p, i) => <li key={i}><Localized text={p.name} locale={locale} /> <span className="text-sm text-ink-muted">({tEditor(`partnerKinds.${p.kind}`)})</span></li>)}
              </ul>
            </dd>
          </div>
        )}
      </dl>

      {service.documents.length > 0 && (
        <section aria-labelledby="service-documents" className="space-y-2">
          <h2 id="service-documents" className="text-xl font-semibold">{t("documents")}</h2>
          <ul className="space-y-2">
            {service.documents.map((d) => (
              <li key={d.key} className="rounded-md border border-line p-3">
                <p className="font-semibold"><Localized text={d.name} locale={locale} /> <span className="text-sm font-normal text-ink-muted">({d.required ? t("mandatory") : t("optional")})</span></p>
                {textOf(d.description, locale) && <p><Localized text={d.description} locale={locale} /></p>}
                {d.fileTypes.length > 0 && <p className="text-sm text-ink-muted">{t("fileTypes")} <span dir="ltr">{d.fileTypes.join(", ")}</span></p>}
                {d.templateFileId && <a href={fileUrl(d.templateFileId)} className="text-sm font-semibold text-brand underline">{t("downloadTemplate")}</a>}
              </li>
            ))}
          </ul>
        </section>
      )}

      {service.steps.length > 0 && (
        <section aria-labelledby="service-steps" className="space-y-2">
          <h2 id="service-steps" className="text-xl font-semibold">{t("steps")}</h2>
          <ol className="list-decimal space-y-1 ps-6">
            {service.steps.map((s, i) => (
              <li key={i}>
                <span className="font-semibold"><Localized text={s.name} locale={locale} /></span>
                {textOf(s.description, locale) && <span className="block text-ink-muted"><Localized text={s.description} locale={locale} /></span>}
              </li>
            ))}
          </ol>
        </section>
      )}

      {service.kind === "Bundle" && (service.bundleItems?.length ?? 0) > 0 && (
        <section aria-labelledby="service-bundle" className="space-y-2" data-testid="service-bundle">
          <h2 id="service-bundle" className="text-xl font-semibold">{t("bundleItems")}</h2>
          <p className="text-sm text-ink-muted">{t("bundleHint")}</p>
          <ol className="space-y-1">
            {service.bundleItems!.map((item) => (
              <li key={item.service.id}>
                <Link href={`/catalog/${item.service.id}`} className="font-semibold text-brand underline"><Localized text={item.service.name} locale={locale} /></Link>
                <span className="ms-2 text-sm text-ink-muted">({item.required ? t("mandatory") : t("optional")} · {t("bundleStage", { number: item.stage + 1 })})</span>
              </li>
            ))}
          </ol>
        </section>
      )}
      {service.prerequisites.length > 0 && (
        <section aria-labelledby="service-prerequisites" className="space-y-2">
          <h2 id="service-prerequisites" className="text-xl font-semibold">{t("prerequisites")}</h2>
          <ul className="space-y-1">
            {service.prerequisites.map((p) => (
              <li key={p.service.id}>
                <Link href={`/catalog/${p.service.id}`} className="text-brand underline"><Localized text={p.service.name} locale={locale} /></Link>
                <span className={`ms-2 text-sm ${p.completed ? "text-ok" : "text-warn"}`}>{p.completed ? t("completed") : t("notCompleted")}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {service.faq.length > 0 && (
        <section aria-labelledby="service-faq" className="space-y-2">
          <h2 id="service-faq" className="text-xl font-semibold">{t("faq")}</h2>
          {service.faq.map((f, i) => {
            const answer = inLanguage(f.answer, locale);
            return (
              <details key={i} className="rounded-md border border-line p-3">
                <summary className="cursor-pointer font-semibold"><Localized text={f.question} locale={locale} /></summary>
                <p className="mt-2 whitespace-pre-line" lang={answer.lang} dir={answer.dir}>{answer.value}</p>
              </details>
            );
          })}
        </section>
      )}

      {service.subServices.length > 0 && (
        <section aria-labelledby="service-children" className="space-y-2">
          <h2 id="service-children" className="text-xl font-semibold">{t("subServices")}</h2>
          <ul className="grid gap-3 sm:grid-cols-2">{service.subServices.map((s) => <ServiceTile key={s.id} service={s} locale={locale} digits={digits} />)}</ul>
        </section>
      )}

      {service.related.length > 0 && (
        <section aria-labelledby="service-related" className="space-y-2">
          <h2 id="service-related" className="text-xl font-semibold">{t("related")}</h2>
          <ul className="grid gap-3 sm:grid-cols-2">{service.related.map((s) => <ServiceTile key={s.id} service={s} locale={locale} digits={digits} />)}</ul>
        </section>
      )}
    </article>
  );
}
