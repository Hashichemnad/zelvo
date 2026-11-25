import "server-only";
import { apiFetch, getJsonAs } from "./api";
import type { GroupSummary } from "./directory";
import type { ReportName, ReportScope } from "./reports";

/** The caller's reporting scope; `forbidden` when they may see no reports, null when the API cannot be reached. */
export async function loadReportScope(accessToken: string, tenant: string): Promise<ReportScope | "forbidden" | null> {
  try {
    const response = await apiFetch("/api/v1/reports/scope", accessToken, tenant);
    if (response.status === 403) return "forbidden";
    return response.ok ? ((await response.json()) as ReportScope) : null;
  } catch {
    return null;
  }
}

/** Groups the filter offers: the ones the person leads, or every active group for the whole organisation. */
export async function loadFilterGroups(scope: ReportScope, permissions: readonly string[], accessToken: string, tenant: string): Promise<{ id: string; name: ReportName }[]> {
  if (scope.mode === "Groups") return scope.groups;
  if (scope.mode !== "All" || !permissions.includes("directory.read")) return [];
  const groups = await getJsonAs<GroupSummary[]>("/api/v1/groups", accessToken, tenant);
  return (groups ?? []).filter((g) => g.active).map((g) => ({ id: g.id, name: { en: g.name.en, ar: g.name.ar } }));
}
