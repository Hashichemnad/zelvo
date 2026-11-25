/**
 * Dashboards, exports, scheduled reports and the setup wizard of module 10 (task F2), as the API
 * returns them, with the small helpers the report pages share.
 */

import { displayLocale, type CalendarSystem, type DigitSystem } from "./format";

export type ReportKind = "Overview" | "Volumes" | "Backlog" | "Sla" | "Agents" | "Happiness";
export type ReportInterval = "Day" | "Week" | "Month";
export type RecordType = "Incident" | "ServiceRequest" | "Complaint" | "Inquiry" | "Suggestion";

export const recordTypes: RecordType[] = ["Incident", "ServiceRequest", "Complaint", "Inquiry", "Suggestion"];
export const reportIntervals: ReportInterval[] = ["Day", "Week", "Month"];

/** The dashboards with their own page, in menu order, and the path segment of each. */
export const dashboards = [
  { kind: "Volumes", slug: "volumes" },
  { kind: "Backlog", slug: "backlog" },
  { kind: "Sla", slug: "sla" },
  { kind: "Agents", slug: "agents" },
  { kind: "Happiness", slug: "happiness" },
] as const satisfies readonly { kind: ReportKind; slug: string }[];

export type DashboardSlug = (typeof dashboards)[number]["slug"];

/** The dashboard of a path segment, or null. */
export function dashboardOf(slug: string): (typeof dashboards)[number] | null {
  return dashboards.find((d) => d.slug === slug) ?? null;
}

/** The API's path segment of a report (`/reports/{segment}`). */
export function apiSegment(kind: ReportKind): string {
  return kind.toLowerCase();
}

export interface ReportName {
  en: string | null;
  ar: string | null;
}

/** What the caller sees in reports (GET /api/v1/reports/scope). */
export interface ReportScope {
  mode: "All" | "Groups" | "Own";
  types: RecordType[];
  groups: { id: string; name: ReportName }[];
  canManageSchedules: boolean;
  canSchedule: boolean;
}

export interface ReportTargets {
  slaTargetPercent: number;
  happinessTargetPercent: number;
}

export interface KeyCount {
  key: string;
  count: number;
}

export interface NamedCount {
  id: string | null;
  name: ReportName | null;
  count: number;
}

export interface OverviewReport {
  from: string;
  to: string;
  created: number;
  resolved: number;
  open: number;
  responsePercent: number | null;
  resolutionPercent: number | null;
  ratings: number;
  happinessPercent: number | null;
  medianResponseMinutes: number | null;
  targets: ReportTargets;
}

export interface VolumesReport {
  from: string;
  to: string;
  interval: ReportInterval;
  created: number;
  resolved: number;
  closed: number;
  series: { bucket: string; created: number; resolved: number; closed: number }[];
  byType: KeyCount[];
  byChannel: KeyCount[];
  byPriority: KeyCount[];
  byGroup: NamedCount[];
  byCategory: NamedCount[];
}

export interface BacklogReport {
  from: string;
  to: string;
  interval: ReportInterval;
  open: number;
  unassigned: number;
  byState: KeyCount[];
  byAge: KeyCount[];
  byType: KeyCount[];
  byPriority: KeyCount[];
  byGroup: NamedCount[];
  trend: { bucket: string; open: number }[];
}

export interface SlaFigures {
  met: number;
  breached: number;
  running: number;
  percent: number | null;
}

export interface SlaRow {
  key: string;
  id: string | null;
  name: ReportName | null;
  response: SlaFigures;
  resolution: SlaFigures;
}

export interface SlaReport {
  from: string;
  to: string;
  interval: ReportInterval;
  response: SlaFigures;
  resolution: SlaFigures;
  byPriority: SlaRow[];
  byType: SlaRow[];
  byGroup: SlaRow[];
  byPolicy: SlaRow[];
  trend: { bucket: string; responsePercent: number | null; resolutionPercent: number | null }[];
  targetPercent: number;
}

