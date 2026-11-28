"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { buttonClass, inputClass } from "@/components/incidents/ReportIncidentForm";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import { changeKinds, fromLocalInput, type ChangeKind, type ChangeTemplate } from "@/lib/changes";
import { selectableNodes, type TreeNode } from "@/lib/directory";
import { inLanguage, type Locale } from "@/lib/incidents";

/**
 * Raises a change (task H3): a standard change picked from the catalog of standard changes, whose
 * plans, category, group and tasks it takes, or a normal or emergency change planned here. The
 * affected configuration items, the risk questionnaire and tasks follow on the change's page.
 */
export function NewChangeForm({ templates, categories, csrfToken, problemNumber }: {
  templates: ChangeTemplate[];
  categories: TreeNode[];
  csrfToken: string;
  /** Task H3: a change raised from a problem it fixes. */
  problemNumber?: string | null;
}) {
  const t = useTranslations("changes.new");
  const tKinds = useTranslations("changes.kinds");
  const locale = useLocale() as Locale;
  const router = useRouter();
  const id = useId();
  const [kind, setKind] = useState<ChangeKind>(templates.length > 0 ? "Standard" : "Normal");
  const [templateId, setTemplateId] = useState("");
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [implementation, setImplementation] = useState("");
  const [test, setTest] = useState("");
  const [backout, setBackout] = useState("");
  const [state, setState] = useState<"idle" | "sending" | "failed">("idle");
  const [error, setError] = useState<string | null>(null);
  const standard = kind === "Standard";
  const template = templates.find((x) => x.id === templateId);
  const ready = standard ? Boolean(templateId) : Boolean(title.trim());

  return (
    <form className="max-w-3xl space-y-4" aria-labelledby={`${id}-heading`} onSubmit={async (e) => {
      e.preventDefault();
      setState("sending");
      setError(null);
      try {
        const created = await postJson<{ id: string }>("/v1/changes", {
          kind,
          templateId: standard ? templateId : undefined,
          title: title || undefined,
          description: description || undefined,
          categoryId: categoryId || undefined,
          plannedStart: fromLocalInput(start) ?? undefined,
          plannedEnd: fromLocalInput(end) ?? undefined,
          implementationPlan: implementation || undefined,
          testPlan: test || undefined,
          backoutPlan: backout || undefined,
        }, csrfToken);
        if (problemNumber) {
          await postJson(`/v1/changes/${created.id}/links`, { number: problemNumber }, csrfToken).catch(() => undefined);
        }
        router.push(`/changes/${created.id}`);
      } catch (err) {
        setState("failed");
        setError(err instanceof ApiError ? err.message : null);
      }
    }}>
      <h2 id={`${id}-heading`} className="sr-only">{t("title")}</h2>
      {problemNumber && <p role="status">{t("fixes", { number: problemNumber })}</p>}
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("kind")}</legend>
        {changeKinds.map((k) => (
          <label key={k} className="flex items-start gap-2">
            <input type="radio" name={`${id}-kind`} className="mt-1 size-5 accent-brand" checked={kind === k} onChange={() => setKind(k)}
              disabled={k === "Standard" && templates.length === 0} />
            <span>
              <span className="block font-semibold">{tKinds(k)}</span>
              <span className="block text-sm text-ink-muted">{t(`kindHints.${k}`)}</span>
            </span>
          </label>
        ))}
      </fieldset>
      {standard && (
        <label className="block space-y-1">
          <span className="block font-semibold">{t("template")}</span>
          <select value={templateId} onChange={(e) => setTemplateId(e.target.value)} required className={inputClass}>
            <option value="">{t("chooseTemplate")}</option>
            {templates.map((x) => <option key={x.id} value={x.id}>{inLanguage(x.name, locale).value}</option>)}
          </select>
          {template && <span className="block text-sm text-ink-muted" dir="auto">{inLanguage(template.description, locale).value}</span>}
        </label>
      )}
      <label className="block space-y-1">
        <span className="block font-semibold">{standard ? t("titleOptional") : t("titleField")}</span>
        <input value={title} onChange={(e) => setTitle(e.target.value)} required={!standard} maxLength={300} dir="auto" className={inputClass} />
      </label>
      <label className="block space-y-1">
        <span className="block font-semibold">{t("description")}</span>
        <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} maxLength={20000} dir="auto" className={inputClass} />
      </label>
      {!standard && (
        <label className="block space-y-1">
          <span className="block font-semibold">{t("category")}</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("noCategory")}</option>
            {selectableNodes(categories).map((n) => <option key={n.id} value={n.id}>{inLanguage(n.path, locale).value}</option>)}
          </select>
        </label>
      )}
      <fieldset className="grid gap-3 sm:grid-cols-2">
        <legend className="mb-1 font-semibold">{t("window")}</legend>
        <label className="block space-y-1 text-sm">
          <span className="block">{t("start")}</span>
          <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} className={inputClass} dir="ltr" />
        </label>
        <label className="block space-y-1 text-sm">
          <span className="block">{t("end")}</span>
          <input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} className={inputClass} dir="ltr" />
        </label>
      </fieldset>
      {!standard && (
        <>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("implementationPlan")}</span>
            <textarea value={implementation} onChange={(e) => setImplementation(e.target.value)} rows={4} maxLength={20000} dir="auto" className={inputClass} />
          </label>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("testPlan")}</span>
            <textarea value={test} onChange={(e) => setTest(e.target.value)} rows={3} maxLength={20000} dir="auto" className={inputClass} />
          </label>
          <label className="block space-y-1">
            <span className="block font-semibold">{t("backoutPlan")}</span>
            <textarea value={backout} onChange={(e) => setBackout(e.target.value)} rows={3} maxLength={20000} dir="auto" className={inputClass} />
          </label>
        </>
      )}
      <button type="submit" className={buttonClass} disabled={state === "sending" || !ready}>{state === "sending" ? t("sending") : t("submit")}</button>
      {state === "failed" && <p role="alert">{error ?? t("failed")}</p>}
    </form>
  );
}
