import { getTranslations, setRequestLocale } from "next-intl/server";
import { CustomerGroupsPanel } from "@/components/sla/CustomerGroupsPanel";
import { getJsonAs } from "@/lib/api";
import type { CustomerGroup } from "@/lib/sla";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/customer-groups">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.customerGroups" });
  return { title: t("title") };
}

/** Settings → Customer groups (module 8; task B3): requesters with their own SLA policies. */
export default async function CustomerGroupsPage({ params }: PageProps<"/[locale]/settings/customer-groups">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/customer-groups`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "sla.customerGroups" }),
    getTranslations({ locale, namespace: "settings.sections.customerGroups" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const groups = user?.permissions.includes("directory.read")
    ? await getJsonAs<CustomerGroup[]>("/api/v1/sla/customer-groups", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="customer-groups-heading" className="space-y-4">
      <h2 id="customer-groups-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!groups ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <CustomerGroupsPanel initial={groups} csrfToken={session.data.csrfToken} canManage={user!.permissions.includes("sla.manage")} />
        </>
      )}
    </section>
  );
}
