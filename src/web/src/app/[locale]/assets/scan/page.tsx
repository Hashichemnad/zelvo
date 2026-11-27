import { getTranslations, setRequestLocale } from "next-intl/server";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { ScanToOpen } from "@/components/assets/ScanToOpen";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets/scan">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.scan" });
  return { title: t("title") };
}

/** Scan an asset label with the phone's camera, in the installable app or the browser (task G3, assets.read). */
export default async function ScanPage({ params }: PageProps<"/[locale]/assets/scan">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/assets/scan`);
  const [t, tList] = await Promise.all([getTranslations({ locale, namespace: "assets.scan" }), getTranslations({ locale, namespace: "assets.list" })]);
  const permissions = user?.permissions ?? [];
  return (
    <section aria-labelledby="scan-heading" className="space-y-6">
      <h1 id="scan-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <AssetsNav current="scan" />
      <p className="max-w-2xl">{t("intro")}</p>
      {permissions.includes("assets.read") || permissions.includes("assets.manage") ? <ScanToOpen /> : <p role="alert">{user ? tList("noAccess") : tList("unavailable")}</p>}
    </section>
  );
}
