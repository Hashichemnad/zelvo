import { getTranslations, setRequestLocale } from "next-intl/server";
import { ReviewQueue } from "@/components/knowledge/KnowledgeList";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { ArticleSummary } from "@/lib/knowledge";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/knowledge/review">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "knowledge.review" });
  return { title: t("title") };
}

/** Articles waiting for review (knowledge.publish). */
export default async function ReviewPage({ params }: PageProps<"/[locale]/knowledge/review">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/knowledge/review`);
  const t = await getTranslations({ locale, namespace: "knowledge" });
  if (!user?.permissions.includes("knowledge.publish")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const [items, display] = await Promise.all([
    getJsonAs<ArticleSummary[]>("/api/v1/knowledge/review-queue", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="review-heading" className="space-y-6">
      <Link href="/knowledge" className="text-sm font-semibold text-brand underline">{t("workspace.back")}</Link>
      <h1 id="review-heading" className="text-3xl font-bold text-brand">{t("review.title")}</h1>
      <p className="max-w-3xl">{t("review.intro")}</p>
      {items ? <ReviewQueue items={items} display={{ locale, ...display }} /> : <p role="alert">{t("unavailable")}</p>}
    </section>
  );
}
