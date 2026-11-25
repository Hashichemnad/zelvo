import { getLocale, getTranslations } from "next-intl/server";
import { getMaintenance } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import { formatDateTime, formatNumber } from "@/lib/format";
import type { Maintenance } from "@/lib/updates";

/**
 * Task F4: the notice of an update on every page, from the time the administrator chose until the
 * update ends, with when it starts and how long Zelvo is unavailable, in the person's calendar.
 */
export async function MaintenanceBanner() {
  const maintenance: Maintenance | null = await getMaintenance();
  if (!maintenance?.announced || !maintenance.scheduledFor) {
    return null;
  }
  const [t, locale, format] = await Promise.all([getTranslations("common"), getLocale(), getDisplayFormat()]);
  const when = formatDateTime(new Date(maintenance.scheduledFor), { locale, ...format });
  const minutes = formatNumber(maintenance.expectedMinutes ?? 30, locale, format.digits);
  return (
    <div role="note" data-testid="maintenance-banner" className="border-b-2 border-brand bg-surface">
      <p className="mx-auto max-w-5xl px-4 py-2 font-semibold">
        {maintenance.inProgress ? t("maintenanceNow", { version: maintenance.version ?? "" }) : t("maintenanceAhead", { version: maintenance.version ?? "", when, minutes })}
      </p>
    </div>
  );
}
