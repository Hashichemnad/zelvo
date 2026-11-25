import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { BundleRequestForm } from "@/components/catalog/BundleRequestForm";
import { RequestServiceForm } from "@/components/catalog/RequestServiceForm";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";
import type { BundleForm, PublishedService, ServiceOffer } from "@/lib/catalog";
import type { TreeNode } from "@/lib/directory";
import type { Answers, ResolvedForm } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";

export async function generateMetadata({ params }: PageProps<"/[locale]/catalog/[id]/request">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.request" });
  return { title: t("heading") };
}

/** Requesting a service (task C2): its form, required documents and, where allowed, for someone else. */
export default async function RequestServicePage({ params, searchParams }: PageProps<"/[locale]/catalog/[id]/request">) {
  const { locale, id } = await params;
  const offerParam = (await searchParams).offer;
  const offerId = typeof offerParam === "string" && isId(offerParam) ? offerParam : null;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/catalog/${id}/request${offerId ? `?offer=${offerId}` : ""}`);
  const t = await getTranslations({ locale, namespace: "catalog.request" });
  const token = session.data.accessToken;
  const language = new URLSearchParams({ language: locale });
  const [service, form, bundle, locations, offer] = await Promise.all([
    getJsonAs<PublishedService>(`/api/v1/catalog/published/${id}`, token, tenant),
    getJsonAs<ResolvedForm>(`/api/v1/catalog/published/${id}/request-form?${language}`, token, tenant),
    // Task C5: a bundle's combined form, and a proactive offer's answers.
    getJsonAs<BundleForm>(`/api/v1/catalog/published/${id}/bundle-form?${language}`, token, tenant),
    getJsonAs<TreeNode[]>("/api/v1/locations", token, tenant),
    offerId ? getJsonAs<ServiceOffer>(`/api/v1/me/offers/${offerId}`, token, tenant) : Promise.resolve(null),
  ]);
  const accepted = offer && offer.service.id === id ? { id: offer.id, answers: (offer.answers ?? {}) as Answers } : null;
  if (!service) {
    return <p role="alert">{t("notFound")}</p>;
  }
  return (
    <section aria-labelledby="request-heading" className="space-y-6">
      <Link href={`/catalog/${id}`} className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      <h1 id="request-heading" className="text-3xl font-bold text-brand">
        {t("heading")}: <Localized text={service.name} locale={locale as Locale} />
      </h1>
      {!service.mayRequest ? (
        <p role="alert">{t(service.state === "Suspended" ? "errors.service-suspended" : service.type === "External" ? "errors.service-external" : "notAvailable")}</p>
      ) : (
        service.kind === "Bundle" && bundle ? (
          <BundleRequestForm service={service} bundle={bundle} csrfToken={session.data.csrfToken} locations={locations ?? []} offer={accepted} />
        ) : (
          <RequestServiceForm service={service} initialForm={form} csrfToken={session.data.csrfToken} locations={locations ?? []} offer={accepted} />
        )
      )}
    </section>
  );
}
