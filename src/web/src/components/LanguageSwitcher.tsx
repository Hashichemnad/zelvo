"use client";

import { useLocale, useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";
import { otherLocale, type Locale } from "@/i18n/routing";

/** Visible on every page; switching keeps the user on the same screen (LANG-3). */
export function LanguageSwitcher() {
  const t = useTranslations("language");
  const locale = useLocale() as Locale;
  const pathname = usePathname();
  const target = otherLocale(locale);

  return (
    <Link
      href={pathname}
      locale={target}
      hrefLang={target}
      className="rounded-md border border-line px-3 py-1.5 text-sm font-semibold text-ink hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
    >
      {/* The accessible name contains the visible text (WCAG 2.5.3), e.g. "Change language to العربية". */}
      <span className="sr-only">{t("switchLabel")}</span>{" "}
      <span lang={target}>{t("switchTo")}</span>
    </Link>
  );
}
