import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { addPasskeyHref, type MyPasskeys } from "@/lib/passkeys";
import { PasskeySettingsForm } from "./PasskeySettingsForm";
import { PasskeysPanel } from "./PasskeysPanel";

function wrap(ui: React.ReactElement, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      {ui}
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const mine: MyPasskeys = {
  mode: "Allowed",
  secondFactor: true,
  passkeys: [{ id: "k1", label: "My phone", createdAt: "2026-11-20T08:00:00Z" }],
};

afterEach(() => vi.unstubAllGlobals());

describe("PasskeysPanel (task J3)", () => {
  it("lists passkeys, says a passkey is staff's second factor, removes one and adds one through Keycloak", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(new Response(null, { status: 204 })).mockResolvedValueOnce(json({ ...mine, passkeys: [] }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<PasskeysPanel initial={mine} csrfToken="csrf-1" outcome="added" />);

    expect(screen.getByText("Your passkey is added. Next time, sign in with it.")).toBeInTheDocument();
    expect(screen.getByText(/also counts as your second factor/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Add a passkey" })).toHaveAttribute(
      "href",
      "/auth/login?action=passkey&locale=en&returnTo=%2Fen%2Faccount%2Fsecurity&mfa=1",
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove My phone" }));
    await waitFor(() => expect(screen.getByText("You have no passkey yet.")).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/me/passkeys/k1");
    expect((fetchMock.mock.calls[0]![1] as RequestInit).method).toBe("DELETE");
  });

  it("is in Arabic, and says so when the organisation turned passkeys off", () => {
    wrap(<PasskeysPanel initial={{ ...mine, mode: "Off" }} csrfToken="c" outcome={null} />, "ar");
    expect(screen.getByTestId("passkeys-off")).toHaveTextContent("لا تتيح جهتكم مفاتيح المرور.");
  });

  it("requesters add a passkey without a second-factor step", () => {
    expect(addPasskeyHref("ar", "/ar/account/security", false)).toBe("/auth/login?action=passkey&locale=ar&returnTo=%2Far%2Faccount%2Fsecurity");
  });
});

describe("PasskeySettingsForm (task J3)", () => {
  it("saves the organisation's choice", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ passkeys: "RequiredForStaff" }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<PasskeySettingsForm initial="Allowed" csrfToken="csrf-2" />);

    expect(screen.getByRole("radio", { name: /Allowed/ })).toBeChecked();
    fireEvent.click(screen.getByRole("radio", { name: /Required for staff/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.getByText("The passkey setting is saved and applied to sign-in.")).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/identity/settings/passkeys");
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ mode: "RequiredForStaff" });
  });
});
