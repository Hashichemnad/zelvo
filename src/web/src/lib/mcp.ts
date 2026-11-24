/** MCP (module 9, task E6): the shapes the API returns for the built-in MCP server, approvals and customers' MCP servers. */

export type McpActionStatus = "Pending" | "Rejected" | "Expired" | "Succeeded" | "Failed";

export interface McpTool {
  name: string;
  description: string;
  permission: string | null;
  changesData: boolean;
}

export interface McpAgent {
  clientId: string;
  name: string;
  kind: "ApiKey" | "OAuthClient";
  status: "Active" | "Revoked";
  usable: boolean;
  role: string;
  tools: string[];
  toolsWithoutApproval: string[];
  allowedTools: string[];
  updatedAt: string;
}

export interface McpServer {
  enabled: boolean;
  endpoint: string;
  tools: McpTool[];
  agents: McpAgent[];
  pendingApprovals: number;
  approvalWindowHours: number;
}

export interface McpReference {
  id: string;
  name: string | null;
}

export interface McpAction {
  id: string;
  agent: McpReference;
  tool: string;
  summary: string;
  arguments: unknown;
  status: McpActionStatus;
  approvalRequired: boolean;
  requestedAt: string;
  expiresAt: string | null;
  decidedBy: McpReference | null;
  decidedAt: string | null;
  note: string | null;
  ticket: McpReference | null;
  completedAt: string | null;
  errorCode: string | null;
  error: string | null;
}

export interface McpServerTool {
  name: string;
  title?: string | null;
  description?: string | null;
  readOnlyHint: boolean;
  enabled: boolean;
  changesData: boolean;
  missing: boolean;
}

export interface McpServerConnection {
  id: string;
  name: string;
  url: string;
  authHeader: string | null;
  secretSet: boolean;
  secretSetAt: string | null;
  status: "Active" | "Disabled";
  tools: McpServerTool[];
  toolsRefreshedAt: string | null;
  lastTestAt: string | null;
  lastTestSucceeded: boolean | null;
  lastTestError: string | null;
  updatedAt: string;
}

/** Permissions of the MCP tools that change data: people holding one may decide AI agents' changes. */
export const aiApprovalPermissions = ["incidents.create", "incidents.edit"] as const;

/** Whether the AI approvals page is worth showing to someone with these permissions. */
export function canDecideAiActions(permissions: readonly string[]): boolean {
  return aiApprovalPermissions.some((p) => permissions.includes(p));
}

/** The first part of an error such as "server-auth-failed: …", for translation. */
export function errorCode(error: string | null): string | null {
  if (!error) return null;
  const index = error.indexOf(":");
  return index > 0 ? error.slice(0, index) : error;
}
