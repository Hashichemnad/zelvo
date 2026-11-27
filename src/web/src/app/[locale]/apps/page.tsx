import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { App } from "@/lib/apps";
import { iconGlyph } from "@/lib/catalog";
import { inLanguage, type Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/apps">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "apps.home" });
  return { title: t("title") };
}

/** The apps a person may use (task K3): for staff their menu entries, for everyone their portal entries. */
export default async function AppsPage({ params }: PageProps<"/[locale]/apps">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/apps`);
  const t = await getTranslations({ locale, namespace: "apps.home" });
  const apps = ((await getJsonAs<App[]>("/api/v1/apps", session.data.accessToken, tenant)) ?? []).filter((a) => a.active);
  const l = locale as Locale;

  return (
    <section aria-labelledby="apps-home-heading" className="space-y-6">
      <h1 id="apps-home-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-3xl">{t("intro")}</p>
      {apps.length === 0 ? <p>{t("none")}</p> : (
        <ul className="grid gap-4 sm:grid-cols-2" data-testid="app-cards">
          {apps.map((app) => {
            const name = inLanguage(app.name, l);
            const description = inLanguage(app.description, l);
            return (
              <li key={app.id} className="space-y-2 rounded-lg border border-line p-4">
                <h2 className="flex items-center gap-2 text-xl font-semibold">
                  <span aria-hidden="true">{iconGlyph[app.icon] ?? "◆"}</span>
                  <Link href={`/apps/${app.key}`} className="text-brand underline-offset-4 hover:underline"><span lang={name.lang} dir={name.dir}>{name.value}</span></Link>
                </h2>
                {description.value && <p lang={description.lang} dir={description.dir}>{description.value}</p>}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
