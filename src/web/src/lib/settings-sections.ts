/**
 * Sections of Settings. Each later module adds its own (forms, SLAs, workflows, branding, catalog,
 * payments, AI); a user sees the sections whose permission one of their roles holds.
 */
export interface SettingsSection {
  /** Key under `settings.sections` in the translation files. */
  key:
    | "language"
    | "signIn"
    | "users"
    | "roles"
    | "groups"
    | "categories"
    | "locations"
    | "skills"
    | "assignment"
    | "incidents"
    | "attachments" | "search" | "integrations" | "importExport" | "translations";
  /** Path below /[locale]. */
  href: string;
  /** Permission needed to open the section. */
  permission: string;
}

export const settingsSections: readonly SettingsSection[] = [
  { key: "language", href: "/settings/language", permission: "settings.manage" },
  { key: "signIn", href: "/settings/sign-in", permission: "identity.manage" },
  { key: "users", href: "/settings/users", permission: "users.read" },
  { key: "roles", href: "/settings/roles", permission: "roles.manage" },
  { key: "groups", href: "/settings/groups", permission: "directory.read" },
  { key: "categories", href: "/settings/categories", permission: "directory.read" },
  { key: "locations", href: "/settings/locations", permission: "directory.read" },
  { key: "skills", href: "/settings/skills", permission: "directory.read" },
  { key: "assignment", href: "/settings/assignment", permission: "directory.read" },
  { key: "incidents", href: "/settings/incidents", permission: "settings.manage" },
  { key: "attachments", href: "/settings/attachments", permission: "settings.manage" },
  { key: "search", href: "/settings/search", permission: "search.manage" },
  { key: "integrations", href: "/settings/integrations", permission: "integrations.manage" },
  { key: "importExport", href: "/settings/import-export", permission: "users.manage" },
  { key: "translations", href: "/settings/translations", permission: "translations.read" },
];

/** Sections the user may open, in menu order. */
export function sectionsFor(permissions: readonly string[]): SettingsSection[] {
  const held = new Set(permissions);
  return settingsSections.filter((section) => held.has(section.permission));
}
