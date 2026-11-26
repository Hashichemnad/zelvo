import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

/** The two pages of Settings → Updates: update packages and malware definitions (task M1). */
export function UpdatesTabs({ current }: { current: "packages" | "definitions" }) {
  const t = useTranslations("malwareDefinitions.tabs");
  const tabs = [
    { key: "packages", href: "/settings/updates" },
    { key: "definitions", href: "/settings/updates/malware-definitions" },
  ] as const;
  return (
    <nav aria-label={t("label")}>
      <ul className="flex flex-wrap gap-2 border-b border-line">
        {tabs.map((tab) => (
          <li key={tab.key}>
            <Link href={tab.href} aria-current={tab.key === current ? "page" : undefined}
              className={`inline-block border-b-2 px-3 py-2 font-semibold ${tab.key === current ? "border-brand text-brand" : "border-transparent text-ink-muted hover:text-ink"}`}>
              {t(tab.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
