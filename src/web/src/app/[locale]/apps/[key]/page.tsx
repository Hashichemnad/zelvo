import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { menuHref, type App } from "@/lib/apps";
import { iconGlyph } from "@/lib/catalog";
import { inLanguage, type Locale } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/apps/[key]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "apps.home" });
  return { title: t("title") };
}

/** An app's own page (task K3): its menu entries for staff and its portal entries for people who may raise records. */
export default async function AppPage({ params }: PageProps<"/[locale]/apps/[key]">) {
  const { locale, key } = await params;
  setRequestLocale(locale);
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(key)) notFound();
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/apps/${key}`);
  const t = await getTranslations({ locale, namespace: "apps.home" });
  const app = await getJsonAs<App>(`/api/v1/apps/${key}`, session.data.accessToken, tenant);
  if (!app || !app.active) notFound();
  const l = locale as Locale;
  const name = inLanguage(app.name, l);
  const description = inLanguage(app.description, l);

  return (
    <section aria-labelledby="app-home-heading" className="space-y-6">
      <Link href="/apps" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h1 id="app-home-heading" className="flex items-center gap-2 text-3xl font-bold text-brand">
        <span aria-hidden="true">{iconGlyph[app.icon] ?? "◆"}</span>
        <span lang={name.lang} dir={name.dir}>{name.value}</span>
      </h1>
      {description.value && <p className="max-w-3xl" lang={description.lang} dir={description.dir}>{description.value}</p>}
      {app.portalEntries.length > 0 && (
        <section aria-labelledby="app-portal-heading" className="space-y-3">
          <h2 id="app-portal-heading" className="text-xl font-semibold">{t("portal")}</h2>
          <ul className="grid gap-4 sm:grid-cols-2" data-testid="app-portal-entries">
            {app.portalEntries.map((entry) => {
              const label = inLanguage(entry.label, l);
              const text = inLanguage(entry.description, l);
              return (
                <li key={entry.key} className="space-y-2 rounded-lg border border-line p-4">
                  <h3 className="text-lg font-semibold"><span lang={label.lang} dir={label.dir}>{label.value}</span></h3>
                  {text.value && <p lang={text.lang} dir={text.dir}>{text.value}</p>}
                  <Link href={`/records/${entry.recordType}/new`} className="inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("start")}</Link>
                </li>
              );
            })}
          </ul>
        </section>
      )}
      {app.menuEntries.length > 0 && (
        <nav aria-labelledby="app-menu-heading" className="space-y-3">
          <h2 id="app-menu-heading" className="text-xl font-semibold">{t("menu")}</h2>
          <ul className="space-y-2" data-testid="app-menu-entries">
            {app.menuEntries.map((entry) => {
              const label = inLanguage(entry.label, l);
              return (
                <li key={entry.key}>
                  <Link href={menuHref(entry, app.listViews)} className="font-semibold text-brand underline-offset-4 hover:underline">
                    <span lang={label.lang} dir={label.dir}>{label.value}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        </nav>
      )}
    </section>
  );
}
