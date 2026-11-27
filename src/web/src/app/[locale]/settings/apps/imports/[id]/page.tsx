import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { ImportReview } from "@/components/apps/ImportReview";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { AppImport } from "@/lib/apps";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/apps/imports/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "apps.review" });
  return { title: t("title") };
}

/** The check and preview of an imported app package, approved or discarded by an administrator (task K3). */
export default async function AppImportPage({ params }: PageProps<"/[locale]/settings/apps/imports/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!/^[0-9a-f-]{36}$/.test(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/apps/imports/${id}`);
  const [t, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "apps.review" }),
    getTranslations({ locale, namespace: "apps.settings" }),
  ]);
  if (!(user?.permissions ?? []).includes("apps.manage")) {
    return <p role="alert">{tSettings("noAccess")}</p>;
  }

  const item = await getJsonAs<AppImport>(`/api/v1/app-imports/${id}`, session.data.accessToken, tenant);
  if (!item) notFound();
  return (
    <section aria-labelledby="import-heading" className="space-y-6">
      <Link href="/settings/apps" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{tSettings("back")}</Link>
      <h2 id="import-heading" className="text-2xl font-bold">{t("title")}</h2>
      <p className="max-w-2xl">{t("intro")}</p>
      <ImportReview key={`${item.status}-${item.decidedAt ?? ""}`} item={item} csrfToken={session.data.csrfToken} />
    </section>
  );
}
