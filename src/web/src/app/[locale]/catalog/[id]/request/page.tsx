import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { RequestServiceForm } from "@/components/catalog/RequestServiceForm";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";
import type { PublishedService } from "@/lib/catalog";
import type { TreeNode } from "@/lib/directory";
import type { ResolvedForm } from "@/lib/forms/types";
import type { Locale } from "@/lib/incidents";

export async function generateMetadata({ params }: PageProps<"/[locale]/catalog/[id]/request">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.request" });
  return { title: t("heading") };
}

/** Requesting a service (task C2): its form, required documents and, where allowed, for someone else. */
export default async function RequestServicePage({ params }: PageProps<"/[locale]/catalog/[id]/request">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/catalog/${id}/request`);
  const t = await getTranslations({ locale, namespace: "catalog.request" });
  const token = session.data.accessToken;
  const [service, form, locations] = await Promise.all([
    getJsonAs<PublishedService>(`/api/v1/catalog/published/${id}`, token, tenant),
    getJsonAs<ResolvedForm>(`/api/v1/catalog/published/${id}/request-form?${new URLSearchParams({ language: locale })}`, token, tenant),
    getJsonAs<TreeNode[]>("/api/v1/locations", token, tenant),
  ]);
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
        <RequestServiceForm service={service} initialForm={form} csrfToken={session.data.csrfToken} locations={locations ?? []} />
      )}
    </section>
  );
}
