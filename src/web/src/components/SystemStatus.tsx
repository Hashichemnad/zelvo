import { connection } from "next/server";
import { getTranslations } from "next-intl/server";
import { getHealth, getSystemInfo } from "@/lib/api";
import { StatusList } from "./StatusList";

const licenseModes = ["None", "Development", "Demo", "Production", "Test"] as const;
type LicenseMode = (typeof licenseModes)[number];

function isLicenseMode(value: string): value is LicenseMode {
  return (licenseModes as readonly string[]).includes(value);
}

/** Live status of the API and its dependencies, read on every request. */
export async function SystemStatus() {
  await connection();
  const [health, info, t] = await Promise.all([getHealth(), getSystemInfo(), getTranslations("status")]);

  return (
    <div className="grid gap-6 md:grid-cols-3">
      <section aria-labelledby="status-heading" className="md:col-span-2">
        <h2 id="status-heading" className="mb-3 text-xl font-bold">
          {t("heading")}
        </h2>
        <StatusList report={health} />
      </section>
      <section aria-labelledby="installation-heading">
        <h2 id="installation-heading" className="mb-3 text-xl font-bold">
          {t("installationHeading")}
        </h2>
        <dl className="space-y-3 rounded-lg border border-line p-4 text-sm">
          <div>
            <dt className="text-ink-muted">{t("version")}</dt>
            <dd className="font-semibold" dir="ltr">
              {info?.version ?? "—"}
            </dd>
          </div>
          <div>
            <dt className="text-ink-muted">{t("license")}</dt>
            <dd className="font-semibold" data-testid="license-mode">
              {info && isLicenseMode(info.licenseMode) ? t(`licenseModes.${info.licenseMode}`) : "—"}
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}
