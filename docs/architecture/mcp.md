# MCP server and customers' MCP servers (task E6, module 9)

Covers module 9 "MCP server for agentic AI":

- **Built-in MCP server.** Any agentic AI (Claude, ChatGPT, Microsoft Copilot Studio or the customer's own agents) can work with Zelvo through it.
- **Per-agent credentials and tools.** Each agent signs in with its own credentials and gets only the tools that are turned on for it.
- **Approval.** Changes need a person's approval, and every AI action is in the audit log, marked as done by an AI agent.
- **Customers' MCP servers.** Zelvo's own AI can use tools of the customer's MCP servers.

Product owner decisions (2026-11-24):

1. **Who an agent acts for.**
   - An agent acts **as itself**, with its own integration credential (API key, or OAuth client with the client credentials grant).
   - When it raises an incident, it **names an existing requester** by email address.
   - The ticket records the agent (`tickets.created_by_api_client_id`, channel `AiAgent`).
   - Sign-in by a person on the agent's behalf (OAuth authorization code, needed by claude.ai and ChatGPT connectors) comes later.
2. **Approval** is **on by default** for every tool that changes data. An administrator turns it off per agent and tool.
   - A waiting change is decided by **anyone who holds the permission the change needs** (for example `incidents.create`), in **AI approvals**.
   - Requests expire after **24 hours**.
3. **Catalog requests**: the "raise a catalog request" and "list services" tools are added with the service catalog in **task C4**. E6 builds the tool registry they plug into.
4. **Customers' MCP servers**: Settings → AI → MCP servers, plus tool calls in the AI gateway.
   - Read-only tools run on their own.
   - Tools the administrator marks as changing data run only after a person's approval.
   - The first feature to use them is the virtual agent (E5): read-only tools in the chat; a tool that changes data hands the chat to a person (`ai-features.md`).

The MCP SDK is the official C# SDK (`ModelContextProtocol.AspNetCore` and `ModelContextProtocol.Core` 2.4.0, Apache-2.0). It is a new dependency, proposed in the E6 pull request.

## Code

| Piece | Where |
| --- | --- |
| Tables `mcp_agents`, `mcp_actions`, `mcp_servers` (row-level security; migration `Mcp`) and `tickets.created_by_api_client_id` | `src/api/Zelvo.Data/Mcp` |
| MCP server: transport, access check, tools, runner, approvals | `src/api/Zelvo.Api/Mcp` |
| Client for customers' servers and tool calls in the gateway | `src/api/Zelvo.Ai/McpServerConnector.cs`, `AiToolRunner.cs` |
| Settings → AI → MCP server and MCP servers; AI approvals | `src/web/src/components/settings/ai/Mcp*.tsx`, `src/web/src/components/ai`, `src/web/src/app/[locale]/ai-approvals` |

## The built-in MCP server

**Endpoint.** The server answers Streamable HTTP at `/api/v1/mcp` in stateless mode, so every API instance can answer without sessions. Agents send:

- the tenant in `X-Zelvo-Tenant` (or rely on the installation's default tenant);
- their credential: `X-Api-Key: zvk_…`, or `Authorization: Bearer <token>` for OAuth clients.

**Who may connect.** `UseZelvoMcpAccess` runs after authentication and lets in only:

1. requests with a tenant (otherwise 400 `tenant-required`);
2. integrations that are signed in (otherwise 401 `unauthenticated`, with `WWW-Authenticate: Bearer`);
3. tenants that have the server **on** (setting `mcp.server.enabled`, off by default; otherwise 403 `mcp-off`);
4. integrations that an administrator made an **AI agent** (a `mcp_agents` row; otherwise 403 `not-an-ai-agent`). People are refused this way too.

**Integration rules still apply.** Revoked or expired credentials are refused, rate limits apply per integration, and the integration log and audit entry are written for every call (E1).

**Tools.** `tools/list` shows only the tools that are turned on for the agent and whose permission its role and scopes hold, plus `get_action_status`. Each tool carries MCP annotations (`readOnlyHint` and so on).

| Tool | Permission | Changes data |
| --- | --- | --- |
| `search_knowledge`: published articles by text (internal ones only when the agent may read them) | none | no |
| `get_knowledge_article`: by number or id | none | no |
| `search_incidents`: by number, state, open, priority, requester email | `incidents.read` | no |
| `get_incident`: status, people, classification, SLA clocks | `incidents.read` | no |
| `get_incident_sla`: response and resolution clocks | `incidents.read` | no |
| `list_sla_policies` | `directory.read` | no |
| `create_incident`: for a requester named by email; impact and urgency 1–3 | `incidents.create` | yes |
| `update_incident`: title, description, impact, urgency | `incidents.edit` | yes |
| `change_incident_state`: InProgress, OnHold (reason), Resolved (notes), Reopened | `incidents.edit` | yes |
| `get_action_status`: the agent's own waiting or decided change | none | no |

**Tools reuse the REST code.** Tools call the same code as the REST API (`IncidentEndpoints`, `KnowledgePortalEndpoints`, `SlaEndpoints`), with the agent as the caller. Field rules, validation and audit are therefore the same as for the API; for example, hidden fields stay hidden.

**Answers.** Every answer is JSON, both as text content and as structured content. Errors set `isError` and carry the problem code (`not-found`, `validation-failed`, `forbidden`, `tool-not-available` and so on).

### Acting as the AI agent

`McpRunner` runs every call in a scope of its own:

- The tenant is set there.
- The caller is the integration (`CurrentUser.SetClient`: its role narrowed by its scopes, never the blocked permissions).
- The audit actor is **`AiAgent`** with the integration's id and name, and the correlation id.

Everything a tool changes is therefore marked in the audit log as done by an AI agent. When a change runs after approval, the audit entries also carry `mcpActionId`, `approvedBy` and `approvedByName`.

**Incidents raised by an agent:**

- Incident creation accepts an AI agent only with `incidents.create` and a named requester (task E6).
- The channel is `AiAgent`, `created_by_id` is empty and `created_by_api_client_id` is the agent.
- The REST endpoint itself stays people-only (`RequirePerson`) for other integrations.

### Changes and approvals

**Recording.** Every call of a tool that changes data is recorded in `mcp_actions`, with:

- the tool and the permission it needs;
- the arguments as sent (JSON, audited by length only);
- a summary written by Zelvo for the approver, for example `Raise an incident for … : "Printer offline"`;
- the ticket it is about, the status, the decision and the result.

**Checks before queueing.** The arguments are checked before anything is queued: unknown requester, bad levels and incidents the agent cannot see are refused at once.

| Approval for the agent and tool | What happens |
| --- | --- |
| On (default) | Status `Pending`; the agent gets `{"status":"pending-approval","actionId",…}` and checks with `get_action_status`. Expires after 24 hours. |
| Off (`mcp_agents.tools_without_approval`) | Runs at once; recorded as `Succeeded` or `Failed` with the result. |

**Deciding.** `GET /api/v1/ai/approvals` lists waiting changes whose permission the person holds (people only). `POST …/{id}/approve` or `…/reject` takes an optional note, which the agent can read.

**Approving:**

1. The decision is saved first. The row version (`xmin`) stops a second approver, who gets 409 `action-not-pending`.
2. The change then runs as the agent, with the agent's *current* role and scopes. A revoked agent, or one that lost the permission, fails without changing anything.
3. The result is recorded.

**Expired and rejected requests** are never run.

## Settings → AI → MCP server (`ai.manage`)

**Settings.**

- The on/off switch.
- Which integrations are AI agents, with their tools and approvals.
- The activity list (`GET /api/v1/ai/mcp/actions`).

**Rules when saving an agent's tools:**

- Each tool needs its permission in the integration's role and scopes (400 otherwise).
- Only tools that change data can skip approval.
- Removing the agent refuses it at once; the integration itself stays.

**API.**

| Endpoint | Purpose |
| --- | --- |
| `GET /ai/mcp` | Status, endpoint path, tools, agents |
| `PUT /ai/mcp/settings` | Turn the server on or off |
| `PUT /ai/mcp/agents/{clientId}` | Make an integration an AI agent, or change its tools |
| `DELETE /ai/mcp/agents/{clientId}` | Remove the agent |
| `GET /ai/mcp/actions` | The activity list |

Listing integrations to choose from needs `integrations.manage` (tenant administrators have both).

## Customers' MCP servers (Settings → AI → MCP servers, `ai.manage`)

**A server** has a name, a Streamable HTTP address and optionally a credential header and credential:

- `Authorization` sends `Bearer <credential>`; other headers send the credential as it is.
- The credential is encrypted with `Zelvo.Secrets` (purpose `ai.mcpServer:<id>.secret`), never returned, and audited when changed (`ai.mcpServer.secret-changed`, without the value).
- A new address needs the credential again.
- Servers are switched off, not deleted.

**Connections.** `McpServerConnector` uses the official MCP client over Zelvo's own connection handler, so:

- the AI outgoing-request policy applies: HTTPS only, loopback, link-local and metadata addresses always refused, private networks only from `Ai:AllowedPrivateNetworks`;
- the address is checked when saved and again on the address connected to after DNS;
- no redirects are followed and no system proxy is used;
- the AI time limit applies, and answers are cut to 32 KB of text.

Errors have stable codes: `address-not-allowed`, `connection-failed`, `timeout`, `server-auth-failed`, `server-error`.

**Tools.**

- **Refresh** tests the connection and reads the tools.
- New tools start **off**. They count as **changing data** unless the server says they are read-only; the server's hint is only a default, and the administrator decides.
- Tools the server no longer lists are kept as `missing` and not offered.

**Tool calls in the gateway (`IAiToolRunner`).** E5 features call `RunAsync` with an ordinary `AiRequest`:

- **The protocol.** The model is told the turned-on tools as a data block (`available-tools`; descriptions from the server are data, not instructions). It answers with one JSON object: `{"tool": "t1", "arguments": {…}}` or `{"answer": "…"}`. This works with every provider, including self-hosted models without native tool support.
- **Gateway rules apply.** Every model call goes through `IAiGateway`: masking, prompt-injection guards, limit, AI log and audit.
- **Arguments.** Placeholders in the model's tool arguments are swapped back to real values on Zelvo's side before the tool is called.
- **Tool answers** go back to the model as `tool-result` data blocks, masked like any input.
- **Read-only tools** run on their own, at most `maxSteps` (4) per run.
- **Tools that change data** stop the run with `ApprovalNeeded`. The feature asks the person (for example in the virtual agent conversation) and then calls `CallApprovedAsync`.
- **Audit.** Every tool call is audited (`ai.mcp.tool.called`, category `Ai`: server, tool, approved, outcome) without its arguments.

## Tests

- `McpServerTests` (real PostgreSQL, official MCP client over Streamable HTTP):
  - only AI agents, only while the server is on;
  - tools per role, scopes and switches;
  - pending approval, approval by a holder of the permission and refusal for others;
  - the incident with channel `AiAgent` and the agent, and the audit actor `AiAgent` with the approver;
  - approving twice refused; rejection; approval turned off; expired requests; agents see only their own actions.
- `McpClientTests` (real PostgreSQL, a test MCP server and the fake AI provider):
  - address rules, credential encrypted and sent as Bearer, tools start off, refused credential reported;
  - a run that looks up with a read-only tool, with the email masked towards the provider and restored for the tool;
  - a run that stops for a tool that changes data, then the approved call;
  - audit entries; disabled servers not offered.
- Web: `Mcp.test.tsx` covers the panels and approvals in English and Arabic, the CSRF header and the error texts.
- End to end: `tests/e2e/specs/mcp.spec.ts` covers an agent raising an incident through MCP, approval by the support agent, and axe in both languages.

## Not in E6

- Sign-in by a person on an agent's behalf (OAuth authorization code with PKCE, protected resource metadata, dynamic client registration), needed for claude.ai and ChatGPT connectors.
- Catalog request tools (C4). Tools for other modules are added as those modules arrive.
- Email or in-app notice of waiting approvals (B5).
- (Done in E5: the virtual agent uses customers' read-only tools; approving changes inside the conversation comes with module 23.)
- Native tool calling of each provider. The JSON protocol works with all of them; native calling can be added behind `IAiToolRunner` later.

## Catalog request tools (task C4)

`search_services`, `get_service`, `request_service` and `get_service_request` were added with fulfilment; see `docs/architecture/fulfilment.md` (MCP catalog tools).
