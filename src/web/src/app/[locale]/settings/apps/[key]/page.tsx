import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AppEditor, type RoleChoice } from "@/components/apps/AppEditor";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { App, DataList } from "@/lib/apps";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { RecordType } from "@/lib/records";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/apps/[key]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.apps" });
  return { title: t("title") };
}

/** One app in Settings → Apps (task K3). */
export default async function AppSettingsPage({ params }: PageProps<"/[locale]/settings/apps/[key]">) {
  const { locale, key } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(key)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/apps/${key}`);
  const t = await getTranslations({ locale, namespace: "apps.settings" });
  if (!(user?.permissions ?? []).includes("apps.manage")) {
    return <p role="alert">{t("noAccess")}</p>;
  }

  const token = session.data.accessToken;
  const [app, types, lists, roles] = await Promise.all([
    getJsonAs<App>(`/api/v1/apps/${key}`, token, tenant),
    getJsonAs<RecordType[]>("/api/v1/record-types", token, tenant),
    getJsonAs<DataList[]>("/api/v1/data-lists", token, tenant),
    getJsonAs<RoleChoice[]>("/api/v1/roles", token, tenant),
  ]);
  if (!app) notFound();
  const name = inLanguage(app.name, locale as Locale);

  return (
    <section aria-labelledby="app-heading" className="space-y-6">
      <Link href="/settings/apps" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h2 id="app-heading" className="text-2xl font-bold"><span lang={name.lang} dir={name.dir}>{name.value}</span></h2>
      <AppEditor key={app.etag} app={app} types={types ?? []} lists={lists ?? []} roles={roles ?? []} csrfToken={session.data.csrfToken} />
    </section>
  );
}
