import { getTranslations, setRequestLocale } from "next-intl/server";
import { McpServerPanel } from "@/components/settings/ai/McpServerPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { ApiClient, Page } from "@/lib/integrations";
import type { McpAction, McpServer } from "@/lib/mcp";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/mcp">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("mcp") };
}

/** Settings → AI → MCP server (task E6). */
export default async function McpServerPage({ params }: PageProps<"/[locale]/settings/ai/mcp">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/mcp`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [server, clients, actions, display, t] = await Promise.all([
    getJsonAs<McpServer>("/api/v1/ai/mcp", token, tenant),
    // Listing integrations needs integrations.manage; without it only existing agents are shown.
    user.permissions.includes("integrations.manage") ? getJsonAs<Page<ApiClient>>("/api/v1/integrations/clients?pageSize=100&status=Active", token, tenant) : null,
    getJsonAs<Page<McpAction>>("/api/v1/ai/mcp/actions?pageSize=25", token, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!server) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <McpServerPanel
      initial={server}
      clients={clients?.items ?? []}
      actions={actions?.items ?? []}
      csrfToken={session.data.csrfToken}
      display={{ locale, ...display }}
    />
  );
}
