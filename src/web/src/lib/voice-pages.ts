import "server-only";
import { getCurrentUser, getJsonAs, type CurrentUser } from "./api";
import { getRequestAuthConfig } from "./auth/config";
import { getSession, type Session } from "./auth/session";
import { getDisplayFormat } from "./display";
import type { IncidentSettings } from "./incidents";
import type { VoiceComment, VoiceRecord, VoiceSettings } from "./voice";

/** The session when there is one: the portal's customer voice pages are open to visitors too (task C6). */
export async function optionalSession(): Promise<{ session: Session | null; tenant: string; user: CurrentUser | null }> {
  const config = await getRequestAuthConfig();
  const session = await getSession(config).catch(() => null);
  const user = session ? await getCurrentUser(session.data.accessToken, config.tenant) : null;
  return { session, tenant: config.tenant, user };
}

/** The customer voice settings (also for visitors). */
export async function loadVoiceSettings(accessToken: string, tenant: string): Promise<VoiceSettings | null> {
  return getJsonAs<VoiceSettings>("/api/v1/settings/customer-voice", accessToken, tenant);
}

/** Everything a record's page shows, loaded in parallel through the public API. */
export async function loadVoiceRecord(id: string, accessToken: string, tenant: string, locale: string) {
  const get = <T,>(path: string) => getJsonAs<T>(path, accessToken, tenant);
  const [record, comments, settings, incidentSettings, form, format] = await Promise.all([
    get<VoiceRecord>(`/api/v1/customer-voice/${encodeURIComponent(id)}`),
    get<VoiceComment[]>(`/api/v1/customer-voice/${encodeURIComponent(id)}/comments`),
    get<VoiceSettings>("/api/v1/settings/customer-voice"),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    get<import("./forms/types").TicketFormView>(`/api/v1/customer-voice/${encodeURIComponent(id)}/form`),
    getDisplayFormat(),
  ]);
  return { record, comments: comments ?? [], settings, incidentSettings, form: form?.form ? form : null, display: { locale, ...format } };
}
