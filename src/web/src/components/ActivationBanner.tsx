import { getTranslations } from "next-intl/server";
import { getSystemInfo } from "@/lib/api";

/**
 * Task F3: while the installation waits for activation (no license, or one for other servers), a
 * banner on every page says why most screens are not available and where the license is imported.
 */
export async function ActivationBanner() {
  const info = await getSystemInfo();
  if (!info?.activationRequired) {
    return null;
  }
  const t = await getTranslations("common");
  return (
    <div role="alert" data-testid="activation-banner" className="border-b-2 border-warn bg-surface">
      <p className="mx-auto max-w-5xl px-4 py-2 font-semibold text-warn">{t("activationRequired")}</p>
    </div>
  );
}
