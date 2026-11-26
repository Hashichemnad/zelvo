import { getTranslations } from "next-intl/server";
import { formatDateTime, type CalendarSystem, type DigitSystem } from "@/lib/format";
import { releaseStatusKey, type ReleaseComponentsState } from "@/lib/license";

/**
 * Settings → License: the release integrity of each component (module 12 tamper detection, task
 * F6). Components check themselves against the signed release manifest when they start; the
 * updater checks the running images. A failed check stops a release component.
 */
export async function ReleaseComponents({
  state, locale, calendar, digits, timeZone,
}: { state: ReleaseComponentsState; locale: string; calendar: CalendarSystem; digits: DigitSystem; timeZone: string }) {
  const t = await getTranslations({ locale, namespace: "license.release" });
  const ok = (status: string) => status === "Verified" || status === "ImagesVerified" || (!state.releaseBuild && status === "NoManifest");
  return (
    <section aria-labelledby="release-heading" className="space-y-3 rounded border border-gray-300 p-4 dark:border-gray-600">
      <h3 id="release-heading" className="text-xl font-semibold">{t("title")}</h3>
      <p className="max-w-2xl">{t("intro")}</p>
      <p data-testid="release-manifest">
        {state.release ? t("manifest", { version: state.release }) : t(`manifestStatus.${releaseStatusKey(state.manifestStatus)}`)}
      </p>
      {state.components.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <table className="w-full max-w-3xl text-start">
          <thead>
            <tr>
              <th scope="col" className="pe-4 text-start">{t("component")}</th>
              <th scope="col" className="pe-4 text-start">{t("status")}</th>
              <th scope="col" className="pe-4 text-start">{t("version")}</th>
              <th scope="col" className="text-start">{t("checkedAt")}</th>
            </tr>
          </thead>
          <tbody>
            {state.components.map((c) => (
              <tr key={c.component} data-testid={`release-component-${c.component}`}>
                <td className="pe-4"><bdi>{c.component}</bdi></td>
                <td className="pe-4">
                  <span className={ok(c.status) ? "" : "font-semibold text-red-700 dark:text-red-300"}>
                    {t(`statuses.${releaseStatusKey(c.status)}`)}
                  </span>
                </td>
                <td className="pe-4"><bdi>{c.release ?? c.buildVersion}</bdi></td>
                <td>{formatDateTime(new Date(c.checkedAt), { locale, calendar, digits, timeZone })}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
