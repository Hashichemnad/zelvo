/**
 * Directory and assignment (task B2, modules 1 and 10) as the API returns them: categories and
 * locations (trees of up to 3 levels), skills, groups and members, users, roles and assignment
 * rules. See docs/architecture/directory-assignment.md.
 */
import type { Bilingual, Locale } from "./incidents";
import { inLanguage } from "./incidents";

export type TreeKind = "categories" | "locations";

/** A category or location; lists are flat, ordered by path. */
export interface TreeNode {
  id: string;
  parentId: string | null;
  depth: number;
  position: number;
  name: Bilingual;
  path: Bilingual;
  active: boolean;
  /** Active, with every ancestor active: can be chosen for new tickets and rules. */
  selectable: boolean;
  childCount: number;
}

/** The deepest level of a category or location tree (decision 2026-11-23). */
export const maxTreeDepth = 3;

export interface NamedRef {
  id: string;
  name: Bilingual;
}

/** A category or location as tickets and rules show it. */
export interface PathRef extends NamedRef {
  path: Bilingual;
}

export interface Skill {
  id: string;
  name: Bilingual;
  active: boolean;
  userCount: number;
}

export interface GroupSummary {
  id: string;
  name: Bilingual;
  description: Bilingual | null;
  active: boolean;
  memberCount: number;
  leadCount: number;
}

export interface GroupMember {
  userId: string;
  displayName: string | null;
  isLead: boolean;
  availableForAssignment: boolean;
  languages: Language[];
  skills: NamedRef[];
  status: UserStatus;
  lastAssignedAt: string | null;
}

export interface Group extends GroupSummary {
  members: GroupMember[];
}

export type Language = "en" | "ar";
export const languages: Language[] = ["en", "ar"];

export type UserStatus = "Active" | "Disabled";

/** A user. Fields hidden from the caller's roles are missing. */
export interface DirectoryUser {
  id: string;
  displayName?: string | null;
  email?: string | null;
  emiratesId?: string | null;
  status?: UserStatus;
  roles?: string[];
  lastSeenAt?: string | null;
  createdAt: string;
  skills?: NamedRef[];
  languages?: Language[];
  availableForAssignment?: boolean;
  groups?: (NamedRef & { isLead: boolean })[];
}

export interface UserPage {
  items: DirectoryUser[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export type FieldAccess = "Hidden" | "Read" | "Edit";
export const fieldAccessLevels: FieldAccess[] = ["Hidden", "Read", "Edit"];

export interface Role {
  id: string;
  key: string;
  name: Bilingual;
  isSystem: boolean;
  requiresMfa: boolean;
  permissions: string[];
  fieldRules: { resource: string; field: string; access: FieldAccess }[];
}

/** GET /api/v1/permissions: what can be granted. */
export interface PermissionCatalog {
  permissions: string[];
  resources: { key: string; readPermission: string; editPermission: string; fields: { key: string; sensitive: boolean; writable: boolean }[] }[];
}

export type AssignmentMethod = "GroupQueue" | "RoundRobin";
export const assignmentMethods: AssignmentMethod[] = ["GroupQueue", "RoundRobin"];

export interface AssignmentRule {
  id: string;
  position: number;
  name: Bilingual;
  active: boolean;
  category: PathRef | null;
  location: PathRef | null;
  language: Language | null;
  group: NamedRef;
  method: AssignmentMethod;
  skill: NamedRef | null;
}

export interface RuleInput {
  name: { en: string | null; ar: string | null };
  active: boolean;
  categoryId: string | null;
  locationId: string | null;
  language: Language | null;
  groupId: string;
  method: AssignmentMethod;
  skillId: string | null;
}

export interface AssignmentDefault {
  groupId: string | null;
  method: AssignmentMethod;
}

/** POST /api/v1/assignment-rules/test: what would happen now (nothing is assigned). */
export interface RuleTestResult {
  rule: NamedRef | null;
  group: NamedRef | null;
  method: AssignmentMethod | null;
  assignee: { id: string; displayName: string | null } | null;
}

/** The area a permission key belongs to, for grouping: "incidents.read" → "incidents". */
export function permissionArea(key: string): string {
  return key.split(".")[0] ?? key;
}

/** Text in the page's language (or the other one); empty when neither is set. */
export function textOf(text: Bilingual | null | undefined, locale: Locale): string {
  return inLanguage(text, locale).value;
}

/** Bilingual input as the API expects it: blank languages are left out. */
export function toInput(en: string, ar: string): { en: string | null; ar: string | null } {
  return { en: en.trim() || null, ar: ar.trim() || null };
}

/** Tree nodes that can be chosen for a new ticket or rule: active, with every ancestor active. */
export function selectableNodes(nodes: TreeNode[]): TreeNode[] {
  if (nodes.every((n) => typeof n.selectable === "boolean")) return nodes.filter((n) => n.selectable);
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const usable = (node: TreeNode): boolean => node.active && (!node.parentId || (byId.has(node.parentId) && usable(byId.get(node.parentId)!)));
  return nodes.filter(usable);
}
