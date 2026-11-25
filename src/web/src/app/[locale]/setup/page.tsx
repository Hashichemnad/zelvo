import { getTranslations, setRequestLocale } from "next-intl/server";
import { SetupWizard } from "@/components/setup/SetupWizard";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { DepartmentTemplate, SetupState } from "@/lib/reports";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/setup">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "setup" });
  return { title: t("title") };
}

/** The setup wizard (module 10, task F2) for tenant administrators (setup.manage). */
export default async function SetupPage({ params }: PageProps<"/[locale]/setup">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/setup`);
  const t = await getTranslations({ locale, namespace: "setup" });
  const allowed = user?.permissions.includes("setup.manage") ?? false;
  const token = session.data.accessToken;
  const [state, templates, tenantInfo, format] = allowed
    ? await Promise.all([
      getJsonAs<SetupState>("/api/v1/setup", token, tenant),
      getJsonAs<DepartmentTemplate[]>("/api/v1/setup/templates", token, tenant),
      getJsonAs<{ name: { en: string | null; ar: string | null } }>("/api/v1/tenant", token, tenant),
      getDisplayFormat(),
    ])
    : [null, null, null, null];
  return (
    <section aria-labelledby="setup-heading" className="space-y-6">
      <Link href="/settings" className="text-sm font-semibold text-brand underline-offset-4 hover:underline">{t("back")}</Link>
      <h1 id="setup-heading" className="text-3xl font-bold text-brand">{t("title")}</h1>
      {!allowed ? <p role="alert" data-testid="setup-no-access">{t("noAccess")}</p>
        : !state || !templates || !format ? <p role="alert">{t("unavailable")}</p> : (
          <>
            <p className="max-w-3xl">{t("intro")}</p>
            <SetupWizard initial={state} templates={templates} tenantName={tenantInfo?.name ?? null} canRename={user?.permissions.includes("tenant.manage") ?? false}
              csrfToken={session.data.csrfToken} digits={format.digits} />
          </>
        )}
    </section>
  );
}
