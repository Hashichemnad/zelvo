import { redirect } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { PasskeysPanel } from "@/components/identity/PasskeysPanel";
import { getJsonAs } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import type { MyPasskeys } from "@/lib/passkeys";

export async function generateMetadata({ params }: PageProps<"/[locale]/account/security">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "account.security" });
  return { title: t("title") };
}

/** Account → Security (task J3): the person's passkeys. */
export default async function AccountSecurityPage({ params, searchParams }: PageProps<"/[locale]/account/security">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const session = await getSession();
  if (!session) {
    redirect(`/auth/login?${new URLSearchParams({ locale, returnTo: `/${locale}/account/security` })}`);
  }
  const tenant = (await getRequestAuthConfig()).tenant;
  const [t, passkeys, query] = await Promise.all([
    getTranslations({ locale, namespace: "account.security" }),
    getJsonAs<MyPasskeys>("/api/v1/me/passkeys", session.data.accessToken, tenant),
    searchParams,
  ]);
  const outcome = typeof query.passkey === "string" ? query.passkey : null;

  return (
    <section aria-labelledby="security-heading" className="space-y-6">
      <p>
        <Link href="/account" className="text-brand underline">{t("back")}</Link>
      </p>
      <h1 id="security-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      <h2 className="text-xl font-bold">{t("passkeysHeading")}</h2>
      {passkeys ? <PasskeysPanel initial={passkeys} csrfToken={session.data.csrfToken} outcome={outcome} /> : <p role="alert">{t("unavailable")}</p>}
    </section>
  );
}
