import { useLocale, useTranslations } from "next-intl";
import { Localized } from "@/components/directory/common";
import type { Change } from "@/lib/changes";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import { ChangeStageBadge, RiskBadge } from "./common";

/** The facts of a change: type, stage, risk, template, category, group, assignee, approval and review (task H3). */
export function ChangeFacts({ change, display }: { change: Change; display: DateDisplayOptions }) {
  const t = useTranslations("changes.facts");
  const tKinds = useTranslations("changes.kinds");
  const tOutcomes = useTranslations("changes.outcomes");
  const tClosures = useTranslations("changes.closures");
  const tApproval = useTranslations("changes.approvalStates");
  const locale = useLocale() as Locale;
  const row = (label: string, value: React.ReactNode) => (
    <div className="min-w-0"><dt className="font-semibold">{label}</dt><dd className="break-words">{value}</dd></div>
  );
  return (
    <dl className="grid gap-3 rounded-lg border border-line p-4 sm:grid-cols-2 lg:grid-cols-3" data-testid="change-facts">
      {row(t("kind"), tKinds(change.kind))}
      {row(t("stage"), <ChangeStageBadge stage={change.stage} />)}
      {row(t("risk"), <RiskBadge level={change.riskLevel} />)}
      {change.template && row(t("template"), <Localized text={change.template.name} locale={locale} />)}
      {row(t("category"), change.category ? inLanguage(change.category.path, locale).value : "—")}
      {row(t("group"), change.group ? <Localized text={change.group.name} locale={locale} /> : "—")}
      {row(t("assignee"), change.assignee?.displayName ?? "—")}
      {row(t("raisedBy"), change.createdBy?.displayName ?? "—")}
      {change.approval && row(t("approval"), (
        <>{tApproval(change.approval.state)}{change.approval.state === "Pending" && change.approval.levelName
          ? <> · <Localized text={change.approval.levelName} locale={locale} /> ({change.approval.currentLevel + 1}/{change.approval.levels})</> : null}</>
      ))}
      {change.approvedAt && row(t("approvedAt"), formatDateTime(new Date(change.approvedAt), display))}
      {change.closure && row(t("closure"), tClosures(change.closure))}
      {change.resolutionNotes && row(change.closure === "Completed" || change.stage === "Review" ? t("implementationNotes") : t("reason"),
        <span className="whitespace-pre-wrap" dir="auto">{change.resolutionNotes}</span>)}
      {change.outcome && row(t("outcome"), tOutcomes(change.outcome))}
      {change.reviewNotes && row(t("reviewNotes"), <span className="whitespace-pre-wrap" dir="auto">{change.reviewNotes}</span>)}
    </dl>
  );
}
