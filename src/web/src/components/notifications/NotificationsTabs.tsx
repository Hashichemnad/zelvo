"use client";

import { useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";

const tabs = [
  { key: "events", href: "/settings/notifications" },
  { key: "deliveries", href: "/settings/notifications/deliveries" },
] as const;

/** The pages of Settings → Notifications, with the current one marked. */
export function NotificationsTabs() {
  const t = useTranslations("settings.notifications.tabs");
  const pathname = usePathname();

  return (
    <nav aria-label={t("label")}>
      <ul className="flex flex-wrap gap-2 border-b border-line">
        {tabs.map((tab) => {
          const current = pathname === tab.href;
          return (
            <li key={tab.key}>
              <Link
                href={tab.href}
                aria-current={current ? "page" : undefined}
                className={`-mb-px block border-b-2 px-3 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand ${
                  current ? "border-brand text-brand" : "border-transparent text-ink hover:border-line"
                }`}
              >
                {t(tab.key)}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
