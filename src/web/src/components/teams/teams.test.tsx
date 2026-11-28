import { render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { TeamsTab } from "./TeamsTab";

vi.mock("@microsoft/teams-js", () => ({
  app: { initialize: vi.fn().mockRejectedValue(new Error("not in Teams")) },
  authentication: { authenticate: vi.fn() },
}));

describe("TeamsTab (task J4)", () => {
  it("explains itself outside Teams, in English and Arabic", async () => {
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <TeamsTab target="/ar/my-tickets" />
      </NextIntlClientProvider>,
    );
    await waitFor(() => expect(screen.getByRole("note")).toHaveTextContent("هذه الصفحة هي تبويب زيلفو في مايكروسوفت تيمز"));
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <TeamsTab target="/en/my-tickets" />
      </NextIntlClientProvider>,
    );
    expect(screen.getAllByRole("heading", { level: 1 }).map((h) => h.textContent)).toEqual(["زيلفو في تيمز", "Zelvo in Teams"]);
  });
});
