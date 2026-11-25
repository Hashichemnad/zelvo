"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { BilingualInputs, Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "@/components/directory/common";
import { ApiError, deleteRequest, postJson, putJson } from "@/lib/client-api";
import { textOf } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import {
  approvalAllowed,
  emptyAction,
  fixedRecipients,
  notifyPlaceholders,
  recordTypes,
  toRuleInput,
  workflowTriggers,
  type ConditionMatch,
  type RecordType,
  type RuleTestResult,
  type WorkflowAction,
  type WorkflowProblem,
  type WorkflowRule,
  type WorkflowRuleInput,
  type WorkflowTrigger,
} from "@/lib/workflows";
import { ApprovalLevelsEditor, ConditionsEditor, type WorkflowChoices } from "./WorkflowEditors";

const blankRule = (): WorkflowRuleInput => ({
  name: { en: null, ar: null },
  description: { en: null, ar: null },
  active: true,
  trigger: "TicketCreated",
  recordType: "ServiceRequest",
  match: "All",
  conditions: [],
  actions: [emptyAction("notify")],
  stopProcessing: false,
});

/** Recipients of a notify action: fixed roles of the ticket, plus named groups and roles. */
function RecipientsEditor({ recipients, choices, onChange }: { recipients: string[]; choices: WorkflowChoices; onChange: (r: string[]) => void }) {
  const t = useTranslations("workflows");
  const locale = useLocale() as Locale;
  const toggle = (value: string, on: boolean) => onChange(on ? [...recipients, value] : recipients.filter((r) => r !== value));
  return (
    <fieldset className="space-y-1">
      <legend className="text-sm font-semibold">{t("recipients")}</legend>
      <div className="grid gap-1 sm:grid-cols-2">
        {fixedRecipients.map((r) => (
          <label key={r} className="flex items-center gap-2">
            <input type="checkbox" checked={recipients.includes(r)} onChange={(e) => toggle(r, e.target.checked)} />
            {t(`recipientKinds.${r}`)}
          </label>
        ))}
        {choices.groups.map((g) => (
          <label key={g.id} className="flex items-center gap-2">
            <input type="checkbox" checked={recipients.includes(`group:${g.id}`)} onChange={(e) => toggle(`group:${g.id}`, e.target.checked)} />
            {t("groupMembers", { name: textOf(g.name, locale) })}
          </label>
        ))}
        {choices.roles.map((r) => (
          <label key={r.key} className="flex items-center gap-2">
            <input type="checkbox" checked={recipients.includes(`role:${r.key}`)} onChange={(e) => toggle(`role:${r.key}`, e.target.checked)} />
            {t("roleHolders", { name: textOf(r.name, locale) })}
          </label>
        ))}
      </div>
    </fieldset>
  );
}

