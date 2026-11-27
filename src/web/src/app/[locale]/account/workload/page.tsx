import { getTranslations, setRequestLocale } from "next-intl/server";
import { MyWorkload } from "@/components/wellbeing/Wellbeing";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";
import type { MyWellbeing } from "@/lib/wellbeing";

export async function generateMetadata({ params }: PageProps<"/[locale]/account/workload">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "wellbeing.mine" });
  return { title: t("title") };
}

/** Account → My workload (task K4): every agent sees their own figures and the alerts about them. */
export default async function MyWorkloadPage({ params }: PageProps<"/[locale]/account/workload">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/account/workload`);
  const t = await getTranslations({ locale, namespace: "wellbeing.mine" });
  const [mine, format] = await Promise.all([getJsonAs<MyWellbeing>("/api/v1/me/wellbeing", session.data.accessToken, tenant), getDisplayFormat()]);
  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold text-brand">{t("title")}</h1>
      {!mine ? <p role="alert">{t("unavailable")}</p> : <MyWorkload mine={mine} display={{ locale, digits: format.digits }} />}
    </div>
  );
}
