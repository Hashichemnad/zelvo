import { getTranslations, setRequestLocale } from "next-intl/server";
import { ConnectorsPanel } from "@/components/connectors/ConnectorsPanel";
import { getJsonAs } from "@/lib/api";
import type { Connector } from "@/lib/connectors";
import { loadDirectoryChoices } from "@/lib/directory-pages";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/connectors">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "connectors" });
  return { title: t("title") };
}

/** Settings → Connectors (module 14, task E7): identity directories, CRMs and REST connectors. */
export default async function ConnectorsPage({ params }: PageProps<"/[locale]/settings/connectors">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/connectors`);
  const t = await getTranslations({ locale, namespace: "connectors" });
  if (!user?.permissions.includes("connectors.manage")) {
    return null;
  }
  const [connectors, choices, display] = await Promise.all([
    getJsonAs<Connector[]>("/api/v1/connectors", session.data.accessToken, tenant),
    loadDirectoryChoices(session.data.accessToken, tenant, user.permissions),
    getDisplayFormat(),
  ]);
  if (!connectors) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <section aria-labelledby="connectors-heading" className="space-y-4">
      <h2 id="connectors-heading" className="text-2xl font-semibold">{t("title")}</h2>
      <ConnectorsPanel initial={connectors} csrfToken={session.data.csrfToken} groups={choices.groups ?? []} display={{ locale, ...display }} />
    </section>
  );
}
