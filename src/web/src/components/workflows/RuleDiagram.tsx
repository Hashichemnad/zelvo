"use client";

import { useLocale, useTranslations } from "next-intl";
import { textOf } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import {
  fieldKind,
  idListFields,
  nameListFields,
  type WorkflowAction,
  type WorkflowCondition,
  type WorkflowRuleInput,
} from "@/lib/workflows";
import type { WorkflowChoices } from "./WorkflowEditors";

/** Names of what a rule refers to, in the page's language. */
function useNamer(choices: WorkflowChoices) {
  const t = useTranslations("workflows");
  const locale = useLocale() as Locale;
  const unknown = t("diagram.unknownItem");
  const byId = (list: { id: string; name: { en: string | null; ar: string | null } }[], id: string) => {
    const item = list.find((x) => x.id === id);
    return item ? textOf(item.name, locale) : unknown;
  };

  const value = (field: string, v: string): string => {
    switch (field) {
      case "service": {
        const s = choices.services.find((x) => x.id === v);
        return s ? `${s.code} · ${textOf(s.name, locale)}` : unknown;
      }
      case "category":
      case "location": {
        const list = field === "category" ? choices.categories : choices.locations;
        const node = list.find((x) => x.id === v);
        return node ? textOf(node.path, locale) || textOf(node.name, locale) : unknown;
      }
      case "group":
        return byId(choices.groups, v);
      case "requesterDepartment":
        return byId(choices.departments, v);
      case "requesterCustomerGroup":
        return byId(choices.customerGroups ?? [], v);
      case "channel":
        return t.has(`channels.${v}`) ? t(`channels.${v}`) : v;
      case "state":
        return t.has(`states.${v}`) ? t(`states.${v}`) : v;
      case "requesterLanguage":
        return t.has(`languages.${v}`) ? t(`languages.${v}`) : v;
      default:
        return v;
    }
  };

  const recipient = (r: string): string => {
    if (t.has(`recipientKinds.${r}`)) return t(`recipientKinds.${r}`);
    const [kind, rest = ""] = [r.slice(0, r.indexOf(":")), r.slice(r.indexOf(":") + 1)];
    if (kind === "group") return t("groupMembers", { name: byId(choices.groups, rest) });
    if (kind === "groupLeads") return t("groupLeadsOf", { name: byId(choices.groups, rest) });
    if (kind === "role") {
      const role = choices.roles.find((x) => x.key === rest);
      return t("roleHolders", { name: role ? textOf(role.name, locale) : rest });
    }
    return t("diagram.namedPerson");
  };

  const condition = (c: WorkflowCondition): string => {
    const kind = fieldKind(c.field);
    const field = kind === "answer" ? t("diagram.answerField", { key: c.field.slice("answer.".length) }) : t(`fields.${kind}`);
    const op = t(`operators.${c.op}`);
    if (c.op === "empty" || c.op === "notEmpty") return `${field} ${op}`;
    const list = idListFields.includes(kind) || nameListFields.includes(kind);
    const values = list && Array.isArray(c.value) ? c.value.map((v) => value(kind, String(v))).join(t("diagram.or")) : String(c.value ?? "");
    return `${field} ${op} ${values}`;
  };

  const action = (a: WorkflowAction): string => {
    switch (a.type) {
      case "assign":
        return t("diagram.assign", { group: a.groupId ? byId(choices.groups, a.groupId) : unknown, method: t(`methods.${a.method}`) });
      case "notify": {
        const how = [t("diagram.inApp"), ...(a.email ? [t("diagram.byEmail")] : []), ...(a.channels ?? []).map((c) => t(`channels.${c}`))];
        return t("diagram.notify", { recipients: a.recipients.map(recipient).join(t("diagram.and")), how: how.join(t("diagram.and")) });
      }
      case "approval":
        return t("diagram.approval", { stage: t(`stages.${a.stage}`), levels: a.levels.length });
      case "connector": {
        const connector = choices.connectors?.find((x) => x.id === a.connectorId);
        const operation = connector?.operations.find((o) => o.key === a.operation);
        return t("diagram.connector", { connector: connector?.name ?? unknown, operation: operation ? textOf(operation.name, locale) : (a.operation ?? unknown) });
      }
    }
  };

  return { condition, action };
}

/** The arrow between two boxes: down on phones, along the reading direction on wider screens. */
function Arrow() {
  return (
    <li aria-hidden="true" className="flex items-center justify-center text-2xl text-ink-muted">
      <span className="md:hidden">↓</span>
      <span className="hidden md:inline-block rtl:rotate-180">→</span>
    </li>
  );
}

/**
 * A rule as a read-only when → if → then diagram (module 20, task K1): for drafts made from a
 * plain-language description and for every existing rule. Built from lists and headings, so it
 * reads in order with a screen reader and flows right to left in Arabic.
 */
export function RuleDiagram({ rule, choices, id }: { rule: WorkflowRuleInput; choices: WorkflowChoices; id: string }) {
  const t = useTranslations("workflows");
  const { condition, action } = useNamer(choices);
  const box = "flex-1 space-y-2 rounded-lg border-2 border-line bg-surface p-3";
  return (
    <figure className="space-y-2" aria-labelledby={`${id}-caption`} data-testid="rule-diagram">
      <figcaption id={`${id}-caption`} className="text-sm font-semibold">{t("diagram.caption")}</figcaption>
      <ol className="flex flex-col gap-2 md:flex-row md:items-stretch">
        <li className={box}>
          <h4 className="font-bold">{t("when")}</h4>
          <p>{t(`triggers.${rule.trigger}`)}</p>
          <p className="text-sm text-ink-muted">{rule.recordType ? t(`recordTypes.${rule.recordType}`) : t("anyRecordType")}</p>
        </li>
        <Arrow />
        <li className={box}>
          <h4 className="font-bold">{t("if")}</h4>
          {rule.conditions.length === 0 ? (
            <p>{t("diagram.always")}</p>
          ) : (
            <>
              <p className="text-sm text-ink-muted">{t(`matches.${rule.match}`)}</p>
              <ul className="list-disc space-y-1 ps-5">
                {rule.conditions.map((c, i) => <li key={i}>{condition(c)}</li>)}
              </ul>
            </>
          )}
        </li>
        <Arrow />
        <li className={box}>
          <h4 className="font-bold">{t("then")}</h4>
          <ol className="list-decimal space-y-1 ps-5">
            {rule.actions.map((a, i) => <li key={i}>{action(a)}</li>)}
          </ol>
          {rule.stopProcessing && <p className="text-sm text-ink-muted">{t("diagram.stops")}</p>}
        </li>
      </ol>
    </figure>
  );
}
