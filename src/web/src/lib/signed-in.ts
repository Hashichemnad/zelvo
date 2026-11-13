import "server-only";
import { redirect } from "next/navigation";
import { getCurrentUser, type CurrentUser } from "./api";
import { getAuthConfig } from "./auth/config";
import { getSession, type Session } from "./auth/session";

export interface SignedIn {
  session: Session;
  tenant: string;
  /** Null when the API could not be reached. */
  user: CurrentUser | null;
}

/** The signed-in user of a page; sends visitors to sign in and back to `returnTo`. */
export async function requireSignedIn(locale: string, returnTo: string): Promise<SignedIn> {
  const config = getAuthConfig();
  const session = await getSession(config);
  if (!session) {
    redirect(`/auth/login?${new URLSearchParams({ locale, returnTo })}`);
  }
  return { session, tenant: config.tenant, user: await getCurrentUser(session.data.accessToken, config.tenant) };
}
