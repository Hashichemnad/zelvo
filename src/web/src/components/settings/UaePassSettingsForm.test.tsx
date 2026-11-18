import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { UaePassSettingsForm, type UaePassSettings } from "./UaePassSettingsForm";

afterEach(() => vi.unstubAllGlobals());

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

const notSetUp: UaePassSettings = {
  enabled: false,
  environment: "Staging",
  clientId: "",
  clientSecretSet: false,
  clientSecretChangedAt: null,
  redirectUri: "https://id.example.ae/realms/zelvo-default/broker/uaepass/endpoint",
  environments: ["Staging", "Production"],
  acceptAsSecondFactor: false,
  requesterMinimumLevel: "SOP1",
  staffMinimumLevel: "SOP2",
  createRequesters: true,
  updatedAt: null,
};

function renderForm(initial: UaePassSettings, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      <UaePassSettingsForm csrfToken="csrf-7" initial={initial} display={display} />
    </NextIntlClientProvider>,
  );
}

describe("UaePassSettingsForm", () => {
  it("sends the client ID and secret with the CSRF token, then clears the secret field", async () => {
    const saved: UaePassSettings = {
      ...notSetUp,
      enabled: true,
      clientId: "entity_web_stage",
      clientSecretSet: true,
      clientSecretChangedAt: "2026-11-18T08:00:00Z",
      updatedAt: "2026-11-18T08:00:00Z",
    };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(saved), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderForm(notSetUp);

    fireEvent.click(screen.getByLabelText("Offer UAE PASS on the sign-in page"));
    fireEvent.change(screen.getByLabelText("Client ID"), { target: { value: "entity_web_stage" } });
    fireEvent.change(screen.getByLabelText("Client secret"), { target: { value: "top-secret" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The UAE PASS settings are saved."));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/identity/uae-pass");
    expect(init.method).toBe("PUT");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-7");
    expect(JSON.parse(init.body as string)).toEqual({
      enabled: true,
      environment: "Staging",
      clientId: "entity_web_stage",
      clientSecret: "top-secret",
      acceptAsSecondFactor: false,
      requesterMinimumLevel: "SOP1",
      createRequesters: true,
    });
    expect(screen.getByLabelText("Client secret")).toHaveValue("");
    expect(screen.getByText(/A client secret is saved/)).toBeInTheDocument();
  });

  it("leaves the secret out when the field is empty, so the saved one is kept", async () => {
    const current = { ...notSetUp, enabled: true, clientId: "entity", clientSecretSet: true, clientSecretChangedAt: "2026-11-18T08:00:00Z" };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...current, environment: "Production" }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderForm(current);

    fireEvent.change(screen.getByLabelText("UAE PASS environment"), { target: { value: "Production" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({
      enabled: true,
      environment: "Production",
      clientId: "entity",
      acceptAsSecondFactor: false,
      requesterMinimumLevel: "SOP1",
      createRequesters: true,
    });
  });

  it("shows the API's field errors in the page's language and links them to the fields", async () => {
    const problem = { status: 400, code: "validation-failed", errors: { clientSecret: ["Enter the client secret before turning UAE PASS on."] } };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify(problem), { status: 400 })));
    renderForm({ ...notSetUp, clientId: "entity" }, "ar");

    fireEvent.click(screen.getByRole("button", { name: "حفظ" }));

    const field = screen.getByLabelText("الرمز السري للعميل");
    await waitFor(() => expect(field).toHaveAttribute("aria-invalid", "true"));
    expect(field).toHaveAccessibleDescription(/يُرجى إدخال الرمز السري للعميل/);
  });

  it("says nothing was changed when Keycloak refuses", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: "identity-provider-unavailable" }), { status: 502 })));
    renderForm(notSetUp);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("nothing was changed"));
  });

  it("offers only the environments the installation lists and shows the redirect URI to register", () => {
    renderForm({ ...notSetUp, environments: ["Staging", "Production", "Mock"] });

    const options = Array.from((screen.getByLabelText("UAE PASS environment") as HTMLSelectElement).options).map((o) => o.value);
    expect(options).toEqual(["Staging", "Production", "Mock"]);
    expect(screen.getByTestId("uae-pass-redirect-uri")).toHaveTextContent(notSetUp.redirectUri);
  });

  it("sends the rules for staff and requesters, with the defaults the product owner approved", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(notSetUp), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderForm(notSetUp);

    expect(screen.getByLabelText("Accept UAE PASS as the second factor for staff")).not.toBeChecked();
    expect(screen.getByLabelText("Create requester accounts automatically on first UAE PASS sign-in")).toBeChecked();
    expect(screen.getByLabelText("Minimum UAE PASS account level for requesters")).toHaveValue("SOP1");
    expect(screen.getByText("Staff always need a verified UAE PASS account (SOP2 or higher). This cannot be changed.")).toBeInTheDocument();
    expect(screen.getByLabelText("Accept UAE PASS as the second factor for staff")).toHaveAccessibleDescription(/SOP2 or higher/);

    fireEvent.click(screen.getByLabelText("Accept UAE PASS as the second factor for staff"));
    fireEvent.change(screen.getByLabelText("Minimum UAE PASS account level for requesters"), { target: { value: "SOP2" } });
    fireEvent.click(screen.getByLabelText("Create requester accounts automatically on first UAE PASS sign-in"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toMatchObject({ acceptAsSecondFactor: true, requesterMinimumLevel: "SOP2", createRequesters: false });
  });
});
