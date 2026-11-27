import { render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import { AccountSetupForm, tokenFromHash } from "./AccountSetupForm";

describe("account setup (task M2)", () => {
  it("reads the token only from the fragment", () => {
    const token = crypto.randomUUID().replaceAll("-", "");
    expect(tokenFromHash(`#${token}`)).toBe(token);
    expect(tokenFromHash("#short")).toBeNull();
    expect(tokenFromHash("#<script>")).toBeNull();
  });

  it("checks the link, then asks for the password in Arabic, and takes the token out of the address", async () => {
    const token = crypto.randomUUID().replaceAll("-", "");
    window.history.replaceState(null, "", `/ar/account-setup#${token}`);
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ email: "admin@entity.gov.ae" }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <AccountSetupForm />
      </NextIntlClientProvider>,
    );
    expect(await screen.findByLabelText("كلمة المرور الجديدة")).toBeInTheDocument();
    expect(window.location.hash).toBe("");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/identity/account-setup/check", expect.objectContaining({ method: "POST" })));
    expect(JSON.parse(fetchMock.mock.calls[0]![1].body as string)).toEqual({ token });
    vi.unstubAllGlobals();
  });
});
