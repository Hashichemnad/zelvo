import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { PublishedArticleView } from "@/components/knowledge/PublishedArticleView";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import type { PublishedArticle } from "@/lib/knowledge";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/knowledge/[id]/read">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge" });
  return { title: t("title") };
}

/** A published article as readers see it (also internal articles, for staff). */
export default async function ReadArticlePage({ params }: PageProps<"/[locale]/knowledge/[id]/read">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/knowledge/${id}/read`);
  const t = await getTranslations({ locale, namespace: "knowledge" });
  const [article, display] = await Promise.all([
    getJsonAs<PublishedArticle>(`/api/v1/knowledge/published/${id}`, session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  if (!article) {
    return <p role="alert">{t("notFound")}</p>;
  }
  return (
    <div className="space-y-4">
      <Link href="/knowledge" className="text-sm font-semibold text-brand underline">{t("workspace.back")}</Link>
      <PublishedArticleView article={article} display={{ locale, ...display }} csrfToken={session.data.csrfToken} />
    </div>
  );
}
