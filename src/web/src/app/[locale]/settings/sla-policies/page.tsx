import { getTranslations, setRequestLocale } from "next-intl/server";
import { SlaPoliciesPanel } from "@/components/sla/SlaPoliciesPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { GroupSummary, TreeNode } from "@/lib/directory";
import type { IncidentSettings } from "@/lib/incidents";
import type { BusinessCalendar, CustomerGroup, SlaPolicy } from "@/lib/sla";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/sla-policies">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.slaPolicies" });
  return { title: t("title") };
}

/** Settings → SLA policies (module 8; task B3): ordered policies with targets, calendars and escalations, and a tester. */
export default async function SlaPoliciesPage({ params }: PageProps<"/[locale]/settings/sla-policies">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/sla-policies`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "sla.policies" }),
    getTranslations({ locale, namespace: "settings.sections.slaPolicies" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const allowed = user?.permissions.includes("directory.read") ?? false;
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [policies, categories, customerGroups, calendars, groups, settings, format] = await Promise.all([
    allowed ? get<SlaPolicy[]>("/api/v1/sla/policies") : Promise.resolve(null),
    allowed ? get<TreeNode[]>("/api/v1/categories?includeInactive=true") : Promise.resolve(null),
    allowed ? get<CustomerGroup[]>("/api/v1/sla/customer-groups") : Promise.resolve(null),
    allowed ? get<BusinessCalendar[]>("/api/v1/sla/calendars") : Promise.resolve(null),
    allowed ? get<GroupSummary[]>("/api/v1/groups?includeInactive=true") : Promise.resolve(null),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    getDisplayFormat(),
  ]);

  return (
    <section aria-labelledby="sla-policies-heading" className="space-y-4">
      <h2 id="sla-policies-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!policies ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <SlaPoliciesPanel
            initial={policies}
            choices={{
              categories: categories ?? [],
              customerGroups: customerGroups ?? [],
              calendars: calendars ?? [],
              groups: groups ?? [],
              priorities: settings?.priorityMatrix.priorities ?? [],
            }}
            csrfToken={session.data.csrfToken}
            canManage={user!.permissions.includes("sla.manage")}
            display={{ locale, ...format }}
          />
        </>
      )}
    </section>
  );
}
