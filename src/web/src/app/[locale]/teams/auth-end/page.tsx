import { getTranslations, setRequestLocale } from "next-intl/server";
import { TeamsAuthEnd } from "@/components/teams/TeamsAuthEnd";
import { getSession } from "@/lib/auth/session";

export async function generateMetadata({ params }: PageProps<"/[locale]/teams/auth-end">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "teams.tab" });
  return { title: t("title") };
}

/** Task J4: where the Teams sign-in pop-up ends; hands the session to the tab. */
export default async function TeamsAuthEndPage({ params }: PageProps<"/[locale]/teams/auth-end">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const session = await getSession();
  return <TeamsAuthEnd csrfToken={session?.data.csrfToken ?? null} />;
}
