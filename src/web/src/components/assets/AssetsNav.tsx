import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

/** Links between the asset pages (task G3); the current one is marked for screen readers. */
export function AssetsNav({ current }: { current: "assets" | "contracts" | "licenses" | "scan" }) {
  const t = useTranslations("assets.nav");
  const links = [
    ["assets", "/assets"],
    ["contracts", "/assets/contracts"],
    ["licenses", "/assets/licenses"],
    ["scan", "/assets/scan"],
  ] as const;
  return (
    <nav aria-label={t("label")}>
      <ul className="flex flex-wrap gap-3">
        {links.map(([key, href]) => (
          <li key={key}>
            <Link href={href} aria-current={current === key ? "page" : undefined}
              className={`rounded-md border px-3 py-1.5 text-sm font-semibold ${current === key ? "border-brand bg-brand text-surface" : "border-line text-brand"}`}>
              {t(key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
