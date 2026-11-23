import { getTranslations, setRequestLocale } from "next-intl/server";
import { TreeEditor } from "@/components/directory/TreeEditor";
import { getJsonAs } from "@/lib/api";
import type { TreeNode } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/locations">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.locations" });
  return { title: t("title") };
}

/** Settings → Locations (task B2): a tree of up to three levels (for example site, building, floor). */
export default async function LocationsPage({ params }: PageProps<"/[locale]/settings/locations">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/locations`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.locations" }),
    getTranslations({ locale, namespace: "settings.sections.locations" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const nodes = user?.permissions.includes("directory.read")
    ? await getJsonAs<TreeNode[]>("/api/v1/locations?includeInactive=true", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="locations-heading" className="space-y-4">
      <h2 id="locations-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!nodes ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <TreeEditor kind="locations" initial={nodes} csrfToken={session.data.csrfToken} canManage={user!.permissions.includes("directory.manage")} />
        </>
      )}
    </section>
  );
}
