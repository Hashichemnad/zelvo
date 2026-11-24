"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import { selectableNodes, textOf, toInput, type GroupSummary, type TreeNode } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { levelName, type IncidentSettings, type Locale } from "@/lib/incidents";
import {
  formatDuration,
  priorityLevels,
  slaMetrics,
  splitMinutes,
  toMinutes,
  type BusinessCalendar,
  type CustomerGroup,
  type EscalationInput,
  type PolicyInput,
  type SlaMetric,
  type SlaPolicy,
  type SlaPreview,
  type Target,
} from "@/lib/sla";
import { BilingualInputs, Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "@/components/directory/common";

export interface SlaChoices {
  categories: TreeNode[];
  customerGroups: CustomerGroup[];
  calendars: BusinessCalendar[];
  groups: GroupSummary[];
  priorities: IncidentSettings["priorityMatrix"]["priorities"];
}

type TargetRow = { priority: number; responseHours: string; responseMinutes: string; resolutionHours: string; resolutionMinutes: string };

function toRows(targets: Target[]): TargetRow[] {
  return priorityLevels.map((priority) => {
    const target = targets.find((t) => t.priority === priority);
    const response = target?.responseMinutes ? splitMinutes(target.responseMinutes) : null;
    const resolution = target?.resolutionMinutes ? splitMinutes(target.resolutionMinutes) : null;
    return {
      priority,
      responseHours: response ? String(response.hours) : "",
      responseMinutes: response ? String(response.minutes) : "",
      resolutionHours: resolution ? String(resolution.hours) : "",
      resolutionMinutes: resolution ? String(resolution.minutes) : "",
    };
  });
}

const numberClass = `${inputClass} w-20`;

/** Add or change a policy: conditions, calendar, targets per priority and escalation thresholds. */
function PolicyForm({ policy, choices, onSubmit, onCancel, busy }: {
  policy: SlaPolicy | null;
  choices: SlaChoices;
  onSubmit: (input: PolicyInput) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const t = useTranslations("sla.policies");
  const tMetrics = useTranslations("sla.clock.metrics");
  const locale = useLocale() as Locale;
  const id = useId();
  const [name, setName] = useState({ en: policy?.name.en ?? "", ar: policy?.name.ar ?? "" });
  const [active, setActive] = useState(policy?.active ?? true);
  const [categoryId, setCategoryId] = useState(policy?.category?.id ?? "");
  const [customerGroupId, setCustomerGroupId] = useState(policy?.customerGroup?.id ?? "");
  const [calendarId, setCalendarId] = useState(policy?.calendar?.id ?? "");
  const [rows, setRows] = useState<TargetRow[]>(toRows(policy?.targets ?? []));
  const [escalations, setEscalations] = useState<EscalationInput[]>(
    policy
      ? policy.escalations.map((e) => ({ id: e.id, metric: e.metric, percent: e.percent, notifyAssignee: e.notifyAssignee, notifyGroupLeads: e.notifyGroupLeads, notifyGroupId: e.notifyGroup?.id ?? null }))
      : [
          { metric: null, percent: 75, notifyAssignee: true, notifyGroupLeads: true, notifyGroupId: null },
          { metric: null, percent: 100, notifyAssignee: false, notifyGroupLeads: true, notifyGroupId: null },
        ],
  );
  const categories = selectableNodes(choices.categories).concat(choices.categories.filter((c) => c.id === categoryId && !c.selectable));
  const setRow = (index: number, change: Partial<TargetRow>) => setRows(rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  const setEscalation = (index: number, change: Partial<EscalationInput>) => setEscalations(escalations.map((e, i) => (i === index ? { ...e, ...change } : e)));
  const targets: Target[] = rows
    .map((r) => ({ priority: r.priority, responseMinutes: toMinutes(r.responseHours, r.responseMinutes), resolutionMinutes: toMinutes(r.resolutionHours, r.resolutionMinutes) }))
    .filter((t) => t.responseMinutes !== null || t.resolutionMinutes !== null);

  return (
    <form
      className="space-y-4 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      data-testid="policy-form"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          name: toInput(name.en, name.ar),
          active,
          recordType: "Incident",
          categoryId: categoryId || null,
          customerGroupId: customerGroupId || null,
          calendarId: calendarId || null,
          targets,
          escalations,
        });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{policy ? t("editPolicy") : t("addPolicy")}</h3>
      <BilingualInputs id={`${id}-name`} label={t("name")} en={name.en} ar={name.ar} onChange={setName} maxLength={150} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
        {t("active")}
      </label>
      <fieldset className="grid gap-3 sm:grid-cols-3">
        <legend className="font-semibold">{t("when")}</legend>
        <div className="space-y-1">
          <label htmlFor={`${id}-category`} className="block text-sm">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("anyCategory")}</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{textOf(c.path, locale) || textOf(c.name, locale)}</option>
            ))}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-customers`} className="block text-sm">{t("customerGroup")}</label>
          <select id={`${id}-customers`} value={customerGroupId} onChange={(e) => setCustomerGroupId(e.target.value)} className={inputClass}>
            <option value="">{t("anyCustomer")}</option>
            {choices.customerGroups.filter((g) => g.active || g.id === customerGroupId).map((g) => (
              <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
            ))}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-calendar`} className="block text-sm">{t("calendar")}</label>
          <select id={`${id}-calendar`} value={calendarId} onChange={(e) => setCalendarId(e.target.value)} aria-describedby={`${id}-calendar-hint`} className={inputClass}>
            <option value="">{t("defaultCalendar")}</option>
            {choices.calendars.map((c) => (
              <option key={c.id} value={c.id}>{textOf(c.name, locale)}</option>
            ))}
          </select>
        </div>
        <p id={`${id}-calendar-hint`} className="text-sm text-ink-muted sm:col-span-3">{t("whenHint")}</p>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("targets")}</legend>
        <p className="text-sm text-ink-muted">{t("targetsHint")}</p>
        <div className="overflow-x-auto">
          <table className="border-collapse text-start text-sm">
            <thead>
              <tr>
                <th scope="col" className="pe-3 text-start">{t("priority")}</th>
                <th scope="col" className="pe-3 text-start">{tMetrics("Response")}</th>
                <th scope="col" className="pe-3 text-start">{tMetrics("Resolution")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => {
                const priority = levelName(choices.priorities, row.priority, locale);
                return (
                  <tr key={row.priority} className="border-t border-line">
                    <th scope="row" className="py-2 pe-3 text-start font-semibold">{priority}</th>
                    {(["response", "resolution"] as const).map((kind) => (
                      <td key={kind} className="py-2 pe-3">
                        <span className="flex items-center gap-1">
                          <input type="number" min={0} max={8760} inputMode="numeric" value={row[`${kind}Hours`]} className={numberClass}
                            aria-label={t("hoursLabel", { metric: tMetrics(kind === "response" ? "Response" : "Resolution"), priority })}
                            onChange={(e) => setRow(index, { [`${kind}Hours`]: e.target.value } as Partial<TargetRow>)} />
                          <span aria-hidden="true">{t("h")}</span>
                          <input type="number" min={0} max={59} inputMode="numeric" value={row[`${kind}Minutes`]} className={numberClass}
                            aria-label={t("minutesLabel", { metric: tMetrics(kind === "response" ? "Response" : "Resolution"), priority })}
                            onChange={(e) => setRow(index, { [`${kind}Minutes`]: e.target.value } as Partial<TargetRow>)} />
                          <span aria-hidden="true">{t("min")}</span>
                        </span>
                      </td>
                    ))}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("escalations")}</legend>
        <p className="text-sm text-ink-muted">{t("escalationsHint")}</p>
        {escalations.map((escalation, index) => (
          <div key={escalation.id ?? `new-${index}`} className="flex flex-wrap items-end gap-3 rounded-md border border-line p-2">
            <div className="space-y-1">
              <label htmlFor={`${id}-esc-${index}-percent`} className="block text-sm">{t("percent")}</label>
              <input id={`${id}-esc-${index}-percent`} type="number" min={1} max={300} value={escalation.percent} className={numberClass}
                onChange={(e) => setEscalation(index, { percent: Number(e.target.value) })} />
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-esc-${index}-metric`} className="block text-sm">{t("appliesTo")}</label>
              <select id={`${id}-esc-${index}-metric`} value={escalation.metric ?? ""} className={inputClass}
                onChange={(e) => setEscalation(index, { metric: (e.target.value || null) as SlaMetric | null })}>
                <option value="">{t("bothTargets")}</option>
                {slaMetrics.map((m) => (
                  <option key={m} value={m}>{tMetrics(m)}</option>
                ))}
              </select>
            </div>
            <label className="flex items-center gap-2 pb-2">
              <input type="checkbox" checked={escalation.notifyAssignee} onChange={(e) => setEscalation(index, { notifyAssignee: e.target.checked })} />
              {t("notifyAssignee")}
            </label>
            <label className="flex items-center gap-2 pb-2">
              <input type="checkbox" checked={escalation.notifyGroupLeads} onChange={(e) => setEscalation(index, { notifyGroupLeads: e.target.checked })} />
              {t("notifyLeads")}
            </label>
            <div className="space-y-1">
              <label htmlFor={`${id}-esc-${index}-group`} className="block text-sm">{t("notifyGroup")}</label>
              <select id={`${id}-esc-${index}-group`} value={escalation.notifyGroupId ?? ""} className={inputClass}
                onChange={(e) => setEscalation(index, { notifyGroupId: e.target.value || null })}>
                <option value="">{t("noOtherGroup")}</option>
                {choices.groups.filter((g) => g.active || g.id === escalation.notifyGroupId).map((g) => (
                  <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
                ))}
              </select>
            </div>
            <button type="button" className={smallButton} onClick={() => setEscalations(escalations.filter((_, i) => i !== index))}>
              {t("removeEscalation", { percent: escalation.percent })}
            </button>
          </div>
        ))}
        {escalations.length < 10 && (
          <button type="button" className={smallButton}
            onClick={() => setEscalations([...escalations, { metric: null, percent: 50, notifyAssignee: true, notifyGroupLeads: false, notifyGroupId: null }])}>
            {t("addEscalation")}
          </button>
        )}
      </fieldset>

      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || targets.length === 0 || (!name.en.trim() && !name.ar.trim())}>{t("save")}</button>
        <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

/** One line per policy: its conditions and its P1 targets. */
function PolicySummary({ policy, choices, locale, digits }: { policy: SlaPolicy; choices: SlaChoices; locale: Locale; digits: string }) {
  const t = useTranslations("sla.policies");
  const conditions = [
    policy.category ? t("summary.category", { name: textOf(policy.category.path, locale) || textOf(policy.category.name, locale) }) : null,
    policy.customerGroup ? t("summary.customerGroup", { name: textOf(policy.customerGroup.name, locale) }) : null,
  ].filter(Boolean);
  return (
    <div className="space-y-1 text-sm">
      <p>{conditions.length > 0 ? conditions.join(locale === "ar" ? "، " : ", ") : t("summary.always")}</p>
      <ul className="flex flex-wrap gap-x-4">
        {policy.targets.map((target) => (
          <li key={target.priority}>
            <span className="font-semibold">{levelName(choices.priorities, target.priority, locale)}</span>{" "}
            {t("summary.targets", {
              response: target.responseMinutes ? formatDuration(target.responseMinutes, locale, digits) : "—",
              resolution: target.resolutionMinutes ? formatDuration(target.resolutionMinutes, locale, digits) : "—",
            })}
          </li>
        ))}
      </ul>
      <p className="text-ink-muted">
        {policy.calendar ? t("summary.calendar", { name: textOf(policy.calendar.name, locale) }) : t("summary.defaultCalendar")}
        {" · "}
        {t("summary.escalations", { thresholds: policy.escalations.map((e) => `${e.percent}%`).join(", ") || "—" })}
      </p>
    </div>
  );
}

/**
 * Settings → SLA policies (module 8): policies in order (the first match wins), each with targets
 * per priority, a calendar and escalation thresholds; and a panel to try them without changing anything.
 */
export function SlaPoliciesPanel({ initial, choices, csrfToken, canManage, display }: {
  initial: SlaPolicy[];
  choices: SlaChoices;
  csrfToken: string;
  canManage: boolean;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("sla.policies");
  const locale = useLocale() as Locale;
  const id = useId();
  const [policies, setPolicies] = useState([...initial].sort((a, b) => a.position - b.position));
  const [editing, setEditing] = useState<SlaPolicy | "new" | null>(null);
  const [trial, setTrial] = useState({ priority: "3", categoryId: "", groupId: "" });
  const [result, setResult] = useState<SlaPreview | null>(null);
  const saver = useSaver();
  const trialSaver = useSaver();
  const sorted = (list: SlaPolicy[]) => [...list].sort((a, b) => a.position - b.position);
  const reload = async () => setPolicies(sorted(await getJson<SlaPolicy[]>("/v1/sla/policies")));
  const move = (index: number, by: -1 | 1) =>
    void saver.run(async () => {
      const ids = policies.map((p) => p.id);
      [ids[index], ids[index + by]] = [ids[index + by]!, ids[index]!];
      setPolicies(sorted(await putJson<SlaPolicy[]>("/v1/sla/policies/order", { ids }, csrfToken)));
    });
  const catchAll = policies.some((p) => p.active && !p.category && !p.customerGroup);

  return (
    <div className="space-y-8">
      <section aria-labelledby={`${id}-list`} className="space-y-3">
        <h3 id={`${id}-list`} className="text-xl font-bold">{t("list")}</h3>
        <p className="max-w-2xl">{t("listIntro")}</p>
        {!catchAll && <p className="text-warn" role="note">{t("noCatchAll")}</p>}
        {policies.length === 0 ? (
          <p>{t("none")}</p>
        ) : (
          <ol className="space-y-2" data-testid="sla-policies">
            {policies.map((policy, index) => {
              const name = textOf(policy.name, locale);
              return (
                <li key={policy.id} className="space-y-1 rounded-lg border border-line p-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-ink-muted">{index + 1}.</span>
                    <span className="font-semibold"><Localized text={policy.name} locale={locale} /></span>
                    {!policy.active && <span className="rounded-full border border-line px-2 text-xs">{t("inactive")}</span>}
                    {canManage && (
                      <span className="ms-auto flex flex-wrap gap-1">
                        <button type="button" className={smallButton} disabled={saver.busy || index === 0} onClick={() => move(index, -1)}>{t("moveUp", { name })}</button>
                        <button type="button" className={smallButton} disabled={saver.busy || index === policies.length - 1} onClick={() => move(index, 1)}>{t("moveDown", { name })}</button>
                        <button type="button" className={smallButton} onClick={() => setEditing(policy)}>{t("edit", { name })}</button>
                        <button type="button" className={smallButton} disabled={saver.busy}
                          onClick={() => void saver.run(async () => {
                            await deleteRequest(`/v1/sla/policies/${policy.id}`, csrfToken);
                            await reload();
                          })}>
                          {t("delete", { name })}
                        </button>
                      </span>
                    )}
                  </div>
                  <PolicySummary policy={policy} choices={choices} locale={locale} digits={display.digits} />
                </li>
              );
            })}
          </ol>
        )}
        {canManage &&
          (editing ? (
            <PolicyForm
              key={editing === "new" ? "new" : editing.id}
              policy={editing === "new" ? null : editing}
              choices={choices}
              busy={saver.busy}
              onCancel={() => setEditing(null)}
              onSubmit={(input) =>
                void saver.run(async () => {
                  if (editing === "new") await postJson("/v1/sla/policies", input, csrfToken);
                  else await putJson(`/v1/sla/policies/${editing.id}`, input, csrfToken);
                  await reload();
                  setEditing(null);
                })
              }
            />
          ) : (
            <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("addPolicy")}</button>
          ))}
        <SaveStatusLine status={saver.status} />
      </section>

      <section aria-labelledby={`${id}-try`} className="space-y-3">
        <h3 id={`${id}-try`} className="text-xl font-bold">{t("try")}</h3>
        <p className="max-w-2xl">{t("tryIntro")}</p>
        <form
          className="flex flex-wrap items-end gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            void trialSaver.run(async () =>
              setResult(await postJson<SlaPreview>("/v1/sla/policies/test", {
                recordType: "Incident",
                priority: Number(trial.priority),
                categoryId: trial.categoryId || null,
                groupId: trial.groupId || null,
              }, csrfToken)),
            );
          }}
        >
          <div className="space-y-1">
            <label htmlFor={`${id}-try-priority`} className="block text-sm font-semibold">{t("priority")}</label>
            <select id={`${id}-try-priority`} value={trial.priority} onChange={(e) => setTrial({ ...trial, priority: e.target.value })} className={inputClass}>
              {priorityLevels.map((p) => (
                <option key={p} value={p}>{levelName(choices.priorities, p, locale)}</option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-try-category`} className="block text-sm font-semibold">{t("category")}</label>
            <select id={`${id}-try-category`} value={trial.categoryId} onChange={(e) => setTrial({ ...trial, categoryId: e.target.value })} className={inputClass}>
              <option value="">{t("noCategory")}</option>
              {selectableNodes(choices.categories).map((c) => (
                <option key={c.id} value={c.id}>{textOf(c.path, locale) || textOf(c.name, locale)}</option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-try-group`} className="block text-sm font-semibold">{t("group")}</label>
            <select id={`${id}-try-group`} value={trial.groupId} onChange={(e) => setTrial({ ...trial, groupId: e.target.value })} className={inputClass}>
              <option value="">{t("noGroup")}</option>
              {choices.groups.filter((g) => g.active).map((g) => (
                <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
              ))}
            </select>
          </div>
          <button type="submit" className={secondaryButton} disabled={trialSaver.busy}>{t("tryButton")}</button>
        </form>
        <div role="status" aria-live="polite" data-testid="sla-test-result">
          {result &&
            (result.policy ? (
              <div className="space-y-1">
                <p>{t("result", { policy: textOf(result.policy.name, locale), calendar: result.calendar ? textOf(result.calendar.name, locale) : t("allDay") })}</p>
                {result.response && (
                  <p>{t("resultTarget", {
                    metric: t("responseShort"),
                    target: formatDuration(result.response.minutes, locale, display.digits),
                    due: result.response.dueAt ? formatDateTime(new Date(result.response.dueAt), display) : "—",
                  })}</p>
                )}
                {result.resolution && (
                  <p>{t("resultTarget", {
                    metric: t("resolutionShort"),
                    target: formatDuration(result.resolution.minutes, locale, display.digits),
                    due: result.resolution.dueAt ? formatDateTime(new Date(result.resolution.dueAt), display) : "—",
                  })}</p>
                )}
              </div>
            ) : (
              <p>{t("resultNone")}</p>
            ))}
        </div>
        {trialSaver.status.kind === "failed" && <SaveStatusLine status={trialSaver.status} />}
      </section>
    </div>
  );
}
