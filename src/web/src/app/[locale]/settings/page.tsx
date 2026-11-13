import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { sectionsFor } from "@/lib/settings-sections";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings" });
  return { title: t("title") };
}

/** Settings overview: one card per section the user may open. */
export default async function SettingsPage({ params }: PageProps<"/[locale]/settings">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { user } = await requireSignedIn(locale, `/${locale}/settings`);
  const t = await getTranslations({ locale, namespace: "settings" });
  const sections = sectionsFor(user?.permissions ?? []);

  return (
    <section className="space-y-4">
      <p>{t("intro")}</p>
      <ul className="grid gap-4 sm:grid-cols-2">
        {sections.map((section) => (
          <li key={section.key} className="rounded-lg border border-line bg-surface p-4">
            <h2 className="text-lg font-bold">
              <Link href={section.href} className="text-brand underline-offset-4 hover:underline">
                {t(`sections.${section.key}.title`)}
              </Link>
            </h2>
            <p className="text-sm text-ink-muted">{t(`sections.${section.key}.description`)}</p>
          </li>
        ))}
      </ul>
    </section>
  );
}
