import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { LicenseState } from "@/lib/license";
import { moduleShown, problemKey } from "@/lib/license";
import { LicensePanel } from "./LicensePanel";

const waiting: LicenseState = {
  activated: false, activationRequired: true, kind: "None", problem: "fingerprint-mismatch", reason: "other servers", licenseId: null, licensedTo: null,
  customerId: null, licensedVersion: null, upgrades: [], issuedAt: null, installedVersion: "1.4.2", releaseBuild: true,
  modules: [{ key: "m01-incidents", licensed: false }, { key: "m15-payments", licensed: false }],
  fingerprint: [{ key: "databaseId", value: "7311", matches: true }, { key: "machineId", value: "ab12cd34…", matches: false }],
  files: [],
};
const active: LicenseState = {
  ...waiting, activated: true, activationRequired: false, kind: "Production", problem: null, reason: null, licenseId: "LIC-2026-0001",
  licensedTo: { en: "Example Authority", ar: "هيئة المثال" }, customerId: "C-0042", licensedVersion: "1.4", upgrades: ["1.5"], issuedAt: "2026-11-20T08:00:00Z",
  modules: [{ key: "m01-incidents", licensed: true }, { key: "m15-payments", licensed: false }],
  fingerprint: [{ key: "databaseId", value: "7311", matches: true }],
  files: [{ fileType: "License", licenseId: "LIC-2026-0001", importedAt: "2026-11-21T08:00:00Z", fileName: "license.json" }],
};

function renderPanel(initial: LicenseState, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <LicensePanel initial={initial} csrfToken="csrf-3" calendar="gregory" digits="latn" />
    </NextIntlClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("LicensePanel", () => {
  it("says why the installation is not activated and shows which part of the fingerprint differs", () => {
    renderPanel(waiting);
    expect(screen.getByTestId("license-status")).toHaveTextContent("The license was issued for other servers.");
    const server = screen.getByRole("row", { name: /Server/ });
    expect(server).toHaveTextContent("No");
    expect(screen.getByRole("heading", { name: "Activate Zelvo" })).toBeInTheDocument();
  });

  it("imports a license file and shows the activated license", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(active), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderPanel(waiting);
    const file = new File(["{}"], "license.json", { type: "application/json" });
    fireEvent.change(screen.getByLabelText("Import the license file you received:"), { target: { files: [file] } });
    await waitFor(() => expect(screen.getByTestId("license-message")).toHaveTextContent("License file imported."));
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/license/import");
    expect((fetchMock.mock.calls[0]![1] as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf-3" });
    expect(screen.getByTestId("license-status")).toHaveTextContent("Licensed to Example Authority.");
    expect(screen.getByText("1.4, 1.5")).toBeInTheDocument();
    expect(screen.getByTestId("license-modules")).toHaveTextContent("Online payments: not licensed");
  });

  it("explains a refused file", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: "license-signature-invalid" }), { status: 422 })));
    renderPanel(waiting);
    fireEvent.change(screen.getByLabelText("Import the license file you received:"), { target: { files: [new File(["x"], "l.json")] } });
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("The file was changed after Zelvo issued it"));
  });

  it("is in Arabic", () => {
    renderPanel(active, "ar");
    expect(screen.getByTestId("license-status")).toHaveTextContent("مرخّص لـ هيئة المثال.");
    expect(screen.getByRole("button", { name: "تنزيل طلب التفعيل" })).toBeInTheDocument();
  });

  it("hides modules only when the license says so", () => {
    expect(moduleShown(undefined, "m02-service-catalog")).toBe(true);
    expect(moduleShown(["m01-incidents"], "m02-service-catalog")).toBe(false);
    expect(problemKey("license-unknown-key")).toBe("unknown-key");
    expect(problemKey("something-else")).toBe("other");
  });
});
