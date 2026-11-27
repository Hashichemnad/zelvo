import { getTranslations, setRequestLocale } from "next-intl/server";
import { WellbeingDashboard } from "@/components/wellbeing/Wellbeing";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";
import type { WellbeingGroupSummary, WellbeingSettings } from "@/lib/wellbeing";

export async function generateMetadata({ params }: PageProps<"/[locale]/wellbeing">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "wellbeing" });
  return { title: t("title") };
}

/** The wellbeing dashboard (module 21, task K4): team leads see their groups' agents, others group totals. */
export default async function WellbeingPage({ params }: PageProps<"/[locale]/wellbeing">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/wellbeing`);
  const t = await getTranslations({ locale, namespace: "wellbeing" });
  if (!user?.permissions.includes("wellbeing.read")) {
    return <p role="alert">{t(user ? "noAccess" : "unavailable")}</p>;
  }
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const settings = await get<WellbeingSettings>("/api/v1/wellbeing/settings");
  const [groups, format] = await Promise.all([settings?.enabled ? get<WellbeingGroupSummary[]>("/api/v1/wellbeing/groups") : null, getDisplayFormat()]);
  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
      {!settings ? (
        <p role="alert">{t("unavailable")}</p>
      ) : !settings.enabled ? (
        <p data-testid="wellbeing-off">{t("off")}</p>
      ) : !groups ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <WellbeingDashboard groups={groups} csrfToken={session.data.csrfToken} display={{ locale, digits: format.digits }} />
      )}
    </div>
  );
}
