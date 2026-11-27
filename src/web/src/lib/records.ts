/**
 * Custom record types (task K2, module 20) as the API returns them: types with their states mapped
 * onto the ticket core's state categories, and records of a type. The server is Zelvo.Api/Records;
 * keep the two equal.
 */
import type { Bilingual, Person, TicketState } from "./incidents";
import type { NamedRef, PathRef } from "./directory";
import type { SlaClock } from "./sla";

export type RecordStateCategory = "New" | "InProgress" | "OnHold" | "Resolved" | "Closed";
export const recordStateCategories: readonly RecordStateCategory[] = ["New", "InProgress", "OnHold", "Resolved", "Closed"];

export interface RecordTypeState {
  key: string;
  name: Bilingual;
  category: RecordStateCategory;
  inUse: boolean;
}

export interface RecordType {
  id: string;
  key: string;
  name: Bilingual;
  description: Bilingual;
  numberPrefix: string;
  states: RecordTypeState[];
  portalEntry: boolean;
  defaultPriority: number;
  active: boolean;
  access: { read: boolean; edit: boolean; assign: boolean; raise: boolean };
  etag: string;
  createdAt: string;
  updatedAt: string;
}

export interface RecordStateRef {
  key: string;
  name: Bilingual;
  category: RecordStateCategory;
}

export interface CustomRecord {
  id: string;
  number: string;
  type: string;
  typeName: Bilingual;
  title: string;
  description: string | null;
  state: TicketState;
  stateOfType: RecordStateRef;
  priority: number;
  channel: string;
  requester: Person | null;
  loggedBy: Person | null;
  assignee: Person | null;
  group: NamedRef | null;
  category: PathRef | null;
  awaitingApproval: boolean;
  onHoldReason: string | null;
  resolutionNotes: string | null;
  nextStates: RecordStateRef[];
  sla: SlaClock[];
  etag: string;
  createdAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
}

export interface RecordPage {
  items: CustomRecord[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface RecordComment {
  id: string;
  visibility: "Public" | "Internal";
  body: string;
  language: string;
  author: Person;
  byStaff: boolean;
  createdAt: string;
}

/** A per-type permission of the role catalog, with its type's name for the label. */
export interface RecordPermission {
  permission: string;
  recordType: string;
  recordTypeName: Bilingual;
  action: "read" | "edit" | "assign";
}

/** Keys of types and states: lower-case letters, digits and hyphens. */
export const typeKeyPattern = /^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$/;
export const stateKeyPattern = /^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$/;
export const prefixPattern = /^[A-Z][A-Z0-9]{0,9}$/;

/** Most states of a type. */
export const maxStates = 20;

/** Why a list of states cannot be saved, as a translation key, or null. */
export function statesProblem(states: { key: string; name: { en: string; ar: string }; category: RecordStateCategory }[]): string | null {
  if (states.length < 3 || states.length > maxStates) return "count";
  if (states.some((s) => !stateKeyPattern.test(s.key) || !(s.name.en.trim() || s.name.ar.trim()))) return "fields";
  if (new Set(states.map((s) => s.key)).size !== states.length) return "unique";
  if (!(["New", "Resolved", "Closed"] as const).every((c) => states.some((s) => s.category === c))) return "categories";
  return null;
}

/** Whether a person holds any per-type record permission (staff of some custom type). */
export function hasRecordPermission(permissions: readonly string[], action: "read" | "edit" | "assign" = "read"): boolean {
  return permissions.some((p) => p.startsWith("records.") && p.endsWith(`.${action}`));
}
