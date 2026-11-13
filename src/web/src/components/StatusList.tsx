import { useTranslations } from "next-intl";
import type { HealthReport, HealthStatus } from "@/lib/api";

/** Display order and translation keys of the services shown on the welcome page. */
export const serviceKeys = ["api", "postgres", "redis", "rabbitmq", "opensearch", "keycloak"] as const;
type ServiceKey = (typeof serviceKeys)[number];

const statusKey: Record<HealthStatus, "healthy" | "degraded" | "unhealthy"> = {
  Healthy: "healthy",
  Degraded: "degraded",
  Unhealthy: "unhealthy",
};

const dotClass: Record<HealthStatus, string> = {
  Healthy: "bg-ok",
  Degraded: "bg-warn",
  Unhealthy: "bg-bad",
};

interface StatusListProps {
  /** Readiness report from the API, or null when the API did not answer. */
  report: HealthReport | null;
}

export function StatusList({ report }: StatusListProps) {
  const t = useTranslations("status");

  const statusOf = (key: ServiceKey): HealthStatus => {
    if (report === null) return "Unhealthy";
    if (key === "api") return "Healthy";
    return report.checks.find((check) => check.name === key)?.status ?? "Unhealthy";
  };

  const summary =
    report === null ? t("apiUnreachable") : report.status === "Healthy" ? t("allHealthy") : t("someUnhealthy");

  return (
    <div>
      <p role="status" className="mb-4 text-ink-muted">
        {summary}
      </p>
      <ul className="divide-y divide-line rounded-lg border border-line">
        {serviceKeys.map((key) => {
          const status = statusOf(key);
          return (
            <li key={key} data-service={key} data-status={status} className="flex items-center justify-between gap-4 px-4 py-3">
              <span>{t(`services.${key}`)}</span>
              <span className="flex items-center gap-2 text-sm font-semibold">
                <span aria-hidden="true" className={`inline-block size-2.5 rounded-full ${dotClass[status]}`} />
                {t(statusKey[status])}
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
