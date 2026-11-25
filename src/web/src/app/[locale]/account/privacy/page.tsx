import { getTranslations, setRequestLocale } from "next-intl/server";
import { MyPrivacyPanel } from "@/components/privacy/MyPrivacyPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { MyPrivacy, PrivacyRequest } from "@/lib/privacy";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/account/privacy">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "privacy.account" });
  return { title: t("title") };
}

/** Account → Privacy (GOV-5, task F5). */
export default async function MyPrivacyPage({ params }: PageProps<"/[locale]/account/privacy">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant } = await requireSignedIn(locale, `/${locale}/account/privacy`);
  const t = await getTranslations({ locale, namespace: "privacy.account" });
  const [privacy, requests, format] = await Promise.all([
    getJsonAs<MyPrivacy>("/api/v1/me/privacy", session.data.accessToken, tenant),
    getJsonAs<PrivacyRequest[]>("/api/v1/me/privacy/requests", session.data.accessToken, tenant),
    getDisplayFormat(),
  ]);
  return (
    <section aria-labelledby="my-privacy-heading" className="space-y-6">
      <p><Link href="/account" className="text-brand underline">{t("back")}</Link></p>
      <h1 id="my-privacy-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <p className="max-w-2xl">{t("intro")}</p>
      {!privacy || !requests
        ? <p role="alert">{t("unavailable")}</p>
        : <MyPrivacyPanel initial={privacy} initialRequests={requests} display={{ locale, ...format }} csrfToken={session.data.csrfToken} />}
    </section>
  );
}
