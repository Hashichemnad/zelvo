import { getTranslations, setRequestLocale } from "next-intl/server";
import { DepartmentsPanel } from "@/components/directory/DepartmentsPanel";
import { getJsonAs } from "@/lib/api";
import type { Department } from "@/lib/directory";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/departments">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.sections.departments" });
  return { title: t("title") };
}

/** Settings → Departments (task C1): people belong to one; forms fill it in from the profile. */
export default async function DepartmentsPage({ params }: PageProps<"/[locale]/settings/departments">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/departments`);
  const [t, tSection, tSettings] = await Promise.all([
    getTranslations({ locale, namespace: "directory.departments" }),
    getTranslations({ locale, namespace: "settings.sections.departments" }),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  const departments = user?.permissions.includes("directory.read")
    ? await getJsonAs<Department[]>("/api/v1/departments?includeInactive=true", session.data.accessToken, tenant)
    : null;

  return (
    <section aria-labelledby="departments-heading" className="space-y-4">
      <h2 id="departments-heading" className="text-2xl font-bold">{tSection("title")}</h2>
      {!departments ? (
        <p role="alert">{tSettings(user ? "noAccess" : "unavailable")}</p>
      ) : (
        <>
          <p className="max-w-2xl">{t("intro")}</p>
          <DepartmentsPanel initial={departments} csrfToken={session.data.csrfToken} canManage={user!.permissions.includes("directory.manage")} />
        </>
      )}
    </section>
  );
}
