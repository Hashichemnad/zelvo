import { Suspense } from "react";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { SystemStatus } from "@/components/SystemStatus";

export async function generateMetadata({ params }: PageProps<"/[locale]/status">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "status" });
  return { title: t("heading") };
}

/** Live status of the installation's services and its license mode (moved from the starter welcome page when the portal home arrived, task D2). */
export default async function StatusPage({ params }: PageProps<"/[locale]/status">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "status" });
  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold text-brand">{t("heading")}</h1>
      <Suspense fallback={<p role="status">{t("checking")}</p>}>
        <SystemStatus />
      </Suspense>
    </div>
  );
}
