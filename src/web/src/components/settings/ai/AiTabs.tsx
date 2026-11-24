"use client";

import { useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";

const tabs = [
  { key: "providers", href: "/settings/ai" },
  { key: "features", href: "/settings/ai/features" },
  { key: "usage", href: "/settings/ai/usage" },
  { key: "prices", href: "/settings/ai/prices" },
  { key: "log", href: "/settings/ai/log" },
  { key: "mcp", href: "/settings/ai/mcp" },
  { key: "mcpServers", href: "/settings/ai/mcp-servers" },
] as const;

/** The pages of Settings → AI, with the current one marked. */
export function AiTabs() {
  const t = useTranslations("settings.ai.tabs");
  const pathname = usePathname();
  const current = (href: string) => (href === "/settings/ai" ? pathname === href : pathname === href || pathname.startsWith(`${href}/`));

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
