import { getCurrentUser } from "@/lib/api";
import { getRequestAuthConfig } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import { PortalChat } from "./PortalChat";

/** Puts the portal's chat button on every page, except for people who answer chats themselves. */
export async function PortalChatLauncher() {
  const config = await getRequestAuthConfig();
  const session = await getSession(config).catch(() => null);
  const user = session ? await getCurrentUser(session.data.accessToken, config.tenant) : null;
  if (user?.permissions.includes("chat.handle")) {
    return null;
  }
  return <PortalChat signedIn={Boolean(session && user)} csrfToken={session?.data.csrfToken ?? null} />;
}
