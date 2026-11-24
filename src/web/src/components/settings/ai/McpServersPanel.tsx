"use client";

import { useId, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson, putJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { errorCode, type McpServerConnection, type McpServerTool } from "@/lib/mcp";
import { cellClass, inputClass, primaryButton, secondaryButton, tableClass } from "../integrations/ui";

/**
 * Settings → AI → MCP servers: customers' own MCP servers whose tools Zelvo's AI may use. Tools
 * start off; tools that change data run only after a person's approval.
 */
export function McpServersPanel({ initial, csrfToken, display }: { initial: McpServerConnection[]; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("settings.ai.mcpServers");
  const tAi = useTranslations("settings.ai");
  const id = useId();
  const [servers, setServers] = useState(initial);
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [authHeader, setAuthHeader] = useState("Authorization");
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function failure(refused: unknown) {
    const problem = refused instanceof ApiError ? (refused.details.urlProblem as string | undefined) : undefined;
    setError(
      refused instanceof ApiError && refused.rateLimited
        ? tAi("rateLimited")
        : problem
          ? t(`urlProblems.${problem === "url-not-https" || problem === "address-not-allowed" ? problem : "url-invalid"}`)
          : refused instanceof ApiError && refused.status === 400
            ? t("invalid")
            : tAi("failed"),
    );
  }

  async function reload() {
    setServers(await getJson<McpServerConnection[]>("/v1/ai/mcp-servers"));
  }

  async function add(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const created = await postJson<McpServerConnection>(
        "/v1/ai/mcp-servers",
        { name: name.trim(), url: url.trim(), authHeader: secret ? authHeader.trim() : null, secret: secret || null },
        csrfToken,
      );
      await postJson(`/v1/ai/mcp-servers/${created.id}/refresh`, {}, csrfToken);
      setName("");
      setUrl("");
      setSecret("");
      await reload();
    } catch (refused) {
      failure(refused);
    } finally {
      setBusy(false);
    }
  }

  async function run(action: () => Promise<unknown>) {
    setError(null);
    try {
      await action();
      await reload();
    } catch (refused) {
      failure(refused);
    }
  }

  return (
    <div className="space-y-6" data-testid="mcp-servers-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {error && <p role="alert" className="text-bad">{error}</p>}
      {servers.length === 0 && <p>{t("empty")}</p>}
      {servers.map((server) => (
        <ServerCard
          key={server.id}
          server={server}
          display={display}
          onRefresh={() => run(() => postJson(`/v1/ai/mcp-servers/${server.id}/refresh`, {}, csrfToken))}
          onStatus={() => run(() => patchJson(`/v1/ai/mcp-servers/${server.id}`, { status: server.status === "Active" ? "Disabled" : "Active" }, csrfToken))}
          onTools={(tools) => run(() => putJson(`/v1/ai/mcp-servers/${server.id}/tools`, { tools }, csrfToken))}
        />
      ))}

      <form onSubmit={add} className="max-w-3xl space-y-4 rounded-md border border-line p-4" aria-labelledby={`${id}-title`} noValidate>
        <h3 id={`${id}-title`} className="text-lg font-semibold">{t("addTitle")}</h3>
        <div className="grid gap-3 sm:grid-cols-2">
          <div>
            <label htmlFor={`${id}-name`} className="font-semibold">{t("name")}</label>
            <input id={`${id}-name`} required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} className={inputClass} />
          </div>
          <div>
            <label htmlFor={`${id}-url`} className="font-semibold">{t("url")}</label>
            <input id={`${id}-url`} dir="ltr" type="url" required value={url} onChange={(e) => setUrl(e.target.value)} className={inputClass} aria-describedby={`${id}-url-help`} />
            <p id={`${id}-url-help`} className="text-sm text-ink-muted">{t("urlHelp")}</p>
          </div>
          <div>
            <label htmlFor={`${id}-header`} className="font-semibold">{t("authHeader")}</label>
            <input id={`${id}-header`} dir="ltr" maxLength={64} value={authHeader} onChange={(e) => setAuthHeader(e.target.value)} className={inputClass} aria-describedby={`${id}-header-help`} />
            <p id={`${id}-header-help`} className="text-sm text-ink-muted">{t("authHeaderHelp")}</p>
          </div>
          <div>
            <label htmlFor={`${id}-secret`} className="font-semibold">{t("secret")}</label>
            <input id={`${id}-secret`} dir="ltr" type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} className={inputClass} aria-describedby={`${id}-secret-help`} />
            <p id={`${id}-secret-help`} className="text-sm text-ink-muted">{t("secretHelp")}</p>
          </div>
        </div>
        <button type="submit" disabled={busy || !name.trim() || !url.trim()} className={primaryButton}>{t("add")}</button>
      </form>
    </div>
  );
}

