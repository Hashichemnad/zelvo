import "server-only";
import { getJsonAs } from "./api";
import type { GroupSummary, TreeNode } from "./directory";

/**
 * Categories and locations everyone may choose (active ones), and groups for holders of
 * directory.read (task B2), loaded in parallel through the public API.
 */
export async function loadDirectoryChoices(accessToken: string, tenant: string, permissions: readonly string[]) {
  const get = <T,>(path: string) => getJsonAs<T>(path, accessToken, tenant);
  const [categories, locations, groups] = await Promise.all([
    get<TreeNode[]>("/api/v1/categories"),
    get<TreeNode[]>("/api/v1/locations"),
    permissions.includes("directory.read") ? get<GroupSummary[]>("/api/v1/groups") : Promise.resolve(null),
  ]);
  return { categories: categories ?? [], locations: locations ?? [], groups };
}
