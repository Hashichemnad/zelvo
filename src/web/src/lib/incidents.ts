/**
 * Incidents (module 1) as the API returns them, and small helpers shared by the staff screens
 * and the requester pages ("My incidents"), which the self-service portal (D2) reuses.
 */

import type { SlaClock } from "./sla";

/** Bilingual text as the API returns it. */
export interface Bilingual {
  en: string | null;
  ar: string | null;
  missing?: string[];
}

export type TicketState = "New" | "Assigned" | "InProgress" | "OnHold" | "Resolved" | "Closed" | "Reopened";
export type TicketChannel = "Portal" | "Email" | "Chat" | "WhatsApp" | "Phone" | "Api" | "AiAgent";

export const ticketStates: TicketState[] = ["New", "Assigned", "InProgress", "OnHold", "Resolved", "Closed", "Reopened"];
export const staffChannels: TicketChannel[] = ["Phone", "Email", "Chat", "WhatsApp", "Portal", "Api"];

export interface Person {
  id: string;
  displayName: string | null;
}

/** A category or location of an incident, with its path ("Hardware › Laptop"). */
export interface DirectoryRef {
  id: string;
  name: Bilingual;
  path: Bilingual;
}

export interface TicketLink {
  id: string;
  number: string;
  title: string;
  state: TicketState;
}

/** An incident. Fields hidden from the caller are missing (field rules, or the requester's smaller view). */
export interface Incident {
  id: string;
  number: string;
  title?: string;
  description?: string | null;
  state: TicketState;
  channel?: TicketChannel;
  requester?: Person;
  assignee?: Person | null;
  /** Category and location (task B2); requesters see them on their own incidents. */
  category?: DirectoryRef | null;
  location?: DirectoryRef | null;
  /** Assignment group and the rule that last routed the incident (staff only). */
  group?: { id: string; name: Bilingual } | null;
  routingRule?: { id: string; name: Bilingual } | null;
  impact?: number;
  urgency?: number;
  priority?: number;
  isMajor: boolean;
  majorDeclaredAt: string | null;
  onHoldReason: string | null;
  resolutionNotes?: string | null;
  reopenCount: number;
  parent?: TicketLink | null;
  followUpOf: TicketLink | null;
  children?: TicketLink[];
  nextStates?: TicketState[];
  canReopen: boolean;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
  /** First response: work started or resolved (task B3). */
  respondedAt?: string | null;
  /** Response and resolution clocks (task B3, staff only). */
  sla?: SlaClock[];
  version: number;
}

export interface IncidentPage {
  items: Incident[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface NamedLevel {
  level: number;
  name: Bilingual;
}

export interface OnHoldReason {
  key: string;
  name: Bilingual;
  active: boolean;
  /** Whether SLA clocks pause while waiting for this reason (task B3). */
  pausesSla?: boolean;
}

/** The tenant's incident settings (GET /api/v1/settings/incidents). */
export interface IncidentSettings {
  priorityMatrix: { impacts: NamedLevel[]; urgencies: NamedLevel[]; priorities: NamedLevel[]; matrix: number[][] };
  onHoldReasons: OnHoldReason[];
  numberPrefixes: Record<string, string>;
  autoCloseDays: number;
  /** Count the auto-close days as working days of the default business calendar (task B3). */
  autoCloseBusinessDays?: boolean;
  updatedAt: string | null;
}

/** The tenant's attachment settings (GET /api/v1/settings/attachments). */
export interface AttachmentSettings {
  maxSizeMb: number;
  allowedExtensions: string[];
  availableExtensions: string[];
  scanningEnabled: boolean;
  updatedAt: string | null;
}

export type ScanStatus = "Pending" | "Clean" | "Infected" | "NotScanned";

export interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: ScanStatus;
  downloadable: boolean;
  canDelete: boolean;
  uploadedBy: Person | null;
  uploadedAt: string;
}

export interface MajorIncidentUpdate {
  id: string;
  ticketId: string;
  ticketNumber: string;
  body: Bilingual;
  audience: "Internal" | "Requesters";
  postedBy: Person;
  postedAt: string;
}

export interface HistoryEntry {
  sequence: number;
  occurredAt: string;
  category: "Create" | "Change" | "Delete" | "Admin" | "View" | "Security" | "Ai";
  action: string;
  entityType: string | null;
  actorType: string;
  actorName: string | null;
  changes: Record<string, { old: unknown; new: unknown }> | null;
  details: Record<string, unknown> | null;
}

export type Locale = "en" | "ar";

/** Text in the page's language, or the other language (marked with lang and dir) when it is missing. */
export function inLanguage(text: Bilingual | null | undefined, locale: Locale): { value: string; lang?: Locale; dir?: "ltr" | "rtl" } {
  const other: Locale = locale === "ar" ? "en" : "ar";
  const own = text?.[locale];
  if (own) {
    return { value: own };
  }
  const fallback = text?.[other];
  return fallback ? { value: fallback, lang: other, dir: other === "ar" ? "rtl" : "ltr" } : { value: "" };
}

/** Name of a level (impact, urgency or priority) from the tenant's matrix. */
export function levelName(levels: NamedLevel[], level: number | undefined, locale: Locale): string {
  const named = levels.find((l) => l.level === level);
  return named ? inLanguage(named.name, locale).value : level === undefined ? "" : String(level);
}

/** Name of an on-hold reason, or its key when the tenant removed it. */
export function reasonName(reasons: OnHoldReason[], key: string | null, locale: Locale): string {
  if (!key) {
    return "";
  }
  const reason = reasons.find((r) => r.key === key);
  return reason ? inLanguage(reason.name, locale).value : key;
}

/** Whether an incident is still being worked on. */
export function isOpen(state: TicketState): boolean {
  return state !== "Resolved" && state !== "Closed";
}

/** File size for people: "12 KB", "3.4 MB" (digits follow the user's choice). */
export function formatSize(bytes: number, locale: string, numberingSystem: string): string {
  const units = ["byte", "kilobyte", "megabyte"] as const;
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return new Intl.NumberFormat(`${locale}-AE`, {
    style: "unit",
    unit: units[unit],
    unitDisplay: "short",
    maximumFractionDigits: unit === 0 ? 0 : 1,
    numberingSystem,
  }).format(value);
}