export interface AgentRow {
  id: string;
  name: string;
  assigned: number;
  resolved: number;
  openNow: number;
  medianResponseMinutes: number | null;
  medianResolutionHours: number | null;
  slaPercent: number | null;
  reopenPercent: number | null;
  ratings: number;
  happinessPercent: number | null;
}

export interface AgentsReport {
  from: string;
  to: string;
  agents: AgentRow[];
}

export interface HappinessGroup {
  key: string;
  id: string | null;
  name: ReportName | null;
  ratings: number;
  happinessPercent: number | null;
}

export interface HappinessReport {
  from: string;
  to: string;
  interval: ReportInterval;
  ratings: number;
  happinessPercent: number | null;
  scores: { points: number; score: number; count: number }[];
  byType: HappinessGroup[];
  byChannel: HappinessGroup[];
  byGroup: HappinessGroup[];
  byService: HappinessGroup[];
  trend: { bucket: string; ratings: number; happinessPercent: number | null }[];
  targetPercent: number;
}

export type ReportFrequency = "Daily" | "Weekly" | "Monthly";
export const reportFrequencies: ReportFrequency[] = ["Daily", "Weekly", "Monthly"];

export interface ReportPerson {
  id: string;
  name: string;
}

/** A scheduled report (GET /api/v1/reports/schedules). */
export interface ReportSchedule {
  id: string;
  name: string;
  report: ReportKind;
  frequency: ReportFrequency;
  dayOfWeek: number | null;
  dayOfMonth: number | null;
  time: string;
  types: RecordType[];
  groups: string[];
  categories: string[];
  active: boolean;
  owner: ReportPerson;
  recipients: ReportPerson[];
  nextRunAt: string | null;
  lastRunAt: string | null;
  canEdit: boolean;
  updatedAt: string;
}

export interface ReportDelivery {
  id: string;
  recipientId: string;
  recipientName: string;
  runAt: string;
  periodFrom: string;
  periodTo: string;
  status: "Pending" | "Sent" | "Failed" | "Skipped";
  attempts: number;
  error: string | null;
  sentAt: string | null;
  createdAt: string;
}

/** A schedule as the form sends it. */
export interface ReportScheduleInput {
  name: string;
  report: ReportKind;
  frequency: ReportFrequency;
  dayOfWeek: number | null;
  dayOfMonth: number | null;
  time: string;
  types: RecordType[];
  groups: string[];
  recipientIds?: string[];
  active: boolean;
}

/** The filter of a dashboard as the page reads it from its URL. */
export interface ReportFilterValues {
  from: string;
  to: string;
  types: RecordType[];
  groups: string[];
  interval: ReportInterval | "";
}

const datePattern = /^\d{4}-\d{2}-\d{2}$/;
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function many(value: string | string[] | undefined): string[] {
  const values = Array.isArray(value) ? value : value === undefined ? [] : [value];
  return values.flatMap((v) => v.split(",")).map((v) => v.trim()).filter(Boolean);
}

/** The filter in a page's search parameters; anything not valid is left out. */
export function readFilter(search: Record<string, string | string[] | undefined>): ReportFilterValues {
  const one = (key: string) => (typeof search[key] === "string" ? (search[key] as string) : "");
  const interval = one("interval");
  return {
    from: datePattern.test(one("from")) ? one("from") : "",
    to: datePattern.test(one("to")) ? one("to") : "",
    types: [...new Set(many(search.types).filter((t): t is RecordType => (recordTypes as string[]).includes(t)))],
    groups: [...new Set(many(search.groups).filter((g) => idPattern.test(g)))].slice(0, 50),
    interval: (reportIntervals as string[]).includes(interval) ? (interval as ReportInterval) : "",
  };
}

/** The API's query string of a filter (comma-separated lists). */
export function filterQuery(filter: ReportFilterValues): string {
  const query = new URLSearchParams();
  if (filter.from) query.set("from", filter.from);
  if (filter.to) query.set("to", filter.to);
  if (filter.types.length > 0) query.set("types", filter.types.join(","));
  if (filter.groups.length > 0) query.set("groups", filter.groups.join(","));
  if (filter.interval) query.set("interval", filter.interval);
  return query.toString();
}

