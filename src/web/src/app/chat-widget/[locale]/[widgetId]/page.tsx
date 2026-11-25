import { getTranslations, setRequestLocale } from "next-intl/server";
import { WidgetFrame } from "@/components/channels/WidgetFrame";
import { getBranding, getJsonAs } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import type { PublicChatWidget } from "@/lib/channels";
import { isId } from "@/lib/incident-pages";

export async function generateMetadata({ params }: PageProps<"/chat-widget/[locale]/[widgetId]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "channels.chat" });
  return { title: t("dialog") };
}

/** A chat widget in a frame of another website (task E2). Visitors chat without signing in. */
export default async function ChatWidgetPage({ params, searchParams }: PageProps<"/chat-widget/[locale]/[widgetId]">) {
  const { locale, widgetId } = await params;
  const query = await searchParams;
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "channels.chat" });
  const origin = typeof query.origin === "string" ? query.origin : null;
  const config = await getRequestAuthConfig();
  const widget = isId(widgetId)
    ? await getJsonAs<PublicChatWidget>(`/api/v1/chat/widgets/${widgetId}/public${origin ? `?origin=${encodeURIComponent(origin)}` : ""}`, "", config.tenant)
    : null;
  if (!widget || !widget.allowed) {
    return <p role="alert" className="p-4">{t("unavailableWidget")}</p>;
  }
  const poweredBy = await getTranslations({ locale, namespace: "common" });
  // Task F1: the tenant's product name beside the "Powered by Zelvo" mark.
  const branding = await getBranding(config.tenant);
  const product = (locale === "ar" ? branding?.productName.ar ?? branding?.productName.en : branding?.productName.en ?? branding?.productName.ar) ?? null;
  return <WidgetFrame widget={widget} locale={locale} origin={origin} poweredBy={poweredBy("poweredBy")} productName={product} />;
}
