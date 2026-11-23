import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { sectionsFor } from "@/lib/settings-sections";
import { SettingsNav } from "./SettingsNav";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/language",
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

describe("Settings sections", () => {
  it("are those the user's permissions allow", () => {
    expect(sectionsFor([]).map((s) => s.key)).toEqual([]);
    expect(sectionsFor(["translations.read"]).map((s) => s.key)).toEqual(["translations"]);
    expect(sectionsFor(["settings.manage", "translations.read", "users.read"]).map((s) => s.key)).toEqual(["language", "users", "incidents", "attachments", "translations"]);
    // Task B2: agents (users.read, directory.read) see the directory read-only; Roles needs roles.manage.
    expect(sectionsFor(["users.read", "directory.read"]).map((s) => s.key)).toEqual(["users", "groups", "categories", "locations", "skills", "assignment"]);
    expect(sectionsFor(["roles.manage"]).map((s) => s.key)).toEqual(["roles"]);
    expect(sectionsFor(["search.manage", "translations.read", "settings.manage"]).map((s) => s.key)).toEqual(["language", "incidents", "attachments", "search", "translations"]);
  });
});

describe("SettingsNav", () => {
  it("lists the sections in a labelled menu and marks the current one", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <SettingsNav sections={sectionsFor(["settings.manage", "translations.read"])} />
      </NextIntlClientProvider>,
    );

    const nav = screen.getByRole("navigation", { name: "Settings sections" });
    expect(nav).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Language and region" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "Missing translations" })).not.toHaveAttribute("aria-current");
  });

  it("is in Arabic on Arabic pages", () => {
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <SettingsNav sections={sectionsFor(["settings.manage"])} />
      </NextIntlClientProvider>,
    );

    expect(screen.getByRole("navigation", { name: "أقسام الإعدادات" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "اللغة والمنطقة" })).toBeInTheDocument();
  });
});
