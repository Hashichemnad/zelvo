import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { localToIso, packageProblemKey, type UpdatesState } from "@/lib/updates";
import { UpdatesPanel } from "./UpdatesPanel";

const pkg = {
  id: "p1", version: "1.4.1", type: "Security" as const, fileName: "zelvo-1.4.1.zpkg", sizeBytes: 5 * 1024 * 1024, sha256: "a".repeat(64), status: "Imported" as const,
  importedAt: "2026-11-25T08:00:00Z", releaseNotes: { en: "Security fixes", ar: "إصلاحات أمنية" }, expectedMinutes: 20, installable: true, problem: null, reason: null,
};
const base: UpdatesState = {
  installedVersion: "1.4.0", updaterOnline: true, updaterSeenAt: "2026-11-25T08:00:00Z", packages: [pkg], installs: [],
  inbox: [{ fileName: "zelvo-1.4.2.zpkg", sizeBytes: 1024, modifiedAt: "2026-11-25T07:00:00Z" }], maxPackageBytes: 8 * 1024 * 1024 * 1024,
};

function renderPanel(initial: UpdatesState, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <UpdatesPanel initial={initial} csrfToken="csrf-4" calendar="gregory" digits="latn" timeZone="Asia/Dubai" />
    </NextIntlClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("UpdatesPanel", () => {
  it("shows the installed version, the inbox and a package ready to install", () => {
    renderPanel(base);
    expect(screen.getByTestId("installed-version")).toHaveTextContent("1.4.0");
    expect(screen.getByTestId("updater-status")).toHaveTextContent("Online");
    expect(screen.getByTestId("inbox")).toHaveTextContent("zelvo-1.4.2.zpkg");
    expect(screen.getByTestId("package-1.4.1")).toHaveTextContent("Security and bug fix");
    expect(screen.getByRole("button", { name: "Install now" })).toBeInTheDocument();
  });

  it("explains why a package cannot be installed", () => {
    renderPanel({ ...base, packages: [{ ...pkg, version: "1.5.0", type: "Feature", installable: false, problem: "package-upgrade-license-required" }] });
    expect(screen.getByRole("note")).toHaveTextContent("This is a paid feature release");
    expect(screen.queryByRole("button", { name: "Install now" })).toBeNull();
  });

  it("installs now after confirmation and follows the install", async () => {
    vi.stubGlobal("confirm", vi.fn().mockReturnValue(true));
    const running: UpdatesState = {
      ...base, installs: [{ id: "i1", packageId: "p1", fromVersion: "1.4.0", toVersion: "1.4.1", state: "Running", scheduledFor: "2026-11-25T08:00:00Z",
        noticeFrom: "2026-11-25T08:00:00Z", expectedMinutes: 20, error: null, createdAt: "2026-11-25T08:00:00Z", startedAt: "2026-11-25T08:00:00Z", finishedAt: null,
        steps: [{ key: "verify", state: "Done", startedAt: null, endedAt: null, message: null }, { key: "load-images", state: "Running", startedAt: null, endedAt: null, message: null }] }],
    };
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ id: "i1" }), { status: 201 }))
      .mockResolvedValue(new Response(JSON.stringify(running), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderPanel(base);
    fireEvent.click(screen.getByRole("button", { name: "Install now" }));
    await waitFor(() => expect(screen.getByTestId("install-progress")).toHaveTextContent("Installing version 1.4.1"));
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/updates/packages/p1/install");
    expect(screen.getByTestId("install-progress")).toHaveTextContent("Loading the new version: in progress");
  });

  it("says why an upload was refused", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: "package-signature-invalid" }), { status: 422 })));
    renderPanel(base);
    fireEvent.change(screen.getByLabelText(/Package file/), { target: { files: [new File(["x"], "zelvo.zpkg")] } });
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("The package was changed after Zelvo signed it"));
  });

  it("is in Arabic", () => {
    renderPanel(base, "ar");
    expect(screen.getByRole("button", { name: "التثبيت الآن" })).toBeInTheDocument();
    expect(screen.getByTestId("package-1.4.1")).toHaveTextContent("إصلاحات أمنية");
  });

  it("maps problem codes and local times", () => {
    expect(packageProblemKey("package-not-newer")).toBe("package-not-newer");
    expect(packageProblemKey("x")).toBe("other");
    expect(localToIso("")).toBeNull();
    expect(localToIso("2026-11-26T22:00")).toMatch(/^2026-11-2\dT\d\d:00:00.000Z$/);
  });
});