/** Text of a bilingual report name in the page's language, else the other one. */
export function nameIn(name: ReportName | null | undefined, locale: string): string {
  if (!name) return "";
  return (locale === "ar" ? name.ar ?? name.en : name.en ?? name.ar) ?? "";
}

export interface ReportDisplay {
  locale: string;
  calendar: CalendarSystem;
  digits: DigitSystem;
}

/** A day of the API (yyyy-MM-dd) in the reader's calendar and digits; short for trends. */
export function formatDay(day: string, display: ReportDisplay, style: "long" | "short" | "month" = "long"): string {
  const date = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(date.getTime())) return day;
  const options: Intl.DateTimeFormatOptions =
    style === "month" ? { month: "short", year: "numeric" } : style === "short" ? { day: "numeric", month: "short" } : { day: "numeric", month: "long", year: "numeric" };
  return new Intl.DateTimeFormat(displayLocale(display.locale), { ...options, calendar: display.calendar, numberingSystem: display.digits, timeZone: "UTC" }).format(date);
}

/** The short label of a trend bucket for its interval. */
export function bucketLabel(day: string, interval: ReportInterval, display: ReportDisplay): string {
  return formatDay(day, display, interval === "Month" ? "month" : "short");
}

/** A number in the reader's digits, with at most one decimal. */
export function formatFigure(value: number, display: ReportDisplay): string {
  return new Intl.NumberFormat(displayLocale(display.locale), { numberingSystem: display.digits, maximumFractionDigits: 1 }).format(value);
}

/** Who may see reports at all: staff with a read permission of a record type. */
export function canSeeReports(permissions: readonly string[]): boolean {
  return ["incidents.read", "requests.read", "voice.read"].some((p) => permissions.includes(p));
}

/** Who sees the full set of dashboards (the groups they lead, or everything). */
export function seesDashboards(permissions: readonly string[]): boolean {
  return canSeeReports(permissions) && (permissions.includes("reports.read") || permissions.includes("reports.all"));
}

// --- Setup wizard ---

export type SetupStepKey = "organisation" | "language" | "calendar" | "branding" | "signIn" | "departments" | "users" | "channels" | "ai";
export type SetupStepStatus = "Pending" | "Done" | "Skipped";

export interface SetupStep {
  key: SetupStepKey;
  status: SetupStepStatus;
  configured: boolean | null;
}

/** The setup wizard (GET /api/v1/setup). */
export interface SetupState {
  steps: SetupStep[];
  completedAt: string | null;
  finished: number;
  total: number;
  applied: { template: string; departmentId: string; appliedAt: string }[];
}

/** Where each step's own settings are (paths below /[locale]); the organisation's name is changed in the wizard itself. */
export const setupStepLinks: Record<SetupStepKey, string | null> = {
  organisation: null,
  language: "/settings/language",
  calendar: "/settings/business-calendars",
  branding: "/settings/branding",
  signIn: "/settings/sign-in",
  departments: "/settings/departments",
  users: "/settings/users",
  channels: "/settings/mailboxes",
  ai: "/settings/ai",
};

export interface TemplateTextValue {
  en: string | null;
  ar: string | null;
}

/** A department template with everything it creates (GET /api/v1/setup/templates). */
export interface DepartmentTemplate {
  key: string;
  name: TemplateTextValue;
  description: TemplateTextValue;
  codePrefix: string;
  groups: { key: string; name: TemplateTextValue }[];
  categories: { name: TemplateTextValue; group: string; children: TemplateTextValue[] }[];
  policies: { recordType: RecordType; targets: { priority: number; responseMinutes: number; resolutionMinutes: number }[] }[];
  services: { template: string; code: string; name: TemplateTextValue }[];
  responses: { title: TemplateTextValue; body: TemplateTextValue }[];
}

/** What applying a template created. */
export interface AppliedTemplate {
  template: string;
  departmentId: string;
  groupIds: string[];
  rootCategoryId: string;
  categories: number;
  policies: number;
  catalogId: string | null;
  serviceIds: string[];
  responses: number;
}
