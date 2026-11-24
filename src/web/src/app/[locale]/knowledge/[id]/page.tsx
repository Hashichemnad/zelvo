import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { ArticleWorkspace } from "@/components/knowledge/ArticleWorkspace";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { selectableNodes } from "@/lib/directory";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { isId } from "@/lib/incident-pages";
import { isKnowledgeStaff, type ArticleDetail } from "@/lib/knowledge";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/knowledge/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge" });
  return { title: t("title") };
}

/** One article for staff: its versions, editing, review and history. */
export default async function ArticlePage({ params }: PageProps<"/[locale]/knowledge/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/knowledge/${id}`);
  const t = await getTranslations({ locale, namespace: "knowledge" });
  const permissions = user?.permissions ?? [];
  if (!isKnowledgeStaff(permissions)) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const [detail, choices, display] = await Promise.all([
    getJsonAs<ArticleDetail>(`/api/v1/knowledge/articles/${id}`, session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, permissions),
    getDisplayFormat(),
  ]);
  if (!detail) {
    return (
      <section className="space-y-4">
        <p role="alert">{t("notFound")}</p>
        <Link href="/knowledge" className="font-semibold text-brand underline">{t("workspace.back")}</Link>
      </section>
    );
  }
  return <ArticleWorkspace initial={detail} categories={selectableNodes(choices.categories)} display={{ locale, ...display }} csrfToken={session.data.csrfToken} />;
}
