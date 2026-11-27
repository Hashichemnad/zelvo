/**
 * The agent workspace (module 5, task B4) as the API returns it: comments, canned responses,
 * saved views, the customer beside the ticket and bulk actions; plus small helpers the screens and
 * their tests share.
 */

import type { PersonAsset } from "./assets";
import type { Bilingual, Person, TicketState } from "./incidents";

export type CommentVisibility = "Public" | "Internal";

/** A public reply or an internal note. */
export interface Comment {
  id: string;
  ticketId: string;
  visibility: CommentVisibility;
  body: string;
  /** Language it was written in. */
  language: "en" | "ar";
  author: Person;
  byStaff: boolean;
  mentions: Person[];
  cannedResponseId: string | null;
  createdAt: string;
  /** Task E2: written in Zelvo, by email, or in a live chat (its transcript). */
  source?: "Web" | "Email" | "Chat" | "WhatsApp" | "Sms" | "Teams";
  /** Task E2: files that came with an email. */
  attachments?: { id: string; fileName: string }[];
  /** Task E2, staff only: how the email of a staff reply from the ticket's mailbox went. */
  emailDelivery?: "Pending" | "Sent" | "Failed" | null;
  /** Task E3: a WhatsApp voice note, with what the AI heard when transcription is on. */
  voiceNote?: { attachmentId: string; status: "Pending" | "Done" | "Failed" | "Off"; transcript: string | null; language: "en" | "ar" | null } | null;
  /** Task E3, staff only: how a staff reply went out by WhatsApp, SMS or Teams. */
  messageDelivery?: "Pending" | "Sent" | "Delivered" | "Read" | "Failed" | null;
}

export type WorkspaceScope = "Personal" | "Group" | "Tenant";
export const workspaceScopes: WorkspaceScope[] = ["Personal", "Group", "Tenant"];

/** A canned response (bilingual wording with placeholders). */
export interface CannedResponse {
  id: string;
  scope: WorkspaceScope;
  group: { id: string; name: Bilingual } | null;
  title: Bilingual;
  body: Bilingual;
  active: boolean;
  canEdit: boolean;
  updatedAt: string;
}

/** A canned response filled in for one incident. */
export interface RenderedResponse {
  id: string;
  language: "en" | "ar";
  fallback: boolean;
  text: string;
}

/** The incident list's filters (the list endpoint's query parameters). */
export interface ViewFilters {
  state?: TicketState;
  open?: boolean;
  priority?: number;
  major?: boolean;
  assignee?: string;
  group?: string;
  category?: string;
  location?: string;
  requester?: string;
  sla?: "atRisk" | "breached";
  sort?: "due" | "created";
}

/** A saved view. */
export interface SavedView {
  id: string;
  scope: WorkspaceScope;
  group: { id: string; name: Bilingual } | null;
  name: Bilingual;
  filters: ViewFilters;
  canEdit: boolean;
}

/** Views every agent has, named in the translation files (workspace.builtIn.*). */
export const builtInViews: { key: string; filters: ViewFilters }[] = [
  { key: "myOpen", filters: { open: true, assignee: "me", sort: "due" } },
  { key: "groupQueue", filters: { open: true, group: "mine", assignee: "none", sort: "due" } },
  { key: "unassigned", filters: { open: true, assignee: "none", sort: "due" } },
  { key: "atRisk", filters: { open: true, sla: "atRisk", sort: "due" } },
  { key: "breached", filters: { sla: "breached", sort: "due" } },
  { key: "allOpen", filters: { open: true } },
];

/** The requester beside the ticket. */
export interface Customer {
  person: Person;
  email: string | null;
  preferredLanguage: "en" | "ar" | null;
  status: "Active" | "Disabled";
  customerGroups: { id: string; name: Bilingual }[];
  openCount: number;
  totalCount: number;
  recent: { id: string; number: string; title: string; state: TicketState; priority: number; createdAt: string; resolvedAt: string | null }[];
  /** Task G3: the assets assigned to them, for staff with assets.read (null otherwise). */
  assets?: PersonAsset[] | null;
}

export type BulkAction = "Assign" | "Assignment" | "Route" | "Transition" | "Priority" | "Category" | "Location" | "Reply";

export interface BulkItemResult {
  id: string;
  ok: boolean;
  status: number;
  code: string | null;
  title: string | null;
}

export interface BulkResult {
  succeeded: number;
  failed: number;
  results: BulkItemResult[];
}

/** Query string of the list endpoint for a set of filters and a page. */
export function filtersQuery(filters: ViewFilters, page = 1, pageSize = 25): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filters)) {
    if (value !== undefined && value !== null && value !== "") query.set(key, String(value));
  }
  query.set("page", String(page));
  query.set("pageSize", String(pageSize));
  return query.toString();
}

/** Whether two filter sets ask for the same list. */
export function sameFilters(a: ViewFilters, b: ViewFilters): boolean {
  const norm = (f: ViewFilters) => JSON.stringify(Object.entries(f).filter(([, v]) => v !== undefined && v !== "").sort(([x], [y]) => x.localeCompare(y)));
  return norm(a) === norm(b);
}

/** Filters from URL search parameters, keeping only known keys with sensible values. */
export function filtersFrom(params: Record<string, string | string[] | undefined>): ViewFilters {
  const one = (key: string) => {
    const value = params[key];
    return typeof value === "string" && value.length > 0 && value.length <= 40 ? value : undefined;
  };
  const filters: ViewFilters = {};
  const state = one("state");
  if (state && ["New", "Assigned", "InProgress", "OnHold", "Resolved", "Closed", "Reopened"].includes(state)) filters.state = state as TicketState;
  if (one("open") === "true") filters.open = true;
  if (one("major") === "true") filters.major = true;
  const priority = Number(one("priority"));
  if (Number.isInteger(priority) && priority >= 1 && priority <= 9) filters.priority = priority;
  for (const key of ["assignee", "group", "category", "location", "requester"] as const) {
    const value = one(key);
    if (value && /^[A-Za-z0-9-]+$/.test(value)) filters[key] = value;
  }
  const sla = one("sla");
  if (sla === "atRisk" || sla === "breached") filters.sla = sla;
  const sort = one("sort");
  if (sort === "due" || sort === "created") filters.sort = sort;
  return filters;
}

/** Time left until `dueAt` from `now`, in whole minutes; negative when overdue. */
export function minutesLeft(dueAt: string, now: number): number {
  return Math.floor((new Date(dueAt).getTime() - now) / 60000);
}

/** Placeholders canned responses may use (the API checks them too). */
export const responsePlaceholders = ["requester.name", "ticket.number", "ticket.title", "agent.name", "tenant.name"] as const;

/** Keyboard shortcuts of the workspace, shown in the help dialog (labels in workspace.shortcuts.*). */
export const shortcuts: { keys: string; action: string }[] = [
  { keys: "j", action: "next" },
  { keys: "k", action: "previous" },
  { keys: "x", action: "select" },
  { keys: "r", action: "reply" },
  { keys: "n", action: "note" },
  { keys: "/", action: "filter" },
  { keys: "a", action: "assign" },
  { keys: "?", action: "help" },
  { keys: "Escape", action: "close" },
];

/** Whether a key press happened while typing, when shortcuts must not fire. */
export function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable;
}
