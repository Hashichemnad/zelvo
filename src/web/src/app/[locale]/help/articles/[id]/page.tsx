import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { PublishedArticleView } from "@/components/knowledge/PublishedArticleView";
import { Link } from "@/i18n/navigation";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { loadHelp } from "@/lib/knowledge-pages";
import type { PublishedArticle } from "@/lib/knowledge";

export async function generateMetadata({ params }: PageProps<"/[locale]/help/articles/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge.help" });
  return { title: t("title") };
}

/** A published external article in the portal, with "Was this helpful?". */
export default async function HelpArticlePage({ params }: PageProps<"/[locale]/help/articles/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const t = await getTranslations({ locale, namespace: "knowledge.help" });
  const [{ result, signInRequired, csrfToken }, display] = await Promise.all([
    loadHelp<PublishedArticle>(`/api/v1/knowledge/published/${id}`),
    getDisplayFormat(),
  ]);
  if (signInRequired) {
    return (
      <p role="alert">
        {t("signInRequired")}{" "}
        <a href={`/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/help/articles/${id}` })}`} className="font-semibold text-brand underline">{t("signIn")}</a>
      </p>
    );
  }
  if (!result) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/help" className="font-semibold text-brand underline">{t("back")}</Link>
      </section>
    );
  }
  return (
    <div className="space-y-4">
      <Link href="/help" className="text-sm font-semibold text-brand underline">{t("back")}</Link>
      <PublishedArticleView article={result} display={{ locale, ...display }} csrfToken={csrfToken} />
    </div>
  );
}
