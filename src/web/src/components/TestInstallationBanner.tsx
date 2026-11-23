import { getTranslations } from "next-intl/server";
import { getSystemInfo } from "@/lib/api";

/**
 * On the test installation every license includes (module 14), a banner on every page so that
 * nobody mistakes it for the live system.
 */
export async function TestInstallationBanner() {
  const info = await getSystemInfo();
  if (info?.environment !== "test") {
    return null;
  }
  const t = await getTranslations("common");
  return (
    <div role="note" data-testid="test-installation-banner" className="border-b-2 border-warn bg-surface">
      <p className="mx-auto max-w-5xl px-4 py-2 font-semibold text-warn">{t("testInstallation")}</p>
    </div>
  );
}
