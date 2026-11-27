"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { selectableNodes, type TreeNode } from "@/lib/directory";
import { inLanguage, type Locale } from "@/lib/incidents";

/** Raises a problem by hand or from the incidents selected in the workspace or on an incident page. */
export function NewProblemForm({ incidentIds, incidentNumbers, categories, csrfToken }: {
  incidentIds: string[];
  incidentNumbers: string[];
  categories: TreeNode[];
  csrfToken: string;
}) {
  const t = useTranslations("problems.new");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [priority, setPriority] = useState("");
  const [state, setState] = useState<"idle" | "sending" | "failed">("idle");

  return (
    <form className="max-w-3xl space-y-4" aria-labelledby={`${id}-heading`} onSubmit={async (e) => {
      e.preventDefault();
      setState("sending");
      try {
        const created = await postJson<{ id: string }>("/v1/problems", {
          title, description: description || undefined, categoryId: categoryId || undefined, priority: priority ? Number(priority) : undefined, incidentIds,
        }, csrfToken);
        router.push(`/problems/${created.id}`);
      } catch (error) {
        setState(error instanceof ApiError ? "failed" : "failed");
      }
    }}>
      <h2 id={`${id}-heading`} className="sr-only">{t("title")}</h2>
      {incidentIds.length > 0 && (
        <p role="status">
          {t("linked", { count: incidentIds.length })}{" "}
          <span dir="ltr" className="font-mono">{incidentNumbers.join(", ")}</span>
        </p>
      )}
      <label className="block space-y-1">
        <span className="block font-semibold">{t("titleField")}</span>
        <input value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={300} dir="auto" className={inputClass} />
      </label>
      <label className="block space-y-1">
        <span className="block font-semibold">{t("description")}</span>
        <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={5} maxLength={20000} dir="auto" className={inputClass} />
      </label>
      <div className="grid gap-3 sm:grid-cols-2">
        <label className="block space-y-1">
          <span className="block font-semibold">{t("category")}</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("noCategory")}</option>
            {selectableNodes(categories).map((n) => <option key={n.id} value={n.id}>{inLanguage(n.path, locale).value}</option>)}
          </select>
        </label>
        <label className="block space-y-1">
          <span className="block font-semibold">{t("priority")}</span>
          <select value={priority} onChange={(e) => setPriority(e.target.value)} className={inputClass}>
            <option value="">{t("fromIncidents")}</option>
            {[1, 2, 3, 4, 5].map((p) => <option key={p} value={p}>{`P${p}`}</option>)}
          </select>
        </label>
      </div>
      <button type="submit" className={buttonClass} disabled={state === "sending" || !title.trim()}>{state === "sending" ? t("sending") : t("submit")}</button>
      {state === "failed" && <p role="alert">{t("failed")}</p>}
    </form>
  );
}
