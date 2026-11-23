"use client";

import { useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";

const tabs = [
  { key: "clients", href: "/settings/integrations" },
  { key: "webhooks", href: "/settings/integrations/webhooks" },
  { key: "log", href: "/settings/integrations/log" },
] as const;

/** The pages of Settings → Integrations, with the current one marked. */
export function IntegrationsTabs() {
  const t = useTranslations("settings.integrations.tabs");
  const pathname = usePathname();
  const current = (href: string) =>
    href === "/settings/integrations" ? pathname === href : pathname === href || pathname.startsWith(`${href}/`);

  return (
    <nav aria-label={t("label")}>
      <ul className="flex flex-wrap gap-2 border-b border-line">
        {tabs.map((tab) => (
          <li key={tab.key}>
            <Link
              href={tab.href}
              aria-current={current(tab.href) ? "page" : undefined}
              className={`-mb-px block border-b-2 px-3 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${
                current(tab.href) ? "border-brand text-brand" : "border-transparent text-ink hover:border-line"
              }`}
            >
              {t(tab.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
