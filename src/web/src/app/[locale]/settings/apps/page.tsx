import { getTranslations, setRequestLocale } from "next-intl/server";
import { AppsPanel } from "@/components/apps/AppsPanel";
import { getJsonAs } from "@/lib/api";
import type { App, AppImportSummary } from "@/lib/apps";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/apps">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.apps" });
  return { title: t("title") };
}

/** Settings → Apps (task K3, apps.manage): apps, a new app and importing app packages. */
export default async function AppsSettingsPage({ params }: PageProps<"/[locale]/settings/apps">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/apps`);
  const [t, tSection] = await Promise.all([
    getTranslations({ locale, namespace: "apps.settings" }),
    getTranslations({ locale, namespace: "settings.sections.apps" }),
  ]);
  const canManage = (user?.permissions ?? []).includes("apps.manage");
  const token = session.data.accessToken;
  const [apps, imports] = canManage
    ? await Promise.all([getJsonAs<App[]>("/api/v1/apps", token, tenant), getJsonAs<AppImportSummary[]>("/api/v1/app-imports", token, tenant)])
    : [null, null];

  return (
    <section aria-labelledby="apps-heading" className="space-y-6">
      <h2 id="apps-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!apps ? <p role="alert">{t("noAccess")}</p> : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <AppsPanel apps={apps} imports={imports ?? []} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}
