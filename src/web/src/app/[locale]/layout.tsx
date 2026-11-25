import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { hasLocale, NextIntlClientProvider } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { PortalChatLauncher } from "@/components/channels/PortalChatLauncher";
import { PoweredByFooter } from "@/components/PoweredByFooter";
import { PrivacyBanner } from "@/components/privacy/PrivacyBanner";
import { SiteHeader } from "@/components/SiteHeader";
import { getDirection, routing } from "@/i18n/routing";
import { getBranding, getPublishedPrivacyNotice } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { brandingCss, brandingImagePath } from "@/lib/branding";
import "../globals.css";

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

/**
 * Task F1: the tenant's product name names the browser tab (pages' titles come first) and its
 * favicon replaces Zelvo's.
 */
export async function generateMetadata({ params }: LayoutProps<"/[locale]">): Promise<Metadata> {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "meta" });
  const { tenant } = await getRequestAuthConfig();
  const branding = await getBranding(tenant);
  const product = (locale === "ar" ? branding?.productName.ar ?? branding?.productName.en : branding?.productName.en ?? branding?.productName.ar) ?? null;
  const favicon = brandingImagePath(tenant, "Favicon", branding?.images.favicon);
  return {
    title: product ? { default: product, template: `%s · ${product}` } : t("title"),
    description: t("description"),
    ...(favicon ? { icons: { icon: favicon } } : {}),
  };
}

export default async function LocaleLayout({ children, params }: LayoutProps<"/[locale]">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) {
    notFound();
  }
  setRequestLocale(locale);
  const t = await getTranslations({ locale, namespace: "common" });
  const { tenant } = await getRequestAuthConfig();
  const branding = await getBranding(tenant);
  // Task F5: the published privacy notice (cached), for the footer link and the acceptance banner.
  const notice = await getPublishedPrivacyNotice(tenant);

  return (
    <html lang={locale} dir={getDirection(locale)}>
      {/* Task F1: the tenant's published colours and font; only checked values reach the style sheet. */}
      {branding?.publishedAt && <head><style data-testid="branding-style">{brandingCss(branding)}</style></head>}
      <body className="flex min-h-screen flex-col font-sans antialiased">
        <a
          href="#main"
          className="sr-only focus:not-sr-only focus:absolute focus:start-2 focus:top-2 focus:z-10 focus:rounded focus:bg-surface focus:px-3 focus:py-2"
        >
          {t("skipToContent")}
        </a>
        <NextIntlClientProvider>
          <SiteHeader />
          <PrivacyBanner notice={notice} />
          <main id="main" className="mx-auto w-full max-w-5xl flex-1 px-4 py-10 has-[[data-layout=wide]]:max-w-none has-[[data-layout=wide]]:py-4">
            {children}
          </main>
          <PortalChatLauncher />
          <PoweredByFooter privacyNotice={notice !== null} />
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
