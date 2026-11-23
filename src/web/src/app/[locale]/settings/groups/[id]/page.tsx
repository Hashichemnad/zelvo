import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { GroupDetailPanel } from "@/components/directory/GroupDetailPanel";
import { Localized } from "@/components/directory/common";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import type { Group } from "@/lib/directory";
import { isId } from "@/lib/incident-pages";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/groups/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.groups" });
  return { title: t("title") };
}

/** One group: details, members, team leads and availability. */
export default async function GroupPage({ params }: PageProps<"/[locale]/settings/groups/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!isId(id)) notFound();
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/groups/${id}`);
  const [t, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.groups" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!user?.permissions.includes("directory.read")) {
    return <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>;
  }
  const group = await getJsonAs<Group>(`/api/v1/groups/${id}`, session.data.accessToken, tenant);
  if (!group) notFound();

  return (
    <section aria-labelledby="group-heading" className="space-y-4">
      <Link href="/settings/groups" className="font-semibold text-brand underline">{t("back")}</Link>
      <h2 id="group-heading" className="text-2xl font-bold">
        <Localized text={group.name} locale={locale as "en" | "ar"} />
      </h2>
      <GroupDetailPanel initial={group} csrfToken={session.data.csrfToken} canManage={user.permissions.includes("directory.manage")} meId={user.id} />
    </section>
  );
}
