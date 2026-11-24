import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { ServiceView } from "@/components/catalog/ServiceView";
import { Link } from "@/i18n/navigation";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { loadHelp } from "@/lib/knowledge-pages";
import type { PublishedService } from "@/lib/catalog";
import type { Locale } from "@/lib/incidents";

export async function generateMetadata({ params }: PageProps<"/[locale]/catalog/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.browse" });
  return { title: t("title") };
}

/** A service's card (task C2), for whoever may see it; hidden services are not found. */
export default async function ServicePage({ params, searchParams }: PageProps<"/[locale]/catalog/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const t = await getTranslations({ locale, namespace: "catalog.service" });
  const requested = (await searchParams).requested;
  const [{ result }, display] = await Promise.all([loadHelp<PublishedService>(`/api/v1/catalog/published/${id}`), getDisplayFormat()]);
  if (!result) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/catalog" className="font-semibold text-brand underline">{t("back")}</Link>
      </section>
    );
  }
  return (
    <div className="space-y-4">
      <Link href="/catalog" className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      {typeof requested === "string" && <p role="status" className="rounded-md border border-ok p-3">{t("requestedOnBehalf", { number: requested.slice(0, 30) })}</p>}
      <ServiceView service={result} locale={locale as Locale} digits={display.digits}
        signInHref={`/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/catalog/${id}/request` })}`} />
    </div>
  );
}
