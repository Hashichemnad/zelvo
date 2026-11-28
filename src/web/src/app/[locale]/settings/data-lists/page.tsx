import { getTranslations, setRequestLocale } from "next-intl/server";
import { DataListsEditor } from "@/components/apps/DataListsEditor";
import { getJsonAs } from "@/lib/api";
import type { DataList } from "@/lib/apps";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/data-lists">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.dataLists" });
  return { title: t("title") };
}

/** Settings → Data lists (task K3, apps.manage): tables used as the options of form fields. */
export default async function DataListsSettingsPage({ params }: PageProps<"/[locale]/settings/data-lists">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/data-lists`);
  const [t, tSection] = await Promise.all([
    getTranslations({ locale, namespace: "apps.dataLists" }),
    getTranslations({ locale, namespace: "settings.sections.dataLists" }),
  ]);
  const canManage = (user?.permissions ?? []).includes("apps.manage");
  const lists = canManage ? await getJsonAs<DataList[]>("/api/v1/data-lists", session.data.accessToken, tenant) : null;

  return (
    <section aria-labelledby="data-lists-heading" className="space-y-6">
      <h2 id="data-lists-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!lists ? <p role="alert">{t("noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <DataListsEditor initial={lists} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}
