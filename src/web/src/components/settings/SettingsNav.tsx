"use client";

import { useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";
import type { SettingsSection } from "@/lib/settings-sections";

/** The Settings menu: the sections the user may open, with the current one marked. */
export function SettingsNav({ sections }: { sections: SettingsSection[] }) {
  const t = useTranslations("settings");
  const pathname = usePathname();

  return (
    <nav aria-label={t("navLabel")}>
      <ul className="flex flex-wrap gap-2 md:flex-col">
        {sections.map((section) => {
          const current = pathname === section.href || pathname.startsWith(`${section.href}/`);
          return (
            <li key={section.key}>
              <Link
                href={section.href}
                aria-current={current ? "page" : undefined}
                className={`block rounded-md px-3 py-2 text-sm font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${
                  current ? "bg-brand text-surface" : "text-ink hover:bg-surface-muted"
                }`}
              >
                {t(`sections.${section.key}.title`)}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
