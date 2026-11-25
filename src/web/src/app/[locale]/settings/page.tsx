import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { SetupState } from "@/lib/reports";
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
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings`);
  const t = await getTranslations({ locale, namespace: "settings" });
  const sections = sectionsFor(user?.permissions ?? []);
  // Task F2: administrators are reminded of the setup wizard until they finish it.
  const setup = user?.permissions.includes("setup.manage") ? await getJsonAs<SetupState>("/api/v1/setup", session.data.accessToken, tenant) : null;

  return (
    <section className="space-y-4">
      {setup && !setup.completedAt && (
        <div role="status" className="rounded-lg border-2 border-brand bg-surface p-4" data-testid="setup-banner">
          <p className="font-semibold">{t("setupBanner.title")}</p>
          <p className="text-sm">{t("setupBanner.text", { done: setup.finished, total: setup.total })}</p>
          <Link href="/setup" className="mt-2 inline-block rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("setupBanner.action")}</Link>
        </div>
      )}
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
