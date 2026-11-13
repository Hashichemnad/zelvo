/**
 * Sections of Settings. Each later module adds its own (forms, SLAs, workflows, branding, catalog,
 * payments, AI); a user sees the sections whose permission one of their roles holds.
 */
export interface SettingsSection {
  /** Key under `settings.sections` in the translation files. */
  key: "language" | "translations";
  /** Path below /[locale]. */
  href: string;
  /** Permission needed to open the section. */
  permission: string;
}

export const settingsSections: readonly SettingsSection[] = [
  { key: "language", href: "/settings/language", permission: "settings.manage" },
  { key: "translations", href: "/settings/translations", permission: "translations.read" },
];

/** Sections the user may open, in menu order. */
export function sectionsFor(permissions: readonly string[]): SettingsSection[] {
  const held = new Set(permissions);
  return settingsSections.filter((section) => held.has(section.permission));
}
