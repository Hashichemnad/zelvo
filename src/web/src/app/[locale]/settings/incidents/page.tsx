import { getTranslations, setRequestLocale } from "next-intl/server";
import { IncidentSettingsForm } from "@/components/settings/IncidentSettingsForm";
import { getJsonAs } from "@/lib/api";
import type { IncidentSettings } from "@/lib/incidents";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/incidents">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.incidents" });
  return { title: t("title") };
}

/** Settings → Incidents (module 1): priority matrix, on-hold reasons, number prefix, auto-close. */
export default async function IncidentSettingsPage({ params }: PageProps<"/[locale]/settings/incidents">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/incidents`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.incidents" }),
    getTranslations({ locale, namespace: "settings.sections.incidents" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const settings = user?.permissions.includes("settings.manage")
    ? await getJsonAs<IncidentSettings>("/api/v1/settings/incidents", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="incident-settings-heading" className="space-y-4">
      <h2 id="incident-settings-heading" className="text-2xl font-bold">
        {tSection("title")}
      </h2>
      {!settings ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <IncidentSettingsForm initial={settings} csrfToken={session.data.csrfToken} />
        </>
      )}
    </section>
  );
}
