"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import {
  assignmentMethods,
  languages,
  selectableNodes,
  textOf,
  toInput,
  type AssignmentDefault,
  type AssignmentMethod,
  type AssignmentRule,
  type GroupSummary,
  type Language,
  type RuleInput,
  type RuleTestResult,
  type Skill,
  type TreeNode,
} from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { BilingualInputs, Localized, SaveStatusLine, inputClass, primaryButton, secondaryButton, smallButton, useSaver } from "./common";

export interface DirectoryChoices {
  categories: TreeNode[];
  locations: TreeNode[];
  groups: GroupSummary[];
  skills: Skill[];
}

/** Options of a tree select: active nodes (with active ancestors) by path, plus the current one. */
function TreeOptions({ nodes, current, locale }: { nodes: TreeNode[]; current?: string | null; locale: Locale }) {
  const usable = selectableNodes(nodes);
  const extra = current && !usable.some((n) => n.id === current) ? nodes.filter((n) => n.id === current) : [];
  return (
    <>
      {[...usable, ...extra].map((node) => (
        <option key={node.id} value={node.id}>
          {textOf(node.path, locale) || textOf(node.name, locale)}
        </option>
      ))}
    </>
  );
}

/** Add or change one rule: conditions, then where the ticket goes. The skill only applies to round-robin. */
export function RuleForm({
  rule,
  choices,
  onSubmit,
  onCancel,
  busy,
}: {
  rule: AssignmentRule | null;
  choices: DirectoryChoices;
  onSubmit: (input: RuleInput) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const t = useTranslations("directory.assignment");
  const tLanguages = useTranslations("directory.languages");
  const locale = useLocale() as Locale;
  const id = useId();
  const [name, setName] = useState({ en: rule?.name.en ?? "", ar: rule?.name.ar ?? "" });
  const [active, setActive] = useState(rule?.active ?? true);
  const [categoryId, setCategoryId] = useState(rule?.category?.id ?? "");
  const [locationId, setLocationId] = useState(rule?.location?.id ?? "");
  const [language, setLanguage] = useState<Language | "">(rule?.language ?? "");
  const [groupId, setGroupId] = useState(rule?.group.id ?? "");
  const [method, setMethod] = useState<AssignmentMethod>(rule?.method ?? "GroupQueue");
  const [skillId, setSkillId] = useState(rule?.skill?.id ?? "");
  const groups = choices.groups.filter((g) => g.active || g.id === groupId);
  const skills = choices.skills.filter((s) => s.active || s.id === skillId);

  return (
    <form
      className="space-y-4 rounded-lg border border-line p-4"
      aria-labelledby={`${id}-heading`}
      data-testid="rule-form"
      onSubmit={(e) => {
        e.preventDefault();
        onSubmit({
          name: toInput(name.en, name.ar),
          active,
          categoryId: categoryId || null,
          locationId: locationId || null,
          language: language || null,
          groupId,
          method,
          skillId: method === "RoundRobin" ? skillId || null : null,
        });
      }}
    >
      <h3 id={`${id}-heading`} className="text-xl font-bold">{rule ? t("editRule") : t("addRule")}</h3>
      <BilingualInputs id={`${id}-name`} label={t("ruleName")} en={name.en} ar={name.ar} onChange={setName} />
      <label className="flex items-center gap-2">
        <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
        {t("ruleActive")}
      </label>
      <fieldset className="grid gap-3 sm:grid-cols-3">
        <legend className="font-semibold">{t("when")}</legend>
        <div className="space-y-1">
          <label htmlFor={`${id}-category`} className="block text-sm">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("anyCategory")}</option>
            <TreeOptions nodes={choices.categories} current={categoryId} locale={locale} />
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-location`} className="block text-sm">{t("location")}</label>
          <select id={`${id}-location`} value={locationId} onChange={(e) => setLocationId(e.target.value)} className={inputClass}>
            <option value="">{t("anyLocation")}</option>
            <TreeOptions nodes={choices.locations} current={locationId} locale={locale} />
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-language`} className="block text-sm">{t("language")}</label>
          <select id={`${id}-language`} value={language} onChange={(e) => setLanguage(e.target.value as Language | "")} className={inputClass}>
            <option value="">{t("anyLanguage")}</option>
            {languages.map((l) => (
              <option key={l} value={l}>{tLanguages(l)}</option>
            ))}
          </select>
        </div>
        <p className="text-sm text-ink-muted sm:col-span-3">{t("whenHint")}</p>
      </fieldset>
      <fieldset className="grid gap-3 sm:grid-cols-3">
        <legend className="font-semibold">{t("then")}</legend>
        <div className="space-y-1">
          <label htmlFor={`${id}-group`} className="block text-sm">{t("group")}</label>
          <select id={`${id}-group`} value={groupId} required onChange={(e) => setGroupId(e.target.value)} className={inputClass}>
            <option value="">{t("chooseGroup")}</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
            ))}
          </select>
        </div>
        <div className="space-y-1">
          <label htmlFor={`${id}-method`} className="block text-sm">{t("method")}</label>
          <select id={`${id}-method`} value={method} onChange={(e) => setMethod(e.target.value as AssignmentMethod)} className={inputClass} aria-describedby={`${id}-method-hint`}>
            {assignmentMethods.map((m) => (
              <option key={m} value={m}>{t(`methods.${m}`)}</option>
            ))}
          </select>
        </div>
        {method === "RoundRobin" && (
          <div className="space-y-1">
            <label htmlFor={`${id}-skill`} className="block text-sm">{t("skill")}</label>
            <select id={`${id}-skill`} value={skillId} onChange={(e) => setSkillId(e.target.value)} className={inputClass}>
              <option value="">{t("anySkill")}</option>
              {skills.map((s) => (
                <option key={s.id} value={s.id}>{textOf(s.name, locale)}</option>
              ))}
            </select>
          </div>
        )}
        <p id={`${id}-method-hint`} className="text-sm text-ink-muted sm:col-span-3">{t(`methodHints.${method}`)}</p>
      </fieldset>
      <div className="flex flex-wrap gap-2">
        <button type="submit" className={primaryButton} disabled={busy || !groupId || (!name.en.trim() && !name.ar.trim())}>{t("saveRule")}</button>
        <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>
      </div>
    </form>
  );
}

