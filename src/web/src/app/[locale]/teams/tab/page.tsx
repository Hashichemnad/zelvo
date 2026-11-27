import { redirect } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { TeamsTab } from "@/components/teams/TeamsTab";
import { getSession } from "@/lib/auth/session";
import { teamsViewOf, teamsViews } from "@/lib/teams";

export async function generateMetadata({ params }: PageProps<"/[locale]/teams/tab">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "teams.tab" });
  return { title: t("title") };
}

/**
 * Task J4: the Zelvo tab in Microsoft Teams (`staticTabs` of the Teams app). Signed in, it opens
 * the chosen page (My tickets, the agent's list or approvals) with the web app's phone layouts;
 * otherwise it offers sign-in in a pop-up.
 */
export default async function TeamsTabPage({ params, searchParams }: PageProps<"/[locale]/teams/tab">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const view = teamsViewOf((await searchParams).view);
  const target = `/${locale}${teamsViews[view]}`;
  if (await getSession()) {
    redirect(target);
  }
  return <TeamsTab target={target} />;
}
