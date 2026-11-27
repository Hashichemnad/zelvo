"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Localized } from "@/components/directory/common";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { patchJson } from "@/lib/client-api";
import { selectableNodes, type TreeNode } from "@/lib/directory";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import { inLanguage, type Locale } from "@/lib/incidents";
import type { Problem } from "@/lib/problems";
import { ProblemStageBadge, ProblemStatusLine, useSaver } from "./common";

/** A problem's details, changed in place by people with problems.edit (title, description, category, priority, root cause). */
export function ProblemFacts({ problem, categories, reasonName, canEdit, csrfToken, display }: {
  problem: Problem;
  categories: TreeNode[];
  reasonName: string | null;
  canEdit: boolean;
  csrfToken: string;
  display: DateDisplayOptions;
}) {
  const t = useTranslations("problems.detail");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [editing, setEditing] = useState(false);
  const [form, setForm] = useState({
    title: problem.title, description: problem.description ?? "", categoryId: problem.category?.id ?? "",
    priority: String(problem.priority), rootCause: problem.rootCause ?? "",
  });
  const editable = canEdit && problem.state !== "Closed";

  if (editing) {
    return (
      <form className="space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => {
        e.preventDefault();
        void run(async () => {
          await patchJson(`/v1/problems/${problem.id}`, {
            title: form.title, description: form.description, categoryId: form.categoryId || undefined, clearCategory: !form.categoryId,
            priority: Number(form.priority), rootCause: form.rootCause,
          }, csrfToken, { "If-Match": problem.etag });
          setEditing(false);
          router.refresh();
        });
      }}>
        <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("edit")}</h2>
        <label className="block space-y-1">
          <span className="block font-semibold">{t("titleField")}</span>
          <input value={form.title} required maxLength={300} dir="auto" className={inputClass} onChange={(e) => setForm({ ...form, title: e.target.value })} />
        </label>
        <label className="block space-y-1">
          <span className="block font-semibold">{t("descriptionField")}</span>
          <textarea value={form.description} rows={4} maxLength={20000} dir="auto" className={inputClass} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        </label>
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="block font-semibold">{t("category")}</span>
            <select value={form.categoryId} className={inputClass} onChange={(e) => setForm({ ...form, categoryId: e.target.value })}>
              <option value="">{t("none")}</option>
              {selectableNodes(categories).map((n) => <option key={n.id} value={n.id}>{inLanguage(n.path, locale).value}</option>)}
            </select>
          </label>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("priority")}</span>
            <select value={form.priority} className={inputClass} onChange={(e) => setForm({ ...form, priority: e.target.value })}>
              {[1, 2, 3, 4, 5].map((p) => <option key={p} value={p}>{`P${p}`}</option>)}
            </select>
          </label>
        </div>
        <label className="block space-y-1">
          <span className="block font-semibold">{t("rootCause")}</span>
          <textarea value={form.rootCause} rows={3} maxLength={4000} dir="auto" className={inputClass} onChange={(e) => setForm({ ...form, rootCause: e.target.value })} />
        </label>
        <div className="flex flex-wrap gap-2">
          <button type="submit" className={buttonClass} disabled={busy || !form.title.trim()}>{t("save")}</button>
          <button type="button" className={secondaryButtonClass} onClick={() => setEditing(false)}>{t("cancel")}</button>
        </div>
        <ProblemStatusLine status={status} />
      </form>
    );
  }

  const row = (label: string, value: React.ReactNode) => (
    <div className="flex flex-wrap gap-x-2">
      <dt className="font-semibold">{label}:</dt>
      <dd>{value}</dd>
    </div>
  );
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="problem-facts">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("facts")}</h2>
      <dl className="grid gap-2 sm:grid-cols-2">
        {row(t("stage"), <ProblemStageBadge stage={problem.stage} />)}
        {row(t("priority"), <span dir="ltr">{`P${problem.priority}`}</span>)}
        {row(t("category"), problem.category ? <Localized text={problem.category.path} locale={locale} /> : t("none"))}
        {row(t("group"), problem.group ? <Localized text={problem.group.name} locale={locale} /> : t("none"))}
        {row(t("assignee"), problem.assignee?.displayName ?? t("nobody"))}
        {row(t("raisedBy"), problem.createdBy?.displayName ?? "—")}
        {row(t("created"), formatDateTime(new Date(problem.createdAt), display))}
        {problem.resolvedAt && row(t("resolved"), formatDateTime(new Date(problem.resolvedAt), display))}
      </dl>
      {problem.state === "OnHold" && reasonName && <p className="font-semibold">{t("onHold", { reason: reasonName })}</p>}
      {problem.description && <p className="whitespace-pre-wrap" dir="auto">{problem.description}</p>}
      {problem.rootCause && (
        <div>
          <h3 className="font-semibold">{t("rootCause")}</h3>
          <p className="whitespace-pre-wrap" dir="auto">{problem.rootCause}</p>
        </div>
      )}
      {problem.resolutionNotes && (
        <div>
          <h3 className="font-semibold">{t("resolution")}</h3>
          <p className="whitespace-pre-wrap" dir="auto">{problem.resolutionNotes}</p>
        </div>
      )}
      {editable && <button type="button" className={secondaryButtonClass} onClick={() => setEditing(true)}>{t("edit")}</button>}
      <ProblemStatusLine status={status} />
    </section>
  );
}