function ServerCard({
  server,
  display,
  onRefresh,
  onStatus,
  onTools,
}: {
  server: McpServerConnection;
  display: DateDisplayOptions;
  onRefresh: () => Promise<void>;
  onStatus: () => Promise<void>;
  onTools: (tools: { name: string; enabled: boolean; changesData: boolean }[]) => Promise<void>;
}) {
  const t = useTranslations("settings.ai.mcpServers");
  const id = useId();
  const [tools, setTools] = useState<McpServerTool[]>(server.tools);
  const code = errorCode(server.lastTestError);
  const update = (name: string, change: Partial<McpServerTool>) => setTools(tools.map((tool) => (tool.name === name ? { ...tool, ...change } : tool)));

  return (
    <section className="max-w-4xl space-y-3 rounded-md border border-line p-4" aria-labelledby={`${id}-name`} data-testid="mcp-server">
      <h3 id={`${id}-name`} className="text-lg font-semibold">{server.name}</h3>
      <p><code dir="ltr" lang="en">{server.url}</code></p>
      <p className="text-sm">
        {t(`statuses.${server.status}`)} · {server.secretSet ? t("secretSet") : t("noSecret")}
        {server.lastTestAt && (
          <>
            {" · "}
            {server.lastTestSucceeded ? t("lastTestOk", { at: formatDateTime(new Date(server.lastTestAt), display) }) : t("lastTestFailed", { at: formatDateTime(new Date(server.lastTestAt), display) })}
          </>
        )}
      </p>
      {server.lastTestSucceeded === false && code && <p role="status" className="text-bad">{t.has(`errors.${code}`) ? t(`errors.${code}`) : t("errors.other")}</p>}
      <div className="flex flex-wrap gap-2">
        <button type="button" className={secondaryButton} onClick={onRefresh}>{t("refresh")}<span className="sr-only">: {server.name}</span></button>
        <button type="button" className={secondaryButton} onClick={onStatus}>
          {server.status === "Active" ? t("disable") : t("enable")}
          <span className="sr-only">: {server.name}</span>
        </button>
      </div>
      {tools.length === 0 ? (
        <p>{t("noTools")}</p>
      ) : (
        <>
          <div className="overflow-x-auto">
            <table className={tableClass} aria-label={t("toolsOf", { name: server.name })}>
              <thead>
                <tr>
                  <th scope="col" className={cellClass}>{t("tool")}</th>
                  <th scope="col" className={cellClass}>{t("toolOn")}</th>
                  <th scope="col" className={cellClass}>{t("changesData")}</th>
                </tr>
              </thead>
              <tbody>
                {tools.map((tool) => (
                  <tr key={tool.name} data-testid="mcp-server-tool">
                    <td className={cellClass}>
                      <code dir="ltr" lang="en" className="font-semibold">{tool.name}</code>
                      {tool.description && <span dir="auto" className="block text-sm text-ink-muted">{tool.description}</span>}
                      {tool.missing && <span className="block text-sm text-bad">{t("missing")}</span>}
                    </td>
                    <td className={cellClass}>
                      <input type="checkbox" aria-label={t("turnOnTool", { tool: tool.name })} checked={tool.enabled} onChange={() => update(tool.name, { enabled: !tool.enabled })} />
                    </td>
                    <td className={cellClass}>
                      <input type="checkbox" aria-label={t("changesDataFor", { tool: tool.name })} checked={tool.changesData} onChange={() => update(tool.name, { changesData: !tool.changesData })} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="text-sm text-ink-muted">{t("changesDataHelp")}</p>
          <button type="button" className={primaryButton} onClick={() => onTools(tools.map(({ name, enabled, changesData }) => ({ name, enabled, changesData })))}>
            {t("saveTools")}
            <span className="sr-only">: {server.name}</span>
          </button>
        </>
      )}
    </section>
  );
}
