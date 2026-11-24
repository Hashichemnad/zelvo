import { getTranslations, setRequestLocale } from "next-intl/server";
import { KnowledgeList } from "@/components/knowledge/KnowledgeList";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page } from "@/lib/integrations";
import { isKnowledgeStaff, type ArticleSummary } from "@/lib/knowledge";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/knowledge">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge" });
  return { title: t("title") };
}

/** Knowledge base for staff (module 7): every article and its state. */
export default async function KnowledgePage({ params }: PageProps<"/[locale]/knowledge">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/knowledge`);
  const t = await getTranslations({ locale, namespace: "knowledge" });
  const permissions = user?.permissions ?? [];
  if (!isKnowledgeStaff(permissions)) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const [articles, display] = await Promise.all([
    getJsonAs<Page<ArticleSummary>>("/api/v1/knowledge/articles?pageSize=25", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="knowledge-heading" className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 id="knowledge-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
        <div className="flex flex-wrap gap-2">
          {permissions.includes("knowledge.publish") && (
            <Link href="/knowledge/review" className="rounded-md border border-line px-4 py-2 font-semibold">{t("review.title")}</Link>
          )}
          {permissions.includes("knowledge.write") && (
            <Link href="/knowledge/new" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("newArticle")}</Link>
          )}
        </div>
      </div>
      <p className="max-w-3xl">{t("intro")}</p>
      {articles ? <KnowledgeList initial={articles} display={{ locale, ...display }} /> : <p role="alert">{t("unavailable")}</p>}
    </section>
  );
}
