import type { Metadata } from "next";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { AccountSetupForm } from "@/components/identity/AccountSetupForm";

export async function generateMetadata({ params }: PageProps<"/[locale]/account-setup">): Promise<Metadata> {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "accountSetup" });
  // The token is in the address's fragment; never send the page's address anywhere.
  return { title: t("title"), referrer: "no-referrer" };
}

/**
 * Task M2: the one-time setup link of a hardened installation's first administrator
 * (`/<lang>/account-setup#<token>`): they choose a password, then sign in and set up the
 * authenticator app.
 */
export default async function AccountSetupPage({ params }: PageProps<"/[locale]/account-setup">) {
  const { locale } = await params;
  setRequestLocale(locale);
  return <AccountSetupForm />;
}
