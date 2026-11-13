import { render, screen, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { StatusList } from "./StatusList";

vi.mock("server-only", () => ({}));

function withIntl(locale: "en" | "ar", node: ReactNode) {
  return (
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      {node}
    </NextIntlClientProvider>
  );
}

const healthy = {
  status: "Healthy" as const,
  checks: ["keycloak", "opensearch", "postgres", "rabbitmq", "redis"].map((name) => ({
    name,
    status: "Healthy" as const,
  })),
};

describe("StatusList", () => {
  it("lists every service as running when all checks pass", () => {
    render(withIntl("en", <StatusList report={healthy} />));

    expect(screen.getByRole("status")).toHaveTextContent("All services are running.");
    const items = screen.getAllByRole("listitem");
    expect(items).toHaveLength(6);
    for (const item of items) {
      expect(within(item).getByText("Running")).toBeInTheDocument();
    }
  });

  it("marks failing dependencies in Arabic", () => {
    const report = {
      status: "Unhealthy" as const,
      checks: healthy.checks.map((c) => (c.name === "redis" ? { ...c, status: "Unhealthy" as const } : c)),
    };
    const { container } = render(withIntl("ar", <StatusList report={report} />));

    expect(screen.getByRole("status")).toHaveTextContent("بعض الخدمات لا تستجيب بعد");
    const redis = container.querySelector('[data-service="redis"]');
    expect(redis).toHaveAttribute("data-status", "Unhealthy");
    expect(redis).toHaveTextContent("لا تستجيب");
  });

  it("reports an unreachable API", () => {
    const { container } = render(withIntl("en", <StatusList report={null} />));

    expect(screen.getByRole("status")).toHaveTextContent("The API is not reachable yet.");
    expect(container.querySelectorAll('[data-status="Unhealthy"]')).toHaveLength(6);
  });
});
