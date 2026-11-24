"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { ApiError, getJson, postJson } from "@/lib/client-api";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Page } from "@/lib/integrations";
import type { McpAction } from "@/lib/mcp";
import { codeClass, dangerButton, inputClass, primaryButton } from "../settings/integrations/ui";

/**
 * AI approvals (module 9, task E6): changes AI agents asked for through the MCP server that the
 * signed-in person may decide, because they hold the permission the change needs.
 */
export function AiApprovalsList({ initial, csrfToken, display }: { initial: Page<McpAction>; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("aiApprovals");
  const [page, setPage] = useState(initial);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function decide(action: McpAction, decision: "approve" | "reject", note: string) {
    setError(null);
    setMessage(null);
    try {
      const decided = await postJson<McpAction>(`/v1/ai/approvals/${action.id}/${decision}`, { note: note.trim() || null }, csrfToken);
      setMessage(
        decision === "reject"
          ? t("rejected")
          : decided.status === "Succeeded"
            ? t("approved", { ticket: decided.ticket?.name ?? "" })
            : t("approvedButFailed", { error: decided.error ?? decided.errorCode ?? "" }),
      );
      setPage(await getJson<Page<McpAction>>("/v1/ai/approvals"));
    } catch (refused) {
      setError(refused instanceof ApiError && refused.status === 409 ? t("notPending") : t("failed"));
      setPage(await getJson<Page<McpAction>>("/v1/ai/approvals").catch(() => page));
    }
  }

  return (
    <div className="space-y-4">
      <div aria-live="polite">
        {message && <p role="status">{message}</p>}
        {error && <p role="alert" className="text-bad">{error}</p>}
      </div>
      {page.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ul className="space-y-4">
          {page.items.map((action) => (
            <ApprovalItem key={action.id} action={action} display={display} onDecide={decide} />
          ))}
        </ul>
      )}
    </div>
  );
}

function ApprovalItem({
  action,
  display,
  onDecide,
}: {
  action: McpAction;
  display: DateDisplayOptions;
  onDecide: (action: McpAction, decision: "approve" | "reject", note: string) => Promise<void>;
}) {
  const t = useTranslations("aiApprovals");
  const tTools = useTranslations("settings.ai.mcp.tools");
  const id = useId();
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const run = async (decision: "approve" | "reject") => {
    setBusy(true);
    try {
      await onDecide(action, decision, note);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="space-y-3 rounded-md border border-line p-4" data-testid="ai-approval" aria-labelledby={`${id}-summary`}>
      <p id={`${id}-summary`} className="font-semibold" dir="auto">{action.summary}</p>
      <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t("agent")}</dt>
        <dd>{action.agent.name}</dd>
        <dt className="font-semibold">{t("tool")}</dt>
        <dd>{tTools.has(action.tool) ? tTools(action.tool) : action.tool}</dd>
        <dt className="font-semibold">{t("requested")}</dt>
        <dd>{formatDateTime(new Date(action.requestedAt), display)}</dd>
        {action.expiresAt && (
          <>
            <dt className="font-semibold">{t("expires")}</dt>
            <dd>{formatDateTime(new Date(action.expiresAt), display)}</dd>
          </>
        )}
        {action.ticket?.name && (
          <>
            <dt className="font-semibold">{t("ticket")}</dt>
            <dd><Link href={`/incidents/${action.ticket.id}`} className="text-brand underline">{action.ticket.name}</Link></dd>
          </>
        )}
      </dl>
      <details>
        <summary className="cursor-pointer font-semibold">{t("details")}</summary>
        <code dir="ltr" lang="en" className={codeClass}>{JSON.stringify(action.arguments, null, 2)}</code>
      </details>
      <div>
        <label htmlFor={`${id}-note`} className="font-semibold">{t("note")}</label>
        <input id={`${id}-note`} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} className={inputClass} />
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} className={primaryButton} onClick={() => run("approve")}>
          {t("approve")}<span className="sr-only">: {action.summary}</span>
        </button>
        <button type="button" disabled={busy} className={dangerButton} onClick={() => run("reject")}>
          {t("reject")}<span className="sr-only">: {action.summary}</span>
        </button>
      </div>
    </li>
  );
}