/** One line describing a rule's conditions and target. */
function RuleSummary({ rule, locale }: { rule: AssignmentRule; locale: Locale }) {
  const t = useTranslations("directory.assignment");
  const tLanguages = useTranslations("directory.languages");
  const conditions = [
    rule.category ? t("summary.category", { name: textOf(rule.category.path, locale) || textOf(rule.category.name, locale) }) : null,
    rule.location ? t("summary.location", { name: textOf(rule.location.path, locale) || textOf(rule.location.name, locale) }) : null,
    rule.language ? t("summary.language", { language: tLanguages(rule.language) }) : null,
  ].filter(Boolean);
  const target =
    rule.method === "RoundRobin"
      ? rule.skill
        ? t("summary.roundRobinSkill", { group: textOf(rule.group.name, locale), skill: textOf(rule.skill.name, locale) })
        : t("summary.roundRobin", { group: textOf(rule.group.name, locale) })
      : t("summary.queue", { group: textOf(rule.group.name, locale) });
  return (
    <p className="text-sm">
      {conditions.length > 0 ? conditions.join(locale === "ar" ? "، " : ", ") : t("summary.always")} → {target}
    </p>
  );
}

/**
 * Settings → Assignment (module 1, decisions 2026-11-23): rules in order, the first match wins;
 * the default for tickets no rule matches; and a panel to try the rules without assigning anything.
 */
