import { notFound } from "next/navigation";
import { hasLocale, NextIntlClientProvider } from "next-intl";
import { setRequestLocale } from "next-intl/server";
import { getDirection, routing } from "@/i18n/routing";
import "../../globals.css";

/**
 * The chat widget's own page (task E2), shown in a frame on other websites: no header or menus,
 * only the chat and the "Powered by Zelvo" line. Which websites may frame it is set per widget.
 */
export default async function ChatWidgetLayout({ children, params }: LayoutProps<"/chat-widget/[locale]">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) {
    notFound();
  }
  setRequestLocale(locale);
  return (
    <html lang={locale} dir={getDirection(locale)}>
      <body className="h-screen bg-surface font-sans antialiased">
        <NextIntlClientProvider>{children}</NextIntlClientProvider>
      </body>
    </html>
  );
}
