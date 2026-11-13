import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes } from "react";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { LanguageSwitcher } from "./LanguageSwitcher";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/tickets/42",
  Link: ({ href, locale, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement> & { locale: string }) => (
    <a href={`/${locale}${href}`} {...rest} />
  ),
}));

describe("LanguageSwitcher", () => {
  it("links from English to the same page in Arabic", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <LanguageSwitcher />
      </NextIntlClientProvider>,
    );

    const link = screen.getByRole("link", { name: "Change language to العربية" });
    expect(link).toHaveAttribute("href", "/ar/tickets/42");
    expect(link).toHaveAttribute("hreflang", "ar");
    expect(screen.getByText("العربية")).toHaveAttribute("lang", "ar");
  });

  it("links from Arabic to the same page in English", () => {
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <LanguageSwitcher />
      </NextIntlClientProvider>,
    );

    const link = screen.getByRole("link", { name: "تغيير اللغة إلى English" });
    expect(link).toHaveAttribute("href", "/en/tickets/42");
    expect(screen.getByText("English")).toHaveAttribute("lang", "en");
  });
});
