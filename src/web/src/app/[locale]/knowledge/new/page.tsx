import { getTranslations, setRequestLocale } from "next-intl/server";
import { NewArticle } from "@/components/knowledge/NewArticle";
import { Link } from "@/i18n/navigation";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { selectableNodes } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/knowledge/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge" });
  return { title: t("newArticle") };
}

/** A new article, saved as a draft. */
export default async function NewArticlePage({ params }: PageProps<"/[locale]/knowledge/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/knowledge/new`);
  const t = await getTranslations({ locale, namespace: "knowledge" });
  const permissions = user?.permissions ?? [];
  if (!permissions.includes("knowledge.write")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const choices = await loadDirectoryChoices(session.data.accessToken, tenant, permissions);
  return (
    <section aria-labelledby="new-article-heading" className="space-y-6">
      <Link href="/knowledge" className="text-sm font-semibold text-brand underline">{t("workspace.back")}</Link>
      <h1 id="new-article-heading" className="text-3xl font-bold text-brand">{t("newArticle")}</h1>
      <p className="max-w-3xl">{t("newIntro")}</p>
      <NewArticle categories={selectableNodes(choices.categories)} locale={locale as Locale} csrfToken={session.data.csrfToken} />
    </section>
  );
}
