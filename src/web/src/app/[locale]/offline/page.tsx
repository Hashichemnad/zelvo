import type { Metadata } from "next";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { OfflineView } from "@/components/pwa/OfflineView";

export async function generateMetadata({ params }: PageProps<"/[locale]/offline">): Promise<Metadata> {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "app.offline" });
  return { title: t("title") };
}

/**
 * Task J1: the page the installed app opens without a connection (the service worker keeps it).
 * What it shows comes from the device only: the requester's last-viewed tickets and unsent work.
 */
export default async function OfflinePage({ params }: PageProps<"/[locale]/offline">) {
  const { locale } = await params;
  setRequestLocale(locale);
  return <OfflineView />;
}
