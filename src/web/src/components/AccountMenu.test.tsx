import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { AccountMenu } from "./AccountMenu";

describe("AccountMenu", () => {
  it("offers sign-in, coming back to the account page in the same language", () => {
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <AccountMenu userName={null} csrfToken={null} />
      </NextIntlClientProvider>,
    );

    const link = screen.getByRole("link", { name: "تسجيل الدخول" });
    expect(link).toHaveAttribute("href", "/auth/login?locale=ar&returnTo=%2Far%2Faccount");
  });

  it("shows the user and signs out with a POST form carrying the CSRF token", () => {
    const { container } = render(
      <NextIntlClientProvider locale="en" messages={en}>
        <AccountMenu userName="Aisha Admin" csrfToken="token-123" />
      </NextIntlClientProvider>,
    );

    expect(screen.getByRole("link", { name: "Account of Aisha Admin" })).toHaveAttribute("href", "/en/account");
    const button = screen.getByRole("button", { name: "Sign out" });
    const form = button.closest("form");
    expect(form).toHaveAttribute("method", "post");
    expect(form).toHaveAttribute("action", "/auth/logout");
    expect(container.querySelector('input[name="csrf"]')).toHaveValue("token-123");
    expect(container.querySelector('input[name="locale"]')).toHaveValue("en");
  });
});
