import { getTranslations, setRequestLocale } from "next-intl/server";
import { HelpCenter } from "@/components/knowledge/HelpCenter";
import { loadHelp } from "@/lib/knowledge-pages";
import type { Locale } from "@/lib/incidents";
import type { Page } from "@/lib/integrations";
import type { PublishedSummary } from "@/lib/knowledge";

export async function generateMetadata({ params }: PageProps<"/[locale]/help">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge.help" });
  return { title: t("title") };
}

/** The portal's knowledge base: external articles, for visitors too when the tenant allows it. */
export default async function HelpPage({ params }: PageProps<"/[locale]/help">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "knowledge.help" });
  const { result, signInRequired } = await loadHelp<Page<PublishedSummary>>("/api/v1/knowledge/published?pageSize=25&visibility=External");
  return (
    <section aria-labelledby="help-heading" className="space-y-6">
      <h1 id="help-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {signInRequired ? (
        <p role="alert">
          {t("signInRequired")}{" "}
          <a href={`/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/help` })}`} className="font-semibold text-brand underline">{t("signIn")}</a>
        </p>
      ) : result ? (
        <HelpCenter initial={result} locale={locale as Locale} />
      ) : (
        <p role="alert">{t("failed")}</p>
      )}
    </section>
  );
}
