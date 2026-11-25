/** Connectors and monitoring sources (module 14, task E7): the shapes the API returns. */

export type ConnectorKind = "EntraId" | "Ldap" | "Dynamics365" | "Salesforce" | "HubSpot" | "Zoho" | "Rest";
export const connectorKinds: ConnectorKind[] = ["EntraId", "Ldap", "Dynamics365", "Salesforce", "HubSpot", "Zoho", "Rest"];
export const directoryKinds: ConnectorKind[] = ["EntraId", "Ldap"];
export const crmKinds: ConnectorKind[] = ["Dynamics365", "Salesforce", "HubSpot", "Zoho"];

export interface Bilingual {
  en: string | null;
  ar: string | null;
}

export interface GroupMapping {
  externalId: string;
  groupId: string;
}

export interface EntraSettings {
  directoryId: string;
  clientId: string;
  usersGroupId: string | null;
  groupMappings: GroupMapping[];
  signIn: boolean;
}

export type LdapSecurity = "Ldaps" | "StartTls" | "None";

export interface LdapSettings {
  host: string;
  port: number;
  security: LdapSecurity;
  bindDn: string;
  baseDn: string;
  userFilter: string;
  activeDirectory: boolean;
  groupMappings: GroupMapping[];
  signIn: boolean;
}

export interface CrmSettings {
  url: string | null;
  apiUrl: string | null;
  directoryId: string | null;
  clientId: string | null;
  statusMap: Record<string, string>;
  customerGroups: boolean;
}

export type RestAuth = "None" | "ApiKey" | "Basic" | "Bearer" | "OAuth2";

export interface RestResultField {
  key: string;
  name: Bilingual;
  path: string;
}

export interface RestOperation {
  key: string;
  name: Bilingual;
  method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  path: string;
  body: string | null;
  results: RestResultField[];
  externalRefPath: string | null;
}

export interface RestSettings {
  baseUrl: string;
  auth: RestAuth;
  authHeader: string | null;
  username: string | null;
  tokenUrl: string | null;
  clientId: string | null;
  scope: string | null;
  headers: Record<string, string>;
  operations: RestOperation[];
}

export interface ConnectorSettings {
  entra?: EntraSettings | null;
  ldap?: LdapSettings | null;
  crm?: CrmSettings | null;
  rest?: RestSettings | null;
}

export interface SyncStats {
  read: number;
  created: number;
  linked: number;
  updated: number;
  disabled: number;
  enabled: number;
  membershipsAdded: number;
  membershipsRemoved: number;
  skipped: number;
}

export interface Connector {
  id: string;
  name: string;
  kind: ConnectorKind;
  settings: ConnectorSettings;
  credentialSet: boolean;
  credentialSetAt: string | null;
  active: boolean;
  syncMinutes: number;
  lastSyncAt: string | null;
  lastSyncSucceeded: boolean | null;
  lastSyncStats: SyncStats | null;
  nextSyncAt: string | null;
  lastError: string | null;
  /** Entra: the redirect address to register with the app for "Sign in with Microsoft". */
  signInRedirectUri: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ConnectorCall {
  id: number;
  kind: "CrmCase" | "Rest";
  operation: string;
  ticketId: string | null;
  ticketNumber: string | null;
  status: "Pending" | "Succeeded" | "Failed";
  attempts: number;
  statusCode: number | null;
  durationMs: number | null;
  error: string | null;
  createdAt: string;
  finishedAt: string | null;
}

export interface RestOutcome {
  ok: boolean;
  status: number | null;
  durationMs: number;
  request: string;
  response: string | null;
  results: Record<string, string | null>;
  externalRef: string | null;
  error: string | null;
}

export type AlertFormat = "Alertmanager" | "Generic";

export interface MonitoringSource {
  id: string;
  name: string;
  format: AlertFormat;
  mapping: Record<string, unknown>;
  webhookUrl: string;
  categoryId: string | null;
  locationId: string | null;
  groupId: string | null;
  resolveOnClear: boolean;
  active: boolean;
  lastAlertAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MonitoringAlert {
  id: number;
  key: string;
  state: "Firing" | "Resolved";
  count: number;
  firstSeenAt: string;
  lastSeenAt: string;
  ticketId: string;
  ticketNumber: string;
  ticketTitle: string;
  ticketState: string;
}

export const restPlaceholders = [
  "ticket.number", "ticket.title", "ticket.description", "ticket.state", "ticket.type", "ticket.priority", "ticket.channel", "ticket.group", "ticket.category",
  "ticket.url", "ticket.createdAt", "ticket.resolutionNotes", "ticket.externalRef", "requester.name", "requester.email", "requester.phone", "requester.employeeId", "tenant.name",
];

export function emptySettings(kind: ConnectorKind): ConnectorSettings {
  switch (kind) {
    case "EntraId":
      return { entra: { directoryId: "", clientId: "", usersGroupId: null, groupMappings: [], signIn: false } };
    case "Ldap":
      return {
        ldap: {
          host: "", port: 636, security: "Ldaps", bindDn: "", baseDn: "", userFilter: "(&(objectCategory=person)(objectClass=user))", activeDirectory: true, groupMappings: [], signIn: false,
        },
      };
    case "Rest":
      return {
        rest: {
          baseUrl: "", auth: "Bearer", authHeader: null, username: null, tokenUrl: null, clientId: null, scope: null, headers: {},
          operations: [emptyOperation()],
        },
      };
    default:
      return { crm: { url: null, apiUrl: null, directoryId: null, clientId: null, statusMap: {}, customerGroups: true } };
  }
}

export function emptyOperation(): RestOperation {
  return { key: "update-case", name: { en: null, ar: null }, method: "POST", path: "/", body: null, results: [], externalRefPath: null };
}

/** The code of an error such as "sign-in-failed: …", for its translation. */
export function connectorErrorCode(error: string | null | undefined): string | null {
  if (!error) return null;
  const index = error.indexOf(":");
  return index > 0 ? error.slice(0, index) : error;
}
