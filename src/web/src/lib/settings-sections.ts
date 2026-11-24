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
    | "departments"
    | "assignment"
    | "slaPolicies"
    | "businessCalendars"
    | "customerGroups"
    | "incidents"
    | "attachments"
    | "forms"
    | "fieldSets"
    | "catalog"
    | "customerVoice"
    | "knowledge"
    | "notifications"
    | "cannedResponses"
    | "mailboxes"
    | "liveChat"
    | "search"
    | "integrations"
    | "ai"
    | "importExport"
    | "translations";
  /** Path below /[locale]. */
  href: string;
  /** Permission needed to open the section. */
  permission: string;
  /** Other permissions that also open it. */
  orPermissions?: readonly string[];
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
  { key: "departments", href: "/settings/departments", permission: "directory.read" },
  { key: "assignment", href: "/settings/assignment", permission: "directory.read" },
  { key: "slaPolicies", href: "/settings/sla-policies", permission: "directory.read" },
  { key: "businessCalendars", href: "/settings/business-calendars", permission: "directory.read" },
  { key: "customerGroups", href: "/settings/customer-groups", permission: "directory.read" },
  { key: "incidents", href: "/settings/incidents", permission: "settings.manage" },
  { key: "attachments", href: "/settings/attachments", permission: "settings.manage" },
  { key: "forms", href: "/settings/forms", permission: "forms.manage" },
  { key: "fieldSets", href: "/settings/field-sets", permission: "forms.manage" },
  // Task C2: managers build the catalog; publishers review and approve services.
  { key: "catalog", href: "/settings/catalog", permission: "catalog.manage", orPermissions: ["catalog.publish"] },
  { key: "customerVoice", href: "/settings/customer-voice", permission: "voice.manage" },
  { key: "knowledge", href: "/settings/knowledge", permission: "settings.manage" },
  { key: "notifications", href: "/settings/notifications", permission: "notifications.manage" },
  // Task B4: everyone who works on incidents keeps their own; leads and administrators share them.
  { key: "cannedResponses", href: "/settings/canned-responses", permission: "incidents.edit" },
  { key: "mailboxes", href: "/settings/mailboxes", permission: "channels.manage" },
  { key: "liveChat", href: "/settings/chat", permission: "channels.manage" },
  { key: "search", href: "/settings/search", permission: "search.manage" },
  { key: "integrations", href: "/settings/integrations", permission: "integrations.manage" },
  { key: "ai", href: "/settings/ai", permission: "ai.manage" },
  { key: "importExport", href: "/settings/import-export", permission: "users.manage" },
  { key: "translations", href: "/settings/translations", permission: "translations.read" },
];

/** Sections the user may open, in menu order. */
export function sectionsFor(permissions: readonly string[]): SettingsSection[] {
  const held = new Set(permissions);
  return settingsSections.filter((section) => held.has(section.permission) || (section.orPermissions ?? []).some((p) => held.has(p)));
}
