"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import type { ApiClient } from "@/lib/integrations";
import { ApiError, deleteRequest, getJson, putJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { McpAction, McpAgent, McpServer } from "@/lib/mcp";
import { cellClass, codeClass, dangerButton, inputClass, primaryButton, secondaryButton, tableClass } from "../integrations/ui";

/**
 * Settings → AI → MCP server: the switch, the integrations that are AI agents with their tools
 * and approvals, and what the agents did.
 */
export function McpServerPanel({
  initial,
  clients,
  actions,
  csrfToken,
  display,
}: {
  initial: McpServer;
  clients: ApiClient[];
  actions: McpAction[];
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("settings.ai.mcp");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [server, setServer] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [newAgent, setNewAgent] = useState("");
  const candidates = clients.filter((c) => c.status === "Active" && !server.agents.some((a) => a.clientId === c.id));

  function failure(refused: unknown) {
    setError(refused instanceof ApiError && refused.rateLimited ? tAi("rateLimited") : refused instanceof ApiError && refused.status === 400 ? t("invalid") : tAi("failed"));
  }

  async function reload() {
    setServer(await getJson<McpServer>("/v1/ai/mcp"));
  }

  async function toggleServer() {
    setError(null);
    try {
      setServer(await putJson<McpServer>("/v1/ai/mcp/settings", { enabled: !server.enabled }, csrfToken));
    } catch (refused) {
      failure(refused);
    }
  }

  async function saveAgent(clientId: string, tools: string[], toolsWithoutApproval: string[], name: string) {
    setError(null);
    setSaved(null);
    try {
      await putJson(`/v1/ai/mcp/agents/${clientId}`, { tools, toolsWithoutApproval: toolsWithoutApproval.filter((x) => tools.includes(x)) }, csrfToken);
      await reload();
      setSaved(t("agentSaved", { name }));
    } catch (refused) {
      failure(refused);
    }
  }

  async function removeAgent(agent: McpAgent) {
    setError(null);
    try {
      await deleteRequest(`/v1/ai/mcp/agents/${agent.clientId}`, csrfToken);
      await reload();
    } catch (refused) {
      failure(refused);
    }
  }

  return (
    <div className="space-y-6" data-testid="mcp-server-panel">
      <p className="max-w-3xl">{t("intro")}</p>

      <div className="max-w-3xl space-y-3 rounded-md border border-line p-4">
        <p>
          <strong>{t("status")}: </strong>
          <span data-testid="mcp-status">{server.enabled ? t("on") : t("off")}</span>
        </p>
        <button type="button" onClick={toggleServer} className={server.enabled ? dangerButton : primaryButton}>
          {server.enabled ? t("turnOff") : t("turnOn")}
        </button>
        <div>
          <p className="font-semibold">{t("connectTitle")}</p>
          <p className="text-sm">{t("connectHelp")}</p>
          <code dir="ltr" lang="en" className={codeClass}>{`POST <API address>${server.endpoint}\nX-Zelvo-Tenant: <tenant>\nX-Api-Key: <the agent's key>   (or Authorization: Bearer <OAuth token>)`}</code>
        </div>
        <p className="text-sm text-ink-muted">{t("approvalHelp", { hours: server.approvalWindowHours })}</p>
      </div>

      {error && <p role="alert" className="text-bad">{error}</p>}
      {saved && <p role="status">{saved}</p>}

      <section aria-labelledby={`${id}-agents`} className="space-y-4">
        <h3 id={`${id}-agents`} className="text-lg font-semibold">{t("agentsTitle")}</h3>
        {server.agents.length === 0 && <p>{t("noAgents")}</p>}
        {server.agents.map((agent) => (
          <AgentCard key={agent.clientId} agent={agent} server={server} onSave={saveAgent} onRemove={removeAgent} />
        ))}

        {candidates.length === 0 ? (
          <p role="note">{t("noCandidates")}</p>
        ) : (
          <div className="flex max-w-3xl flex-wrap items-end gap-3">
            <div className="grow">
              <label htmlFor={`${id}-new`} className="font-semibold">{t("addAgent")}</label>
              <select id={`${id}-new`} value={newAgent} onChange={(e) => setNewAgent(e.target.value)} className={inputClass}>
                <option value="">{t("chooseIntegration")}</option>
                {candidates.map((c) => (
                  <option key={c.id} value={c.id}>{c.name}</option>
                ))}
              </select>
            </div>
            <button
              type="button"
              disabled={!newAgent}
              className={secondaryButton}
              onClick={async () => {
                const client = candidates.find((c) => c.id === newAgent);
                await saveAgent(newAgent, [], [], client?.name ?? "");
                setNewAgent("");
              }}
            >
              {t("makeAgent")}
            </button>
          </div>
        )}
      </section>

      <section aria-labelledby={`${id}-activity`} className="space-y-3">
        <h3 id={`${id}-activity`} className="text-lg font-semibold">{t("activityTitle")}</h3>
        {actions.length === 0 ? (
          <p>{t("noActivity")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className={tableClass} aria-label={t("activityTitle")}>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("requested")}</th>
                  <th scope="col" className={cellClass}>{t("agent")}</th>
                  <th scope="col" className={cellClass}>{t("change")}</th>
                  <th scope="col" className={cellClass}>{t("actionStatus")}</th>
                  <th scope="col" className={cellClass}>{t("decidedBy")}</th>
                </tr>
              </thead>
              <tbody>
                {actions.map((action) => (
                  <tr key={action.id} data-testid="mcp-action-row">
                    <td className={cellClass}>{formatDateTime(new Date(action.requestedAt), display)}</td>
                    <td className={cellClass}>{action.agent.name}</td>
                    <td className={cellClass}><span dir="auto">{action.summary}</span></td>
                    <td className={cellClass}>{t(`statuses.${action.status}`)}{action.errorCode ? ` (${action.errorCode})` : ""}</td>
                    <td className={cellClass}>{action.decidedBy?.name ?? (action.approvalRequired ? "" : t("noApproval"))}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}

function AgentCard({
  agent,
  server,
  onSave,
  onRemove,
}: {
  agent: McpAgent;
  server: McpServer;
  onSave: (clientId: string, tools: string[], withoutApproval: string[], name: string) => Promise<void>;
  onRemove: (agent: McpAgent) => Promise<void>;
}) {
  const t = useTranslations("settings.ai.mcp");
  const id = useId();
  const [tools, setTools] = useState(agent.tools);
  const [withoutApproval, setWithoutApproval] = useState(agent.toolsWithoutApproval);
  const toggle = (list: string[], name: string) => (list.includes(name) ? list.filter((x) => x !== name) : [...list, name]);

  return (
    <fieldset className="max-w-4xl space-y-3 rounded-md border border-line p-4" data-testid="mcp-agent" aria-labelledby={`${id}-name`}>
      <legend id={`${id}-name`} className="px-1 text-lg font-semibold">{agent.name}</legend>
      <p className="text-sm">
        {t("agentRole", { role: agent.role })} · {t(`kinds.${agent.kind}`)}
        {!agent.usable && <strong className="text-bad"> · {t("notUsable")}</strong>}
      </p>
      <div className="overflow-x-auto">
        <table className={tableClass} aria-label={t("toolsOf", { name: agent.name })}>
          <thead>
            <tr>
              <th scope="col" className={cellClass}>{t("tool")}</th>
              <th scope="col" className={cellClass}>{t("toolOn")}</th>
              <th scope="col" className={cellClass}>{t("needsApproval")}</th>
            </tr>
          </thead>
          <tbody>
            {server.tools.map((tool) => {
              const allowed = agent.allowedTools.includes(tool.name);
              const on = tools.includes(tool.name);
              return (
                <tr key={tool.name}>
                  <td className={cellClass}>
                    <span className="font-semibold">{t(`tools.${tool.name}`)}</span>{" "}
                    <code dir="ltr" lang="en" className="text-xs">{tool.name}</code>
                    {!allowed && <span className="block text-sm text-ink-muted">{t("notAllowed", { permission: tool.permission ?? "" })}</span>}
                  </td>
                  <td className={cellClass}>
                    <input
                      type="checkbox"
                      id={`${id}-${tool.name}-on`}
                      aria-label={t("turnOnTool", { tool: t(`tools.${tool.name}`) })}
                      checked={on}
                      disabled={!allowed && !on}
                      onChange={() => setTools(toggle(tools, tool.name))}
                    />
                  </td>
                  <td className={cellClass}>
                    {tool.changesData ? (
                      <input
                        type="checkbox"
                        aria-label={t("approvalFor", { tool: t(`tools.${tool.name}`) })}
                        checked={!withoutApproval.includes(tool.name)}
                        disabled={!on}
                        onChange={() => setWithoutApproval(toggle(withoutApproval, tool.name))}
                      />
                    ) : (
                      <span className="text-sm text-ink-muted">{t("readOnly")}</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" className={primaryButton} onClick={() => onSave(agent.clientId, tools, withoutApproval, agent.name)}>
          {t("saveAgent")}
          <span className="sr-only">: {agent.name}</span>
        </button>
        <button type="button" className={dangerButton} onClick={() => onRemove(agent)}>
          {t("removeAgent")}
          <span className="sr-only">: {agent.name}</span>
        </button>
      </div>
    </fieldset>
  );
}