export function AssignmentRulesPanel({
  initialRules,
  initialDefault,
  choices,
  csrfToken,
  canManage,
}: {
  initialRules: AssignmentRule[];
  initialDefault: AssignmentDefault;
  choices: DirectoryChoices;
  csrfToken: string;
  canManage: boolean;
}) {
  const t = useTranslations("directory.assignment");
  const tLanguages = useTranslations("directory.languages");
  const locale = useLocale() as Locale;
  const id = useId();
  const [rules, setRules] = useState([...initialRules].sort((a, b) => a.position - b.position));
  const [editing, setEditing] = useState<AssignmentRule | "new" | null>(null);
  const [fallback, setFallback] = useState<AssignmentDefault>(initialDefault);
  const [trial, setTrial] = useState<{ categoryId: string; locationId: string; language: Language | "" }>({ categoryId: "", locationId: "", language: "" });
  const [result, setResult] = useState<RuleTestResult | null>(null);
  const rulesSaver = useSaver();
  const defaultSaver = useSaver();
  const trialSaver = useSaver();

  const reload = async () => setRules((await getJson<AssignmentRule[]>("/v1/assignment-rules")).sort((a, b) => a.position - b.position));

  const move = (index: number, by: -1 | 1) =>
    void rulesSaver.run(async () => {
      const ids = rules.map((r) => r.id);
      [ids[index], ids[index + by]] = [ids[index + by]!, ids[index]!];
      setRules((await putJson<AssignmentRule[]>("/v1/assignment-rules/order", { ids }, csrfToken)).sort((a, b) => a.position - b.position));
    });

  const groups = choices.groups.filter((g) => g.active || g.id === fallback.groupId);

  return (
    <div className="space-y-8">
      <section aria-labelledby={`${id}-rules`} className="space-y-3">
        <h3 id={`${id}-rules`} className="text-xl font-bold">{t("rules")}</h3>
        <p className="max-w-2xl">{t("rulesIntro")}</p>
        {rules.length === 0 ? (
          <p>{t("noRules")}</p>
        ) : (
          <ol className="space-y-2" data-testid="assignment-rules">
            {rules.map((rule, index) => {
              const name = textOf(rule.name, locale);
              return (
                <li key={rule.id} className="space-y-1 rounded-lg border border-line p-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-ink-muted">{index + 1}.</span>
                    <span className="font-semibold">
                      <Localized text={rule.name} locale={locale} />
                    </span>
                    {!rule.active && <span className="rounded-full border border-line px-2 text-xs">{t("inactive")}</span>}
                    {canManage && (
                      <span className="ms-auto flex flex-wrap gap-1">
                        <button type="button" className={smallButton} disabled={rulesSaver.busy || index === 0} onClick={() => move(index, -1)}>
                          {t("moveUp", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={rulesSaver.busy || index === rules.length - 1} onClick={() => move(index, 1)}>
                          {t("moveDown", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={rulesSaver.busy} onClick={() => setEditing(rule)}>
                          {t("edit", { name })}
                        </button>
                        <button type="button" className={smallButton} disabled={rulesSaver.busy}
                          onClick={() => void rulesSaver.run(async () => {
                            await deleteRequest(`/v1/assignment-rules/${rule.id}`, csrfToken);
                            await reload();
                          })}>
                          {t("delete", { name })}
                        </button>
                      </span>
                    )}
                  </div>
                  <RuleSummary rule={rule} locale={locale} />
                </li>
              );
            })}
          </ol>
        )}
        {canManage &&
          (editing ? (
            <RuleForm
              key={editing === "new" ? "new" : editing.id}
              rule={editing === "new" ? null : editing}
              choices={choices}
              busy={rulesSaver.busy}
              onCancel={() => setEditing(null)}
              onSubmit={(input) =>
                void rulesSaver.run(async () => {
                  if (editing === "new") await postJson("/v1/assignment-rules", input, csrfToken);
                  else await putJson(`/v1/assignment-rules/${editing.id}`, input, csrfToken);
                  await reload();
                  setEditing(null);
                })
              }
            />
          ) : (
            <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("addRule")}</button>
          ))}
        <SaveStatusLine status={rulesSaver.status} />
      </section>

      <section aria-labelledby={`${id}-default`} className="space-y-3">
        <h3 id={`${id}-default`} className="text-xl font-bold">{t("default")}</h3>
        <p className="max-w-2xl">{t("defaultIntro")}</p>
        <form
          className="flex flex-wrap items-end gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            void defaultSaver.run(async () => setFallback(await putJson<AssignmentDefault>("/v1/assignment-rules/default", fallback, csrfToken)));
          }}
        >
          <div className="space-y-1">
            <label htmlFor={`${id}-default-group`} className="block text-sm font-semibold">{t("group")}</label>
            <select id={`${id}-default-group`} value={fallback.groupId ?? ""} disabled={!canManage}
              onChange={(e) => setFallback({ ...fallback, groupId: e.target.value || null })} className={inputClass}>
              <option value="">{t("noGroup")}</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-default-method`} className="block text-sm font-semibold">{t("method")}</label>
            <select id={`${id}-default-method`} value={fallback.method} disabled={!canManage}
              onChange={(e) => setFallback({ ...fallback, method: e.target.value as AssignmentMethod })} className={inputClass}>
              {assignmentMethods.map((m) => (
                <option key={m} value={m}>{t(`methods.${m}`)}</option>
              ))}
            </select>
          </div>
          {canManage && <button type="submit" className={primaryButton} disabled={defaultSaver.busy}>{t("saveDefault")}</button>}
        </form>
        <SaveStatusLine status={defaultSaver.status} />
      </section>

      <section aria-labelledby={`${id}-try`} className="space-y-3">
        <h3 id={`${id}-try`} className="text-xl font-bold">{t("try")}</h3>
        <p className="max-w-2xl">{t("tryIntro")}</p>
        <form
          className="flex flex-wrap items-end gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            void trialSaver.run(async () =>
              setResult(await postJson<RuleTestResult>("/v1/assignment-rules/test", {
                categoryId: trial.categoryId || null,
                locationId: trial.locationId || null,
                language: trial.language || null,
              }, csrfToken)),
            );
          }}
        >
          <div className="space-y-1">
            <label htmlFor={`${id}-try-category`} className="block text-sm font-semibold">{t("category")}</label>
            <select id={`${id}-try-category`} value={trial.categoryId} onChange={(e) => setTrial({ ...trial, categoryId: e.target.value })} className={inputClass}>
              <option value="">{t("noCategory")}</option>
              <TreeOptions nodes={choices.categories} locale={locale} />
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-try-location`} className="block text-sm font-semibold">{t("location")}</label>
            <select id={`${id}-try-location`} value={trial.locationId} onChange={(e) => setTrial({ ...trial, locationId: e.target.value })} className={inputClass}>
              <option value="">{t("noLocation")}</option>
              <TreeOptions nodes={choices.locations} locale={locale} />
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-try-language`} className="block text-sm font-semibold">{t("requesterLanguage")}</label>
            <select id={`${id}-try-language`} value={trial.language} onChange={(e) => setTrial({ ...trial, language: e.target.value as Language | "" })} className={inputClass}>
              <option value="">{t("tenantLanguage")}</option>
              {languages.map((l) => (
                <option key={l} value={l}>{tLanguages(l)}</option>
              ))}
            </select>
          </div>
          <button type="submit" className={secondaryButton} disabled={trialSaver.busy}>{t("tryButton")}</button>
        </form>
        <div role="status" aria-live="polite" data-testid="rule-test-result">
          {result && (
            <p>
              {result.group
                ? t("result", {
                    rule: result.rule ? textOf(result.rule.name, locale) : t("defaultName"),
                    group: textOf(result.group.name, locale),
                    assignee: result.assignee ? (result.assignee.displayName ?? result.assignee.id) : t("queue"),
                  })
                : t("resultNoGroup")}
            </p>
          )}
        </div>
        {trialSaver.status.kind === "failed" && <SaveStatusLine status={trialSaver.status} />}
      </section>
    </div>
  );
}
