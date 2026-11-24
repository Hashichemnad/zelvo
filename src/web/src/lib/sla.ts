/**
 * SLA management (module 8, GOV-7; task B3) as the API returns it: business calendars with
 * working periods and holidays, customer groups, SLA policies with targets per priority and
 * escalation thresholds, and the response and resolution clocks of tickets.
 * See docs/architecture/sla.md.
 */
import type { NamedRef, PathRef } from "./directory";
import type { Bilingual } from "./incidents";

export type Weekday = "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday" | "Sunday";

/** Days in the order the UAE week is shown (Monday first). */
export const weekdays: Weekday[] = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** A working period: local times as HH:mm; an end of 24:00 is midnight. */
export interface Period {
  day: Weekday;
  start: string;
  end: string;
}

export interface Holiday {
  id: string;
  startDate: string;
  endDate: string;
  name: Bilingual;
}

export interface BusinessCalendar {
  id: string;
  name: Bilingual;
  timeZone: string;
  isDefault: boolean;
  periods: Period[];
  holidays: Holiday[];
  groupCount: number;
  policyCount: number;
  updatedAt: string;
  version: number;
}

export interface CalendarTemplate {
  key: "uae-government" | "uae-private" | "all-day";
  periods: Period[];
}

export interface CustomerGroup {
  id: string;
  name: Bilingual;
  active: boolean;
  emailDomains: string[];
  members: { userId: string; displayName: string | null }[];
  policyCount: number;
  updatedAt: string;
  version: number;
}

export type SlaMetric = "Response" | "Resolution";
export const slaMetrics: SlaMetric[] = ["Response", "Resolution"];

export interface Target {
  priority: number;
  responseMinutes: number | null;
  resolutionMinutes: number | null;
}

export interface Escalation {
  id: string;
  metric: SlaMetric | null;
  percent: number;
  notifyAssignee: boolean;
  notifyGroupLeads: boolean;
  notifyGroup: NamedRef | null;
}

export interface SlaPolicy {
  id: string;
  name: Bilingual;
  position: number;
  active: boolean;
  recordType: "Incident" | null;
  category: PathRef | null;
  customerGroup: NamedRef | null;
  calendar: NamedRef | null;
  targets: Target[];
  escalations: Escalation[];
  updatedAt: string;
  version: number;
}

export interface EscalationInput {
  id?: string;
  metric: SlaMetric | null;
  percent: number;
  notifyAssignee: boolean;
  notifyGroupLeads: boolean;
  notifyGroupId: string | null;
}

export interface PolicyInput {
  name: { en: string | null; ar: string | null };
  active: boolean;
  recordType: "Incident" | null;
  categoryId: string | null;
  customerGroupId: string | null;
  calendarId: string | null;
  targets: Target[];
  escalations: EscalationInput[];
}

/** POST /api/v1/sla/policies/test: what a ticket would get now. */
export interface SlaPreview {
  policy: NamedRef | null;
  calendar: NamedRef | null;
  customerGroups: NamedRef[];
  response: { minutes: number; dueAt: string | null } | null;
  resolution: { minutes: number; dueAt: string | null } | null;
}

export type SlaStatus = "None" | "Running" | "AtRisk" | "Paused" | "Met" | "Breached";

/** An SLA clock of a ticket (staff only). */
export interface SlaClock {
  metric: SlaMetric;
  status: SlaStatus;
  state: "Running" | "Paused" | "Stopped";
  targetMinutes: number | null;
  elapsedSeconds: number;
  percent: number;
  dueAt: string | null;
  breachedAt: string | null;
  stoppedAt: string | null;
  policy: NamedRef | null;
  calendar: NamedRef | null;
}

/** Priorities a policy has targets for (P1–P5, decision 2026-11-22). */
export const priorityLevels = [1, 2, 3, 4, 5] as const;

/** A duration in minutes split into days, hours and minutes of business time. */
export function splitMinutes(minutes: number): { hours: number; minutes: number } {
  return { hours: Math.floor(minutes / 60), minutes: minutes % 60 };
}

/** Business minutes for people: "1 h 30 min", "45 min" (digits follow the user's choice). */
export function formatDuration(minutes: number, locale: string, numberingSystem: string): string {
  const parts: string[] = [];
  const { hours, minutes: rest } = splitMinutes(Math.max(0, Math.round(minutes)));
  const format = (value: number, unit: "hour" | "minute") =>
    new Intl.NumberFormat(`${locale}-AE`, { style: "unit", unit, unitDisplay: "short", numberingSystem }).format(value);
  if (hours > 0) parts.push(format(hours, "hour"));
  if (rest > 0 || hours === 0) parts.push(format(rest, "minute"));
  return parts.join(" ");
}

/** Minutes from an hours field and a minutes field; null when both are empty or zero. */
export function toMinutes(hours: string, minutes: string): number | null {
  const h = Number.parseInt(hours || "0", 10);
  const m = Number.parseInt(minutes || "0", 10);
  if (!Number.isFinite(h) || !Number.isFinite(m) || h < 0 || m < 0) return null;
  const total = h * 60 + m;
  return total > 0 ? total : null;
}

/** Whether an HH:mm time is valid (00:00–24:00). */
export function isTime(value: string): boolean {
  const match = /^(\d{2}):(\d{2})$/.exec(value);
  if (!match) return false;
  const h = Number(match[1]);
  const m = Number(match[2]);
  return m < 60 && (h < 24 || (h === 24 && m === 0));
}

/** Periods sorted Monday first, then by start time. */
export function sortPeriods(periods: Period[]): Period[] {
  return [...periods].sort((a, b) => weekdays.indexOf(a.day) - weekdays.indexOf(b.day) || a.start.localeCompare(b.start));
}

/** Tone of an SLA status badge; the text, not the colour, carries the meaning. */
export const slaTone: Record<SlaStatus, string> = {
  None: "border-line text-ink-muted",
  Running: "border-brand text-brand",
  AtRisk: "border-warn text-warn",
  Paused: "border-line text-ink-muted",
  Met: "border-ok text-ok",
  Breached: "border-bad text-bad",
};
