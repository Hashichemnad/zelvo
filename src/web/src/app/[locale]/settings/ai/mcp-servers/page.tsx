import { getTranslations, setRequestLocale } from "next-intl/server";
import { McpServersPanel } from "@/components/settings/ai/McpServersPanel";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { McpServerConnection } from "@/lib/mcp";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/mcp-servers">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("mcpServers") };
}

/** Settings → AI → MCP servers: customers' own MCP servers (task E6). */
export default async function McpServersPage({ params }: PageProps<"/[locale]/settings/ai/mcp-servers">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/mcp-servers`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const [servers, display, t] = await Promise.all([
    getJsonAs<McpServerConnection[]>("/api/v1/ai/mcp-servers", session.data.accessToken, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!servers) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return <McpServersPanel initial={servers} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />;
}
