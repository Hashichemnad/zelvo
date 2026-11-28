/**
 * The low-code app builder (task K3, module 20) as the API returns it: apps grouping custom record
 * types, data lists and roles with their list views, menu entries and portal entries; data lists;
 * and imported app packages with their check. The server is Zelvo.Api/Apps; keep the two equal.
 */
import type { Bilingual } from "./incidents";

export interface Text {
  en: string | null;
  ar: string | null;
}

export interface AppListView {
  key: string;
  name: Text;
  recordType: string;
  state?: string | null;
  open?: boolean | null;
  assignee?: "me" | "none" | null;
}

export interface AppMenuEntry {
  key: string;
  label: Text;
  target: "records" | "new";
  recordType: string;
  view?: string | null;
}

export interface AppPortalEntry {
  key: string;
  label: Text;
  description: Text;
  recordType: string;
}

export interface AppDefinition {
  listViews: AppListView[];
  menuEntries: AppMenuEntry[];
  portalEntries: AppPortalEntry[];
}

export interface AppMember {
  key: string;
  name: Bilingual;
  active: boolean;
}

export interface App {
  id: string;
  key: string;
  name: Bilingual;
  description: Bilingual;
  icon: string;
  active: boolean;
  listViews: AppListView[];
  menuEntries: AppMenuEntry[];
  portalEntries: AppPortalEntry[];
  members: { recordTypes: AppMember[]; dataLists: AppMember[]; roles: AppMember[]; forms: AppMember[]; rules: AppMember[] } | null;
  importId: string | null;
  etag: string;
  createdAt: string;
  updatedAt: string;
}

export interface DataListColumn {
  key: string;
  label: Text;
}

export interface DataListItem {
  key: string;
  label: Bilingual;
  values: Record<string, string>;
  active: boolean;
}

export interface DataList {
  id: string;
  key: string;
  name: Bilingual;
  description: Bilingual;
  columns: DataListColumn[];
  items: DataListItem[];
  app: string | null;
  etag: string;
  updatedAt: string;
}

export interface PackageProblem {
  path: string;
  code: string;
  message: string;
}

export interface PackageConflict {
  id: string;
  kind: "app" | "recordType" | "dataList" | "role";
  key: string;
  existing: Bilingual;
  replace: string;
}

export interface PackagePlan {
  appKey: string | null;
  appName: Bilingual;
  icon: string | null;
  source: { profile: string | null; productVersion: string | null } | null;
  exportedAt: string | null;
  problems: PackageProblem[];
  conflicts: PackageConflict[];
  items: { kind: string; key: string; name: Bilingual; exists: boolean }[];
  permissionsCreated: string[];
  grants: { role: string; roleExists: boolean; permissions: string[] }[];
  references: { kind: string; label: string; foundId: string | null; foundName: Bilingual | null }[];
  forms: number;
  rules: number;
  listViews: number;
  menuEntries: number;
  portalEntries: number;
  valid: boolean;
}

export type AppImportStatus = "Pending" | "Approved" | "Discarded";

export interface AppImport {
  id: string;
  fileName: string | null;
  sha256: string;
  appKey: string;
  appName: Bilingual;
  status: AppImportStatus;
  importedAt: string;
  decidedAt: string | null;
  plan: PackagePlan | null;
  choices: Record<string, string>;
  result: { appId: string; added: string[]; replaced: string[]; kept: string[] } | null;
}

export interface AppImportSummary {
  id: string;
  fileName: string | null;
  appKey: string;
  appName: Bilingual;
  status: AppImportStatus;
  importedAt: string;
  decidedAt: string | null;
}

/** Keys of apps, entries and record types: lower-case letters, digits and hyphens. */
export const appKeyPattern = /^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$/;

/** Keys of data lists (also used as form option sources). */
export const dataListKeyPattern = /^[a-z][a-zA-Z0-9-]{0,39}$/;

/** Keys of data list columns. */
export const columnKeyPattern = /^[a-z][a-zA-Z0-9]{0,19}$/;

/** Largest package the API takes. */
export const maxPackageBytes = 5 * 1024 * 1024;

/** The address of a list view: the record type's list with the view's filters. */
export function viewHref(view: AppListView): string {
  const query = new URLSearchParams();
  if (view.state) query.set("state", view.state);
  if (view.open) query.set("open", "true");
  if (view.assignee) query.set("assignee", view.assignee);
  const text = query.toString();
  return `/records/${view.recordType}${text ? `?${text}` : ""}`;
}

/** Where a menu entry leads. */
export function menuHref(entry: AppMenuEntry, views: AppListView[]): string {
  if (entry.target === "new") return `/records/${entry.recordType}/new`;
  const view = entry.view ? views.find((v) => v.key === entry.view && v.recordType === entry.recordType) : undefined;
  return view ? viewHref(view) : `/records/${entry.recordType}`;
}
