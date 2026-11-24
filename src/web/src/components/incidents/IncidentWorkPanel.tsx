"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, getJson, patchJson, postJson, putJson } from "@/lib/client-api";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import { isOpen, levelName, type Incident, type IncidentPage, type IncidentSettings, type Locale, type Person, type TicketState } from "@/lib/incidents";
import { AssignmentPicker } from "./AssignmentPicker";
import { inputClass, secondaryButtonClass } from "./ReportIncidentForm";

export interface IncidentWorkPanelProps {
  incident: Incident;
  settings: IncidentSettings | null;
  csrfToken: string;
  permissions: string[];
  /** Agents the incident can be assigned to; null without incidents.assign. */
  assignees: Person[] | null;
  /** Categories, locations and groups to choose from (task B2). */
  directory?: { categories: TreeNode[]; locations: TreeNode[]; groups: GroupSummary[] } | null;
}

type Status = { kind: "idle" | "saving" | "saved" } | { kind: "failed"; code: string | null };

/**
 * Staff actions on an incident: assign, change state (with on-hold reason or resolution notes),
 * impact and urgency, the parent link and the major-incident flag. Each action saves at once and
 * reloads the page's data, so the history shows it.
 */
export function IncidentWorkPanel({ incident, settings, csrfToken, permissions, assignees, directory }: IncidentWorkPanelProps) {
  const t = useTranslations("incidents.work");
  const tCommon = useTranslations("common");
  const tFields = useTranslations("incidents.fields");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [status, setStatus] = useState<Status>({ kind: "idle" });
  const [categoryId, setCategoryId] = useState(incident.category?.id ?? "");
  const [locationId, setLocationId] = useState(incident.location?.id ?? "");
  const [target, setTarget] = useState<TicketState | null>(null);
  const [reason, setReason] = useState("");
  const [notes, setNotes] = useState("");
  const [impact, setImpact] = useState(incident.impact ?? 2);
  const [urgency, setUrgency] = useState(incident.urgency ?? 2);
  const [parentNumber, setParentNumber] = useState(incident.parent?.number ?? "");
  const can = (permission: string) => permissions.includes(permission);
  const closed = incident.state === "Closed";
  const matrix = settings?.priorityMatrix;
  const base = `/v1/incidents/${incident.id}`;

  async function run(action: () => Promise<unknown>) {
    setStatus({ kind: "saving" });
    try {
      await action();
      setStatus({ kind: "saved" });
      setTarget(null);
      router.refresh();
    } catch (error) {
      setStatus({ kind: "failed", code: error instanceof ApiError ? (error.rateLimited ? "rate-limited" : error.code) : null });
      if (error instanceof ApiError && error.code === "version-mismatch") {
        router.refresh();
      }
    }
  }

  const message = () => {
    if (status.kind === "saving") return t("saving");
    if (status.kind === "saved") return t("saved");
    if (status.kind !== "failed") return "";
    if (status.code === "rate-limited") return tCommon("rateLimited");
    return status.code && t.has(`errors.${status.code}`) ? t(`errors.${status.code}`) : t("failed");
  };

  async function saveParent() {
    const number = parentNumber.trim().toUpperCase();
    if (!number) {
      return putJson(`${base}/parent`, { parentId: null }, csrfToken);
    }
    const found = await getJson<IncidentPage>(`/v1/incidents?${new URLSearchParams({ number })}`);
    const parent = found.items[0];
    if (!parent) {
      throw new ApiError("not found", 404, "parent-not-found");
    }
    return putJson(`${base}/parent`, { parentId: parent.id }, csrfToken);
  }

  const reasons = settings?.onHoldReasons.filter((r) => r.active) ?? [];
  const nextStates = incident.nextStates ?? [];
  const sectionClass = "space-y-3 rounded-lg border border-line p-4";

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4" data-testid="incident-work">
      <h2 id={`${id}-heading`} className="text-xl font-bold">
        {t("heading")}
      </h2>

      {can("incidents.assign") && assignees && !closed && (
        <div className="space-y-2">
          <AssignmentPicker
            incident={incident}
            groups={directory?.groups ?? []}
            allAssignees={assignees}
            busy={status.kind === "saving"}
            onSave={(groupId, assigneeId) => void run(() => putJson(`${base}/assignment`, { groupId, assigneeId }, csrfToken))}
          />
          {isOpen(incident.state) && (
            <div className="flex flex-wrap items-center gap-2">
              <button type="button" className={secondaryButtonClass} disabled={status.kind === "saving"}
                aria-describedby={`${id}-route-hint`} onClick={() => void run(() => postJson(`${base}/route`, undefined, csrfToken))}>
                {t("routeAgain")}
              </button>
              <p id={`${id}-route-hint`} className="text-sm text-ink-muted">{t("routeAgainHint")}</p>
            </div>
          )}
        </div>
      )}

      {can("incidents.edit") && !closed && directory && (
        <form
          className={sectionClass}
          onSubmit={(e) => {
            e.preventDefault();
            void run(() => putJson(`${base}/classification`, { categoryId: categoryId || null, locationId: locationId || null }, csrfToken));
          }}
        >
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("classification")}</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {(["category", "location"] as const).map((name) => {
                const nodes = name === "category" ? directory.categories : directory.locations;
                const value = name === "category" ? categoryId : locationId;
                const usable = selectableNodes(nodes);
                const current = incident[name];
                return (
                  <div key={name} className="space-y-1">
                    <label htmlFor={`${id}-${name}`} className="block text-sm">
                      {tFields(name)}
                    </label>
                    <select id={`${id}-${name}`} value={value} onChange={(e) => (name === "category" ? setCategoryId : setLocationId)(e.target.value)} className={inputClass}>
                      <option value="">{t("none")}</option>
                      {current && !usable.some((n) => n.id === current.id) && <option value={current.id}>{textOf(current.path, locale)}</option>}
                      {usable.map((node) => (
                        <option key={node.id} value={node.id}>{textOf(node.path, locale) || textOf(node.name, locale)}</option>
                      ))}
                    </select>
                  </div>
                );
              })}
            </div>
          </fieldset>
          <button type="submit" className={secondaryButtonClass} disabled={status.kind === "saving"}>
            {t("saveClassification")}
          </button>
        </form>
      )}

      {nextStates.length > 0 && (
        <div className={sectionClass}>
          <p className="font-semibold">{t("state")}</p>
          <div className="flex flex-wrap gap-2">
            {nextStates.map((state) => (
              <button
                key={state}
                type="button"
                className={secondaryButtonClass}
                aria-pressed={target === state}
                disabled={status.kind === "saving"}
                onClick={() => {
                  if (state === "OnHold" || state === "Resolved") {
                    setTarget(state);
                  } else {
                    void run(() => postJson(`${base}/transitions`, { state }, csrfToken));
                  }
                }}
              >
                {t(`moveTo.${state}`)}
              </button>
            ))}
          </div>
          {target === "OnHold" && (
            <form
              className="space-y-2"
              onSubmit={(e) => {
                e.preventDefault();
                void run(() => postJson(`${base}/transitions`, { state: "OnHold", onHoldReason: reason }, csrfToken));
              }}
            >
              <label htmlFor={`${id}-reason`} className="block font-semibold">
                {t("reason")}
              </label>
              <select id={`${id}-reason`} value={reason} onChange={(e) => setReason(e.target.value)} required className={`${inputClass} max-w-md`}>
                <option value="">{t("chooseReason")}</option>
                {reasons.map((r) => (
                  <option key={r.key} value={r.key}>
                    {r.name[locale] ?? r.name[locale === "ar" ? "en" : "ar"] ?? r.key}
                  </option>
                ))}
              </select>
              <button type="submit" className={secondaryButtonClass} disabled={!reason || status.kind === "saving"}>
                {t("moveTo.OnHold")}
              </button>
            </form>
          )}
          {target === "Resolved" && (
            <form
              className="space-y-2"
              onSubmit={(e) => {
                e.preventDefault();
                void run(() => postJson(`${base}/transitions`, { state: "Resolved", resolutionNotes: notes }, csrfToken));
              }}
            >
              <label htmlFor={`${id}-notes`} className="block font-semibold">
                {t("notes")}
              </label>
              <textarea id={`${id}-notes`} value={notes} onChange={(e) => setNotes(e.target.value)} required rows={3} dir="auto" className={inputClass} />
              <button type="submit" className={secondaryButtonClass} disabled={!notes.trim() || status.kind === "saving"}>
                {t("moveTo.Resolved")}
              </button>
            </form>
          )}
        </div>
      )}

      {can("incidents.edit") && !closed && matrix && incident.impact !== undefined && (
        <form
          className={sectionClass}
          onSubmit={(e) => {
            e.preventDefault();
            void run(() => patchJson(base, { impact, urgency }, csrfToken));
          }}
        >
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("details")}</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {(["impact", "urgency"] as const).map((name) => (
                <div key={name} className="space-y-1">
                  <label htmlFor={`${id}-${name}`} className="block text-sm">
                    {tFields(name)}
                  </label>
                  <select
                    id={`${id}-${name}`}
                    value={name === "impact" ? impact : urgency}
                    onChange={(e) => (name === "impact" ? setImpact : setUrgency)(Number(e.target.value))}
                    className={inputClass}
                  >
                    {(name === "impact" ? matrix.impacts : matrix.urgencies).map((level) => (
                      <option key={level.level} value={level.level}>
                        {levelName(name === "impact" ? matrix.impacts : matrix.urgencies, level.level, locale)}
                      </option>
                    ))}
                  </select>
                </div>
              ))}
            </div>
          </fieldset>
          <button type="submit" className={secondaryButtonClass} disabled={status.kind === "saving"}>
            {t("saveDetails")}
          </button>
        </form>
      )}

      {can("incidents.edit") && !closed && (
        <form
          className={sectionClass}
          onSubmit={(e) => {
            e.preventDefault();
            void run(saveParent);
          }}
        >
          <label htmlFor={`${id}-parent`} className="block font-semibold">
            {t("parentNumber")}
          </label>
          <div className="flex flex-wrap gap-2">
            <input
              id={`${id}-parent`}
              value={parentNumber}
              onChange={(e) => setParentNumber(e.target.value)}
              dir="ltr"
              aria-describedby={`${id}-parent-hint`}
              className={`${inputClass} max-w-xs`}
            />
            <button type="submit" className={secondaryButtonClass} disabled={status.kind === "saving"}>
              {t("saveParent")}
            </button>
          </div>
          <p id={`${id}-parent-hint`} className="text-sm text-ink-muted">
            {t("parentHint")}
          </p>
        </form>
      )}

      {can("incidents.major") && !closed && (
        <div className={sectionClass}>
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={status.kind === "saving"}
            onClick={() => void run(() => putJson(`${base}/major`, { isMajor: !incident.isMajor }, csrfToken))}
          >
            {incident.isMajor ? t("standDown") : t("declare")}
          </button>
        </div>
      )}

      <p role="status" aria-live="polite" className={status.kind === "failed" ? "text-bad" : "text-ok"}>
        {status.kind === "failed" && status.code === "parent-not-found" ? t("parentNotFound") : message()}
      </p>
    </section>
  );
}
