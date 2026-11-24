import "server-only";
import { apiFetch } from "./api";
import { getRequestAuthConfig } from "./auth/config";
import { getSession } from "./auth/session";

/**
 * Loads a public knowledge page's data as the signed-in user, or as a visitor (task D1). Says when
 * the tenant requires signing in to read the knowledge base.
 */
export async function loadHelp<T>(path: string): Promise<{ result: T | null; signInRequired: boolean; csrfToken: string | null }> {
  const config = await getRequestAuthConfig();
  const session = await getSession(config).catch(() => null);
  try {
    const response = await apiFetch(path, session?.data.accessToken ?? "", config.tenant);
    if (response.status === 401) {
      return { result: null, signInRequired: true, csrfToken: null };
    }
    return { result: response.ok ? ((await response.json()) as T) : null, signInRequired: false, csrfToken: session?.data.csrfToken ?? null };
  } catch {
    return { result: null, signInRequired: false, csrfToken: session?.data.csrfToken ?? null };
  }
}