/** One action of a rule. */
function ActionEditor({ action, index, rule, choices, onChange, onRemove }: {
  action: WorkflowAction;
  index: number;
  rule: WorkflowRuleInput;
  choices: WorkflowChoices;
  onChange: (a: WorkflowAction) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("workflows");
  const locale = useLocale() as Locale;
  const id = useId();
  return (
    <li className="space-y-3 rounded-lg border border-line p-3" data-testid="rule-action">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div className="space-y-1">
          <label htmlFor={`${id}-type`} className="block text-sm font-semibold">{t("actionNumber", { number: index + 1 })}</label>
          <select id={`${id}-type`} className={inputClass} value={action.type} onChange={(e) => onChange(emptyAction(e.target.value as WorkflowAction["type"]))}>
            <option value="assign">{t("actionTypes.assign")}</option>
            <option value="notify">{t("actionTypes.notify")}</option>
            {(approvalAllowed(rule.trigger, rule.recordType) || action.type === "approval") && <option value="approval">{t("actionTypes.approval")}</option>}
            {((choices.connectors?.length ?? 0) > 0 || action.type === "connector") && <option value="connector">{t("actionTypes.connector")}</option>}
          </select>
        </div>
        <button type="button" className={smallButton} onClick={onRemove} aria-label={t("removeAction", { number: index + 1 })}>{t("remove")}</button>
      </div>
      {action.type === "assign" && (
        <div className="grid gap-2 sm:grid-cols-2">
          <div className="space-y-1">
            <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
            <select id={`${id}-group`} required className={inputClass} value={action.groupId ?? ""} onChange={(e) => onChange({ ...action, groupId: e.target.value || null, assigneeId: null })}>
              <option value="">{t("chooseGroup")}</option>
              {choices.groups.filter((g) => g.active || g.id === action.groupId).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-method`} className="block text-sm">{t("method")}</label>
            <select id={`${id}-method`} className={inputClass} value={action.method} onChange={(e) => onChange({ ...action, method: e.target.value as "GroupQueue" | "RoundRobin" })}>
              <option value="GroupQueue">{t("methods.GroupQueue")}</option>
              <option value="RoundRobin">{t("methods.RoundRobin")}</option>
            </select>
          </div>
        </div>
      )}
      {action.type === "connector" && (
        <div className="space-y-2">
          <div className="grid gap-2 sm:grid-cols-2">
            <div className="space-y-1">
              <label htmlFor={`${id}-connector`} className="block text-sm">{t("connector")}</label>
              <select id={`${id}-connector`} required className={inputClass} value={action.connectorId ?? ""}
                onChange={(e) => onChange({ ...action, connectorId: e.target.value || null, operation: null })}>
                <option value="">{t("chooseConnector")}</option>
                {(choices.connectors ?? []).filter((c) => c.active || c.id === action.connectorId).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </div>
            <div className="space-y-1">
              <label htmlFor={`${id}-operation`} className="block text-sm">{t("operation")}</label>
              <select id={`${id}-operation`} required className={inputClass} value={action.operation ?? ""} onChange={(e) => onChange({ ...action, operation: e.target.value || null })}>
                <option value="">{t("chooseOperation")}</option>
                {(choices.connectors ?? []).find((c) => c.id === action.connectorId)?.operations.map((o) => (
                  <option key={o.key} value={o.key}>{textOf(o.name, locale)}</option>
                ))}
              </select>
            </div>
          </div>
          <p className="text-sm text-ink-muted">{t("connectorHint")}</p>
        </div>
      )}
      {action.type === "notify" && (
        <div className="space-y-3">
          <RecipientsEditor recipients={action.recipients} choices={choices} onChange={(recipients) => onChange({ ...action, recipients })} />
          <BilingualInputs id={`${id}-subject`} label={t("subject")} en={action.subject.en ?? ""} ar={action.subject.ar ?? ""} maxLength={300}
            onChange={(v) => onChange({ ...action, subject: { en: v.en || null, ar: v.ar || null } })} />
          <fieldset className="grid gap-2 sm:grid-cols-2" aria-describedby={`${id}-placeholders`}>
            <legend className="font-semibold">{t("body")}</legend>
            {(["en", "ar"] as const).map((lang) => (
              <div key={lang} className="space-y-1">
                <label htmlFor={`${id}-body-${lang}`} className="block text-sm">{t(lang === "en" ? "inEnglish" : "inArabic")}</label>
                <textarea id={`${id}-body-${lang}`} lang={lang} dir={lang === "ar" ? "rtl" : "ltr"} rows={4} maxLength={5000} className={inputClass}
                  value={action.body[lang] ?? ""} onChange={(e) => onChange({ ...action, body: { ...action.body, [lang]: e.target.value || null } })} />
              </div>
            ))}
          </fieldset>
          <p id={`${id}-placeholders`} className="text-sm text-ink-muted">
            {t("placeholders")} <span dir="ltr">{notifyPlaceholders.map((p) => `{{${p}}}`).join(" ")}</span>
          </p>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={action.email} onChange={(e) => onChange({ ...action, email: e.target.checked })} />
            {t("alsoEmail")}
          </label>
        </div>
      )}
      {action.type === "approval" && (
        <div className="space-y-3">
          <fieldset className="space-y-1">
            <legend className="text-sm font-semibold">{t("stage")}</legend>
            {(["BeforeAssignment", "AfterFulfilment"] as const).map((stage) => (
              <label key={stage} className="flex items-center gap-2">
                <input type="radio" className="size-5" name={`${id}-stage`} checked={action.stage === stage} onChange={() => onChange({ ...action, stage })} />
                {t(`stages.${stage}`)}
              </label>
            ))}
          </fieldset>
          <ApprovalLevelsEditor legend={t("levels")} levels={action.levels} choices={choices} onChange={(levels) => onChange({ ...action, levels })} />
        </div>
      )}
    </li>
  );
}

/** Add or change one rule: when, if, then. */
export function RuleEditor({ initial, choices, busy, problems, onSubmit, onCancel }: {
  initial: WorkflowRuleInput;
  choices: WorkflowChoices;
  busy: boolean;
  problems: WorkflowProblem[];
  onSubmit: (rule: WorkflowRuleInput) => void;
  onCancel: () => void;
}) {
  const t = useTranslations("workflows");
  const id = useId();
  const [rule, setRule] = useState(initial);
  const set = (patch: Partial<WorkflowRuleInput>) => setRule((r) => ({ ...r, ...patch }));
  return (
    <form className="space-y-4 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} data-testid="workflow-rule-form"
      onSubmit={(e) => { e.preventDefault(); onSubmit(rule); }}>
      <h3 id={`${id}-heading`} className="text-xl font-bold">{t("ruleEditor")}</h3>
      {problems.length > 0 && (
        <div role="alert" className="rounded-md border border-bad p-3" data-testid="rule-problems">
          <p className="font-semibold">{t("problemsTitle")}</p>
          <ul className="list-disc ps-5">
            {problems.map((p, i) => (
              <li key={i}>{t.has(`problems.${p.code}`) ? t(`problems.${p.code}`) : p.message} {p.field && <span dir="ltr" className="text-ink-muted">({p.field})</span>}</li>
            ))}
          </ul>
        </div>
      )}
      <BilingualInputs id={`${id}-name`} label={t("ruleName")} en={rule.name.en ?? ""} ar={rule.name.ar ?? ""} maxLength={150}
        onChange={(v) => set({ name: { en: v.en || null, ar: v.ar || null } })} />
      <BilingualInputs id={`${id}-description`} label={t("ruleDescription")} en={rule.description.en ?? ""} ar={rule.description.ar ?? ""} maxLength={1000}
        onChange={(v) => set({ description: { en: v.en || null, ar: v.ar || null } })} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={rule.active} onChange={(e) => set({ active: e.target.checked })} />
        {t("ruleActive")}
      </label>
      <fieldset className="grid gap-3 sm:grid-cols-2">
        <legend className="text-lg font-semibold">{t("when")}</legend>
        <div className="space-y-1">
          <label htmlFor={`${id}-trigger`} className="block text-sm">{t("trigger")}</label>
          <select id={`${id}-trigger`} className={inputClass} value={rule.trigger} onChange={(e) => set({ trigger: e.target.value as WorkflowTrigger })}>
            {workflowTriggers.map((x) => <option key={x} value={x}>{t(`triggers.${x}`)}</option>)}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-type`} className="block text-sm">{t("recordType")}</label>
          <select id={`${id}-type`} className={inputClass} value={rule.recordType ?? ""} onChange={(e) => set({ recordType: (e.target.value || null) as RecordType | null })}>
            <option value="">{t("anyRecordType")}</option>
            {recordTypes.map((x) => <option key={x} value={x}>{t(`recordTypes.${x}`)}</option>)}
          </select>
        </div>
      </fieldset>
      <fieldset className="space-y-3">
        <legend className="text-lg font-semibold">{t("if")}</legend>
        <fieldset className="flex flex-wrap gap-4">
          <legend className="text-sm font-semibold">{t("match")}</legend>
          {(["All", "Any"] as ConditionMatch[]).map((m) => (
            <label key={m} className="flex items-center gap-2">
              <input type="radio" className="size-5" name={`${id}-match`} checked={rule.match === m} onChange={() => set({ match: m })} />
              {t(`matches.${m}`)}
            </label>
          ))}
        </fieldset>
        <ConditionsEditor legend={t("conditions")} conditions={rule.conditions} choices={choices} onChange={(conditions) => set({ conditions })} />
      </fieldset>
      <fieldset className="space-y-3">
        <legend className="text-lg font-semibold">{t("then")}</legend>
        <ol className="space-y-3">
          {rule.actions.map((a, i) => (
            <ActionEditor key={i} index={i} action={a} rule={rule} choices={choices}
              onChange={(next) => set({ actions: rule.actions.map((x, j) => (j === i ? next : x)) })}
              onRemove={() => set({ actions: rule.actions.filter((_, j) => j !== i) })} />
          ))}
        </ol>
        <button type="button" className={smallButton} onClick={() => set({ actions: [...rule.actions, emptyAction("notify")] })}>{t("addAction")}</button>
      </fieldset>
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={rule.stopProcessing} onChange={(e) => set({ stopProcessing: e.target.checked })} />
        {t("stopProcessing")}
      </label>
      <div className="flex gap-2">
        <button type="submit" className={primaryButton} disabled={busy}>{t("saveRule")}</button>
        <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

/**
 * Settings → Workflow rules (task C3, module 10): ordered when / if / then rules, edited without
 * code, with move up / down (keyboard friendly), and a tester that shows which rules hold for an
 * existing ticket.
 */
export function WorkflowRulesPanel({ initialRules, choices, csrfToken }: { initialRules: WorkflowRule[]; choices: WorkflowChoices; csrfToken: string }) {
  const t = useTranslations("workflows");
  const locale = useLocale() as Locale;
  const id = useId();
  const [rules, setRules] = useState(initialRules);
  const [editing, setEditing] = useState<WorkflowRule | "new" | null>(null);
  const [problems, setProblems] = useState<WorkflowProblem[]>([]);
  const [testNumber, setTestNumber] = useState("");
  const [testTrigger, setTestTrigger] = useState<WorkflowTrigger>("TicketCreated");
  const [tested, setTested] = useState<RuleTestResult[] | null>(null);
  const { status, run, busy } = useSaver();

  async function save(input: WorkflowRuleInput) {
    setProblems([]);
    try {
      const saved = editing === "new"
        ? await postJson<WorkflowRule>("/v1/workflow-rules", input, csrfToken)
        : await putJson<WorkflowRule>(`/v1/workflow-rules/${(editing as WorkflowRule).id}`, input, csrfToken, { "If-Match": (editing as WorkflowRule).etag });
      setRules((list) => (editing === "new" ? [...list, saved] : list.map((r) => (r.id === saved.id ? saved : r))));
      setEditing(null);
    } catch (error) {
      if (error instanceof ApiError && Array.isArray(error.details.problems)) setProblems(error.details.problems as WorkflowProblem[]);
      else setProblems([{ field: null, code: error instanceof ApiError ? (error.code ?? "failed") : "failed", message: t("saveFailed") }]);
    }
  }

  async function move(index: number, by: number) {
    const ids = rules.map((r) => r.id);
    [ids[index], ids[index + by]] = [ids[index + by]!, ids[index]!];
    const ordered = await run(() => putJson<WorkflowRule[]>("/v1/workflow-rules/order", { ids }, csrfToken));
    if (ordered) setRules(ordered);
  }

  return (
    <div className="space-y-6">
      {editing ? (
        <RuleEditor key={editing === "new" ? "new" : editing.id} initial={editing === "new" ? blankRule() : toRuleInput(editing)} choices={choices}
          busy={busy} problems={problems} onSubmit={(r) => void save(r)} onCancel={() => { setEditing(null); setProblems([]); }} />
      ) : (
        <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("addRule")}</button>
      )}
      <section aria-labelledby={`${id}-list`} className="space-y-2">
        <h3 id={`${id}-list`} className="text-lg font-semibold">{t("rulesInOrder")}</h3>
        {rules.length === 0 ? (
          <p>{t("noRules")}</p>
        ) : (
          <ol className="space-y-2" data-testid="workflow-rules">
            {rules.map((rule, i) => (
              <li key={rule.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-line p-3">
                <div>
                  <p className="font-semibold"><Localized text={rule.name} locale={locale} />{!rule.active && <span className="ms-2 text-sm text-ink-muted">{t("inactive")}</span>}</p>
                  <p className="text-sm text-ink-muted">
                    {t("summary", {
                      trigger: t(`triggers.${rule.trigger}`),
                      type: rule.recordType ? t(`recordTypes.${rule.recordType}`) : t("anyRecordType"),
                      conditions: rule.conditions.length,
                      actions: rule.actions.map((a) => t(`actionTypes.${a.type}`)).join(", "),
                    })}
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  <button type="button" className={smallButton} disabled={i === 0 || busy} onClick={() => void move(i, -1)}>
                    {t("moveRuleUp", { name: textOf(rule.name, locale) })}</button>
                  <button type="button" className={smallButton} disabled={i === rules.length - 1 || busy} onClick={() => void move(i, 1)}>
                    {t("moveRuleDown", { name: textOf(rule.name, locale) })}</button>
                  <button type="button" className={smallButton} onClick={() => { setProblems([]); setEditing(rule); }}
                    aria-label={t("editRuleNamed", { name: textOf(rule.name, locale) })}>{t("edit")}</button>
                  <button type="button" className={smallButton}
                    onClick={() => void run(async () => { await deleteRequest(`/v1/workflow-rules/${rule.id}`, csrfToken); setRules((l) => l.filter((r) => r.id !== rule.id)); })}
                    aria-label={t("deleteRuleNamed", { name: textOf(rule.name, locale) })}>{t("delete")}</button>
                </div>
              </li>
            ))}
          </ol>
        )}
        <SaveStatusLine status={status} />
      </section>
      <section aria-labelledby={`${id}-test`} className="space-y-2 rounded-lg border border-line p-4">
        <h3 id={`${id}-test`} className="text-lg font-semibold">{t("tryRules")}</h3>
        <form className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => { e.preventDefault(); void run(async () => setTested(await postJson<RuleTestResult[]>("/v1/workflow-rules/test", { number: testNumber, trigger: testTrigger }, csrfToken))); }}>
          <div className="space-y-1">
            <label htmlFor={`${id}-number`} className="block text-sm">{t("ticketNumber")}</label>
            <input id={`${id}-number`} dir="ltr" required className={inputClass} value={testNumber} maxLength={30} onChange={(e) => setTestNumber(e.target.value)} />
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-trigger`} className="block text-sm">{t("trigger")}</label>
            <select id={`${id}-trigger`} className={inputClass} value={testTrigger} onChange={(e) => setTestTrigger(e.target.value as WorkflowTrigger)}>
              {workflowTriggers.map((x) => <option key={x} value={x}>{t(`triggers.${x}`)}</option>)}
            </select>
          </div>
          <button type="submit" className={secondaryButton}>{t("try")}</button>
        </form>
        {tested && (
          <ul className="space-y-1" aria-live="polite" data-testid="rule-test-results">
            {tested.length === 0 && <li>{t("noRulesForTrigger")}</li>}
            {tested.map((r) => (
              <li key={r.id}>
                <Localized text={r.name} locale={locale} />: {r.holds ? t("holds", { actions: r.actions.map((a) => t(`actionTypes.${a}`)).join(", ") }) : t("doesNotHold")}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
