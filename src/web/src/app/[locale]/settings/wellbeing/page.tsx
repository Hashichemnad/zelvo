import { getTranslations, setRequestLocale } from "next-intl/server";
import { WellbeingSettingsPanel } from "@/components/wellbeing/Wellbeing";
import { getJsonAs } from "@/lib/api";
import { requireSignedIn } from "@/lib/signed-in";
import type { WellbeingSettings } from "@/lib/wellbeing";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/wellbeing">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.wellbeing" });
  return { title: t("title") };
}

/** Settings → Wellbeing (task K4): off by default; turned on only after staff were told (UAE PDPL). */
export default async function WellbeingSettingsPage({ params }: PageProps<"/[locale]/settings/wellbeing">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/wellbeing`);
  const [tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.wellbeing" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("wellbeing.manage") ?? false;
  const settings = allowed ? await getJsonAs<WellbeingSettings>("/api/v1/wellbeing/settings", session.data.accessToken, tenant) : null;
  return (
    <section aria-labelledby="wellbeing-heading" className="space-y-4">
      <h2 id="wellbeing-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!settings ? <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p> : <WellbeingSettingsPanel initial={settings} csrfToken={session.data.csrfToken} />}
    </section>
  );
}
