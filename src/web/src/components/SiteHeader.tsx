import { hasRecordPermission } from "@/lib/records";
import { getLocale, getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getBranding, getCurrentUser, getDemo, getSystemInfo } from "@/lib/api";
import { brandingImagePath } from "@/lib/branding";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import { getDisplayFormat } from "@/lib/display";
import { isKnowledgeStaff } from "@/lib/knowledge";
import { canDecideAiActions } from "@/lib/mcp";
import { formatDateTime } from "@/lib/format";
import { canSeeReports, seesDashboards } from "@/lib/reports";
import { sectionsFor } from "@/lib/settings-sections";
import { getJsonAs } from "@/lib/api";
import { toDigitSystem } from "@/lib/format";
import { AccountMenu } from "./AccountMenu";
import { CollapsibleNav } from "./pwa/CollapsibleNav";
import { NotificationBell } from "./notifications/NotificationBell";
import { DemoPanel } from "./DemoPanel";
import { LanguageSwitcher } from "./LanguageSwitcher";
import { TestInstallationBanner } from "./TestInstallationBanner";
import { ActivationBanner } from "./ActivationBanner";
import { MaintenanceBanner } from "./MaintenanceBanner";
import { moduleShown, modules } from "@/lib/license";

export async function SiteHeader() {
  const t = await getTranslations("common");
  const tIncidents = await getTranslations("incidents.nav");
  const tKnowledge = await getTranslations("knowledge.nav");
  const tApprovals = await getTranslations("aiApprovals");
  const tChats = await getTranslations("channels.agent");
  const tCatalog = await getTranslations("catalog.nav");
  const tVoice = await getTranslations("voice.nav");
  const tPortal = await getTranslations("portal.nav");
  const tReports = await getTranslations("reports.nav");
  const tCmdb = await getTranslations("cmdb");
  const tAssets = await getTranslations("assets");
  const tWellbeing = await getTranslations("wellbeing");
  const tProblems = await getTranslations("problems");
  const tRecords = await getTranslations("records");
  const config = await getRequestAuthConfig();
  const locale = await getLocale();
  // Task F1: the tenant's product name and logos (light and dark mode) instead of Zelvo's.
  const branding = await getBranding(config.tenant);
  const product = (locale === "ar" ? branding?.productName.ar ?? branding?.productName.en : branding?.productName.en ?? branding?.productName.ar) ?? t("productName");
  const logoLight = brandingImagePath(config.tenant, "LogoLight", branding?.images.logoLight);
  const logoDark = brandingImagePath(config.tenant, "LogoDark", branding?.images.logoDark) ?? logoLight;
  const session = await getSession(config).catch(() => null);
  // Task F3: links to modules the license does not include are left out.
  const licensed = (await getSystemInfo())?.modules;
  const user = session ? await getCurrentUser(session.data.accessToken, config.tenant) : null;
  // Demo installations only: the API says whether this is one.
  const demo = config.demoTenants.length > 0 ? await getDemo(config.tenant) : null;
  const unread = session && user
    ? await getJsonAs<{ count: number }>("/api/v1/me/notifications/unread-count", session.data.accessToken, config.tenant)
    : null;
  let lastReset: string | null = null;
  if (demo?.lastResetAt) {
    const format = await getDisplayFormat();
    lastReset = formatDateTime(new Date(demo.lastResetAt), { locale, ...format });
  }

  return (
    <>
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-4 py-3">
          <div className="flex flex-wrap items-center gap-4">
            {/* Task D2: the product name leads to the portal's home. */}
            <Link href="/" className="flex items-center gap-2 text-lg font-bold text-brand" data-testid="product-name">
              {logoLight && (
                <picture>
                  {logoDark && <source srcSet={logoDark} media="(prefers-color-scheme: dark)" />}
                  {/* The product name beside it says what the logo shows. */}
                  <img src={logoLight} alt="" className="h-8 w-auto" />
                </picture>
              )}
              <span>{product}</span>
            </Link>
            <CollapsibleNav label={tIncidents("label")} menuLabel={t("menu")}>
              <ul className="flex flex-col gap-3 py-2 text-sm font-semibold sm:flex-row sm:flex-wrap sm:py-0">
                {moduleShown(licensed, modules.knowledge) && (
                  <li>
                    <Link href="/help" className="text-brand underline-offset-4 hover:underline">
                      {tKnowledge("help")}
                    </Link>
                  </li>
                )}
                {moduleShown(licensed, modules.serviceCatalog) && (
                  <li>
                    <Link href="/catalog" className="text-brand underline-offset-4 hover:underline">
                      {tCatalog("catalog")}
                    </Link>
                  </li>
                )}
                {moduleShown(licensed, modules.customerVoice) && (
                  <li>
                    <Link href="/contact" className="text-brand underline-offset-4 hover:underline">
                      {tVoice("contact")}
                    </Link>
                  </li>
                )}
                {user && (
                  <>
                  {moduleShown(licensed, modules.portal) && (
                    <li>
                      <Link href="/my-tickets" className="text-brand underline-offset-4 hover:underline">
                        {tPortal("myTickets")}
                      </Link>
                    </li>
                  )}
                  {user.permissions.includes("voice.read") && (
                    <li>
                      <Link href="/customer-voice" className="text-brand underline-offset-4 hover:underline">
                        {tVoice("staff")}
                      </Link>
                    </li>
                  )}
                  <li>
                    <Link href="/approvals" className="text-brand underline-offset-4 hover:underline">
                      {t("approvals")}
                    </Link>
                  </li>
                  {user.permissions.includes("requests.edit") && (
                    <li>
                      <Link href="/tasks" className="text-brand underline-offset-4 hover:underline">
                        {tCatalog("myTasks")}
                      </Link>
                    </li>
                  )}
                  {user.permissions.includes("requests.read") && (
                    <li>
                      <Link href="/requests" className="text-brand underline-offset-4 hover:underline">
                        {tCatalog("requests")}
                      </Link>
                    </li>
                  )}
                  {user.permissions.includes("payments.read") && (
                    <li>
                      <Link href="/payments" className="text-brand underline-offset-4 hover:underline">
                        {t("payments")}
                      </Link>
                    </li>
                  )}
                  {/* Task F5: data subject requests for privacy officers. */}
                  {user.permissions.includes("privacy.requests") && (
                    <li>
                      <Link href="/privacy-requests" className="text-brand underline-offset-4 hover:underline" data-testid="nav-privacy-requests">
                        {t("privacyRequests")}
                      </Link>
                    </li>
                  )}
                  {user.permissions.includes("incidents.read") && (
                    <>
                      <li>
                        <Link href="/workspace" className="text-brand underline-offset-4 hover:underline">
                          {tIncidents("workspace")}
                        </Link>
                      </li>
                      <li>
                        <Link href="/incidents" className="text-brand underline-offset-4 hover:underline">
                          {tIncidents("incidents")}
                        </Link>
                      </li>
                    </>
                  )}
                  {user.permissions.includes("cmdb.read") && (
                    <li>
                      <Link href="/cmdb" className="text-brand underline-offset-4 hover:underline" data-testid="nav-cmdb">
                        {tCmdb("nav")}
                      </Link>
                    </li>
                  )}
                  {(user.permissions.includes("assets.read") || user.permissions.includes("assets.manage")) && (
                    <li>
                      <Link href="/assets" className="text-brand underline-offset-4 hover:underline" data-testid="nav-assets">
                        {tAssets("headerNav")}
                      </Link>
                    </li>
                  )}
                  {/* Task H1: problem management and the known error database. */}
                  {user.permissions.includes("problems.read") && (
                    <li>
                      <Link href="/problems" className="text-brand underline-offset-4 hover:underline" data-testid="nav-problems">
                        {tProblems("nav")}
                      </Link>
                    </li>
                  )}
                  {/* Task K2: custom record types, for staff of any type. */}
                  {hasRecordPermission(user.permissions) && (
                    <li>
                      <Link href="/records" className="text-brand underline-offset-4 hover:underline" data-testid="nav-records">
                        {tRecords("nav")}
                      </Link>
                    </li>
                  )}
                  {isKnowledgeStaff(user.permissions) && (
                    <li>
                      <Link href="/knowledge" className="text-brand underline-offset-4 hover:underline">
                        {tKnowledge("knowledge")}
                      </Link>
                    </li>
                  )}
                  {user.permissions.includes("chat.handle") && (
                    <li>
                      <Link href="/chats" className="text-brand underline-offset-4 hover:underline">
                        {tChats("nav")}
                      </Link>
                    </li>
                  )}
                  {/* Task F2: dashboards for leads and executives, "My performance" for other staff. */}
                  {canSeeReports(user.permissions) && (
                    <li>
                      <Link href="/reports" className="text-brand underline-offset-4 hover:underline" data-testid="nav-reports">
                        {seesDashboards(user.permissions) ? tReports("reports") : tReports("myPerformance")}
                      </Link>
                    </li>
                  )}
                  {/* Task K4: the wellbeing dashboard for team leads and above. */}
                  {user.permissions.includes("wellbeing.read") && (
                    <li>
                      <Link href="/wellbeing" className="text-brand underline-offset-4 hover:underline" data-testid="nav-wellbeing">
                        {tWellbeing("nav")}
                      </Link>
                    </li>
                  )}
                  {canDecideAiActions(user.permissions) && (
                    <li>
                      <Link href="/ai-approvals" className="text-brand underline-offset-4 hover:underline">
                        {tApprovals("nav")}
                      </Link>
                    </li>
                  )}
                  </>
                )}
              </ul>
            </CollapsibleNav>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            {user && unread && (
              <NotificationBell initialCount={unread.count} csrfToken={session?.data.csrfToken} locale={await getLocale()} digits={toDigitSystem(user.preferences.effective.digits)} />
            )}
            <AccountMenu
              userName={session ? (session.data.name ?? session.data.email ?? session.data.subject) : null}
              csrfToken={session?.data.csrfToken ?? null}
              showSettings={sectionsFor(user?.permissions ?? []).length > 0}
            />
            <LanguageSwitcher />
          </div>
        </div>
      </header>
      <ActivationBanner />
      <MaintenanceBanner />
      <TestInstallationBanner />
      {demo && (
        <DemoPanel
          demo={demo}
          currentUsername={session?.data.email ?? null}
          csrfToken={session?.data.csrfToken ?? null}
          canReset={user?.permissions.includes("demo.reset") ?? false}
          lastReset={lastReset}
        />
      )}
    </>
  );
}
