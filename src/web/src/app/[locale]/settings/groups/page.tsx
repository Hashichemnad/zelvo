import { getTranslations, setRequestLocale } from "next-intl/server";
import { GroupsPanel } from "@/components/directory/GroupsPanel";
import { getJsonAs } from "@/lib/api";
import type { GroupSummary } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/groups">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.groups" });
  return { title: t("title") };
}

/** Settings → Groups (task B2): assignment groups with members and team leads. */
export default async function GroupsPage({ params }: PageProps<"/[locale]/settings/groups">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/groups`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.groups" }),
    getTranslations({ locale, namespace: "settings.sections.groups" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const groups = user?.permissions.includes("directory.read")
    ? await getJsonAs<GroupSummary[]>("/api/v1/groups?includeInactive=true", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="groups-heading" className="space-y-4">
      <h2 id="groups-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!groups ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <GroupsPanel groups={groups} csrfToken={session.data.csrfToken} canManage={user!.permissions.includes("directory.manage")} />
        </>
      )}
    </section>
  );
}
