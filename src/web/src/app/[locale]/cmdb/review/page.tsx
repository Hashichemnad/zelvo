import { getTranslations, setRequestLocale } from "next-intl/server";
import { ReviewQueue } from "@/components/cmdb/ReviewQueue";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { CiReviewPage } from "@/lib/cmdb";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/cmdb/review">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "cmdb.review" });
  return { title: t("title") };
}

/** The CMDB reconciliation review queue (task G2, cmdb.manage): possible duplicates and records that match several items. */
export default async function CmdbReviewPage({ params }: PageProps<"/[locale]/cmdb/review">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/cmdb/review`);
  const t = await getTranslations({ locale, namespace: "cmdb.review" });
  const permissions = user?.permissions ?? [];
  const allowed = permissions.includes("cmdb.manage");
  const [page, format] = await Promise.all([
    allowed ? getJsonAs<CiReviewPage>("/api/v1/cmdb/review?pageSize=25", session.data.accessToken, tenant) : Promise.resolve(null),
    getDisplayFormat(),
  ]);

  return (
    <section aria-labelledby="review-heading" className="space-y-6">
      <Link href="/cmdb" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h1 id="review-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {!page ? (
        <p role="alert">{user && !allowed ? t("noAccess") : t("unavailable")}</p>
      ) : (
        <ReviewQueue initial={page} csrfToken={session.data.csrfToken} display={{ locale, ...format }} />
      )}
    </section>
  );
}
