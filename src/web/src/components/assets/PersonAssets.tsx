import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { formatDay, type PersonAsset } from "@/lib/assets";
import { formatNumber, type DateDisplayOptions } from "@/lib/format";
import { textIn } from "@/lib/cmdb";
import type { Locale } from "@/lib/incidents";

/**
 * The assets assigned to a person (task G3): in My assets for themselves, and in the agent's
 * customer panel beside a ticket (module 18 story: the user's laptop, its warranty and its past
 * tickets, to decide repair or replace). Staff links open the asset.
 */
export function PersonAssets({ assets, display, staff }: { assets: PersonAsset[]; display: DateDisplayOptions; staff: boolean }) {
  const t = useTranslations("assets.mine");
  const locale = display.locale as Locale;
  if (assets.length === 0) return <p className="text-sm text-ink-muted">{t(staff ? "noneForPerson" : "none")}</p>;
  return (
    <ul className="space-y-2 text-sm" data-testid="person-assets">
      {assets.map((a) => (
        <li key={a.id} className="space-y-0.5">
          <p className="font-semibold">
            {staff ? (
              <Link href={`/assets/${a.id}`} className="text-brand underline-offset-4 hover:underline"><span dir="ltr" className="font-mono">{a.tag}</span> <span dir="auto">{a.name}</span></Link>
            ) : (
              <><span dir="ltr" className="font-mono">{a.tag}</span> <span dir="auto">{a.name}</span></>
            )}
          </p>
          {a.classPath && <p className="text-ink-muted">{textIn(a.classPath, locale)}</p>}
          <p>{a.warrantyEndsOn ? t("warranty", { date: formatDay(a.warrantyEndsOn, display.locale, display.calendar, display.digits) }) : t("noWarranty")}</p>
          {a.ticketCount !== null && <p>{t("tickets", { count: a.ticketCount, shown: formatNumber(a.ticketCount, display.locale, display.digits) })}</p>}
        </li>
      ))}
    </ul>
  );
}
