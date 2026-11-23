"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { getJson } from "@/lib/client-api";
import { textOf, type GroupSummary } from "@/lib/directory";
import type { Incident, Locale, Person } from "@/lib/incidents";
import { inputClass, secondaryButtonClass } from "./ReportIncidentForm";

/**
 * Group first, then assignee (task B2): when a group is chosen, only its members can be picked
 * (the API refuses anyone else). Without a group, every agent can be picked.
 */
export function AssignmentPicker({
  incident,
  groups,
  allAssignees,
  busy,
  onSave,
}: {
  incident: Incident;
  groups: GroupSummary[];
  /** Agents incidents can be assigned to when no group is chosen. */
  allAssignees: Person[];
  busy: boolean;
  onSave: (groupId: string | null, assigneeId: string | null) => void;
}) {
  const t = useTranslations("incidents.work");
  const locale = useLocale() as Locale;
  const id = useId();
  const [groupId, setGroupId] = useState(incident.group?.id ?? "");
  const [assigneeId, setAssigneeId] = useState(incident.assignee?.id ?? "");
  // Members of the chosen group, loaded when the group changes; every agent without a group.
  const [loaded, setLoaded] = useState<{ groupId: string; people: Person[] | null } | null>(null);
  const members = !groupId ? allAssignees : loaded?.groupId === groupId ? loaded.people : null;
  const loadFailed = !!groupId && loaded?.groupId === groupId && loaded.people === null;
  const loading = !!groupId && loaded?.groupId !== groupId;

  useEffect(() => {
    if (!groupId) return;
    let cancelled = false;
    getJson<Person[]>(`/v1/incidents/assignees?${new URLSearchParams({ groupId })}`)
      .then((people) => {
        if (cancelled) return;
        setLoaded({ groupId, people });
        setAssigneeId((current) => (people.some((p) => p.id === current) ? current : ""));
      })
      .catch(() => {
        if (!cancelled) setLoaded({ groupId, people: null });
      });
    return () => {
      cancelled = true;
    };
  }, [groupId]);

  const choosable = groups.filter((g) => g.active || g.id === groupId);

  return (
    <form
      className="space-y-3 rounded-lg border border-line p-4"
      data-testid="assignment-picker"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(groupId || null, assigneeId || null);
      }}
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-group`} className="block font-semibold">{t("group")}</label>
          <select id={`${id}-group`} value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass}>
            <option value="">{t("noGroup")}</option>
            {choosable.map((g) => (
              <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
            ))}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-assignee`} className="block font-semibold">{t("assignee")}</label>
          <select id={`${id}-assignee`} value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} className={inputClass}
            aria-describedby={`${id}-assignee-hint`} aria-busy={loading ? true : undefined}>
            <option value="">{groupId ? t("groupQueue") : "—"}</option>
            {(members ?? []).map((person) => (
              <option key={person.id} value={person.id}>{person.displayName ?? person.id}</option>
            ))}
          </select>
          <p id={`${id}-assignee-hint`} className="text-sm text-ink-muted">
            {loadFailed ? t("membersFailed") : groupId ? t("assigneeInGroup") : t("assigneeAny")}
          </p>
        </div>
      </div>
      <button type="submit" className={secondaryButtonClass} disabled={busy || loading}>{t("assign")}</button>
    </form>
  );
}
