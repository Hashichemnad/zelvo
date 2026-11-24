import { getTranslations, setRequestLocale } from "next-intl/server";
import { CannedResponsesPanel } from "@/components/workspace/CannedResponsesPanel";
import { getJsonAs } from "@/lib/api";
import type { GroupSummary } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";
import type { CannedResponse } from "@/lib/workspace";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/canned-responses">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.cannedResponses" });
  return { title: t("title") };
}

/**
 * Settings → Canned responses (module 5, task B4): reply wording in English and Arabic.
 * Everyone who works on incidents keeps personal ones; team leads keep their groups'; holders of
 * responses.manage keep the organisation's (decision 2026-11-24).
 */
export default async function CannedResponsesPage({ params }: PageProps<"/[locale]/settings/canned-responses">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/canned-responses`);
  const [tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "settings.sections.cannedResponses" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const permissions = user?.permissions ?? [];
  const allowed = permissions.includes("incidents.edit");
  const [responses, groups] = allowed
    ? await Promise.all([
        getJsonAs<CannedResponse[]>("/api/v1/canned-responses?manage=true", session.data.accessToken, tenant),
        permissions.includes("directory.read") ? getJsonAs<GroupSummary[]>("/api/v1/groups", session.data.accessToken, tenant) : Promise.resolve(null),
      ])
    : [null, null];
  return (
    <section aria-labelledby="canned-heading" className="space-y-4">
      <h2 id="canned-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!responses ? (
        <p role="alert">{tSettings(allowed ? "unavailable" : "noAccess")}</p>
      ) : (
        <CannedResponsesPanel initial={responses} groups={groups ?? []} permissions={permissions} csrfToken={session.data.csrfToken} />
      )}
    </section>
  );
}
