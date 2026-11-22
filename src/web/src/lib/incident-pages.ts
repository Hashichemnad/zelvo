import "server-only";
import { getJsonAs } from "./api";
import { getDisplayFormat } from "./display";
import type { DateDisplayOptions } from "./format";
import type { Attachment, AttachmentSettings, HistoryEntry, Incident, IncidentSettings, MajorIncidentUpdate, Person } from "./incidents";

/** Everything an incident page shows, loaded in parallel through the public API. */
export async function loadIncident(id: string, accessToken: string, tenant: string, locale: string, { staff }: { staff: boolean }) {
  const get = <T,>(path: string) => getJsonAs<T>(path, accessToken, tenant);
  const [incident, settings, attachments, attachmentSettings, updates, history, format] = await Promise.all([
    get<Incident>(`/api/v1/incidents/${encodeURIComponent(id)}`),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    get<Attachment[]>(`/api/v1/incidents/${encodeURIComponent(id)}/attachments`),
    get<AttachmentSettings>("/api/v1/settings/attachments"),
    get<MajorIncidentUpdate[]>(`/api/v1/incidents/${encodeURIComponent(id)}/updates`),
    staff ? get<HistoryEntry[]>(`/api/v1/incidents/${encodeURIComponent(id)}/history`) : Promise.resolve(null),
    getDisplayFormat(),
  ]);
  const display: DateDisplayOptions = { locale, ...format };
  return { incident, settings, attachments: attachments ?? [], attachmentSettings, updates: updates ?? [], history: history ?? [], display };
}

/** Agents incidents can be assigned to (needs incidents.assign). */
export function loadAssignees(accessToken: string, tenant: string): Promise<Person[] | null> {
  return getJsonAs<Person[]>("/api/v1/incidents/assignees", accessToken, tenant);
}

/** The first users of the tenant, by name, to log an incident on their behalf (B2 brings a directory search). */
export async function loadRequesters(accessToken: string, tenant: string): Promise<Person[]> {
  const page = await getJsonAs<{ items: { id: string; displayName: string | null }[] }>("/api/v1/users?pageSize=100", accessToken, tenant);
  return page?.items.map((u) => ({ id: u.id, displayName: u.displayName })) ?? [];
}

/** UUIDs only; anything else is not an incident id. */
export function isId(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
