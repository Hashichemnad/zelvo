import { getTranslations, setRequestLocale } from "next-intl/server";
import { NewAssetForm } from "@/components/assets/NewAssetForm";
import { AssetsNav } from "@/components/assets/AssetsNav";
import { getJsonAs } from "@/lib/api";
import { assetFamilies } from "@/lib/assets";
import type { CiClass } from "@/lib/cmdb";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/assets/new">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "assets.new" });
  return { title: t("heading") };
}

/** A new asset (task G3, assets.manage): a hardware or software configuration item gets a tag. */
export default async function NewAssetPage({ params }: PageProps<"/[locale]/assets/new">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/assets/new`);
  const t = await getTranslations({ locale, namespace: "assets.list" });
  if (!(user?.permissions ?? []).includes("assets.manage")) {
    return <p role="alert">{user ? t("noAccess") : t("unavailable")}</p>;
  }
  const classes = await getJsonAs<CiClass[]>("/api/v1/cmdb/classes", session.data.accessToken, tenant);
  const top = (classes ?? []).filter((c) => c.parentId === null && assetFamilies.includes(c.family)).map((c) => c.id);
  return (
    <section className="space-y-6">
      <AssetsNav current="assets" />
      <NewAssetForm classIds={top} csrfToken={session.data.csrfToken} />
    </section>
  );
}
