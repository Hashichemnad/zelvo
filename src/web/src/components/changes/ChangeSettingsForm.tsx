"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { CiLookup } from "@/components/cmdb/CiLookup";
import { BilingualInputs, Localized, smallButton } from "@/components/directory/common";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { ApprovalLevelsEditor, type WorkflowChoices } from "@/components/workflows/WorkflowEditors";
import { deleteRequest, postJson, putJson } from "@/lib/client-api";
import { fromLocalInput, type Blackout, type ChangeApprovalPaths, type ChangeSettings, type ChangeTemplate, type RiskQuestion } from "@/lib/changes";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import { inLanguage, type Locale } from "@/lib/incidents";
import { changeApproverKinds } from "@/lib/workflows";
import { ChangeStatusLine, useSaver } from "./common";

const paths = ["low", "medium", "high", "emergency"] as const;

function keyOf(text: string, taken: string[]): string {
  const base = text.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 30) || "item";
  let key = base;
  for (let i = 2; taken.includes(key); i++) key = `${base}-${i}`;
  return key;
}

/**
 * The risk and impact questionnaire (bilingual questions, answers with weights), the scores at
 * which risk becomes medium and high, and the approval path of each risk level and of emergency
 * changes (decision 7 of 2026-11-26) with C3's approval levels editor.
 */
export function ChangeSettingsForm({ initial, choices, csrfToken }: { initial: ChangeSettings; choices: WorkflowChoices; csrfToken: string }) {
  const t = useTranslations("changes.settings");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [questions, setQuestions] = useState<RiskQuestion[]>(initial.questions);
  const [mediumFrom, setMediumFrom] = useState(initial.mediumFrom);
  const [highFrom, setHighFrom] = useState(initial.highFrom);
  const [approvalPaths, setApprovalPaths] = useState<ChangeApprovalPaths>(initial.paths);
  const setQuestion = (i: number, patch: Partial<RiskQuestion>) => setQuestions((qs) => qs.map((q, j) => (j === i ? { ...q, ...patch } : q)));
  const maxScore = questions.filter((q) => q.active).reduce((sum, q) => sum + Math.max(0, ...q.options.map((o) => o.weight)), 0);

  return (
    <form className="space-y-6" aria-labelledby={`${id}-heading`} onSubmit={(e) => {
      e.preventDefault();
      void run(async () => {
        const saved = await putJson<ChangeSettings>("/v1/settings/changes", { questions, mediumFrom, highFrom, paths: approvalPaths }, csrfToken);
        setQuestions(saved.questions);
        setApprovalPaths(saved.paths);
      });
    }}>
      <section className="space-y-4 rounded-lg border border-line p-4">
        <h3 id={`${id}-heading`} className="text-lg font-semibold">{t("questionnaire")}</h3>
        <p className="text-sm text-ink-muted">{t("questionnaireHint", { max: maxScore })}</p>
        <ol className="space-y-4">
          {questions.map((q, i) => (
            <li key={q.key} className="space-y-3 rounded-md border border-line p-3" data-testid="risk-question">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h4 className="font-semibold">{t("questionNumber", { number: i + 1 })}</h4>
                <div className="flex flex-wrap gap-2">
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-5 accent-brand" checked={q.active} onChange={(e) => setQuestion(i, { active: e.target.checked })} />
                    {t("active")}
                  </label>
                  <button type="button" className={smallButton} onClick={() => setQuestions((qs) => qs.filter((_, j) => j !== i))}
                    aria-label={t("removeQuestion", { number: i + 1 })}>{t("remove")}</button>
                </div>
              </div>
              <BilingualInputs id={`${id}-q${i}`} label={t("question")} en={q.text.en ?? ""} ar={q.text.ar ?? ""} maxLength={300}
                onChange={(v) => setQuestion(i, { text: { en: v.en || null, ar: v.ar || null } })} />
              <fieldset className="space-y-2">
                <legend className="text-sm font-semibold">{t("answers")}</legend>
                {q.options.map((o, j) => (
                  <div key={o.key} className="flex flex-wrap items-end gap-2">
                    <div className="min-w-0 flex-1">
                      <BilingualInputs id={`${id}-q${i}-o${j}`} label={t("answerNumber", { number: j + 1 })} en={o.text.en ?? ""} ar={o.text.ar ?? ""} maxLength={300}
                        onChange={(v) => setQuestion(i, { options: q.options.map((x, k) => (k === j ? { ...x, text: { en: v.en || null, ar: v.ar || null } } : x)) })} />
                    </div>
                    <label className="space-y-1 text-sm">
                      <span className="block">{t("weight")}</span>
                      <input type="number" min={0} max={100} value={o.weight} className={`${inputClass} w-24`} dir="ltr"
                        onChange={(e) => setQuestion(i, { options: q.options.map((x, k) => (k === j ? { ...x, weight: Number(e.target.value) } : x)) })} />
                    </label>
                    <button type="button" className={smallButton} disabled={q.options.length <= 2}
                      onClick={() => setQuestion(i, { options: q.options.filter((_, k) => k !== j) })}
                      aria-label={t("removeAnswer", { number: j + 1 })}>{t("remove")}</button>
                  </div>
                ))}
                <button type="button" className={smallButton} disabled={q.options.length >= 6}
                  onClick={() => setQuestion(i, { options: [...q.options, { key: keyOf(`answer-${q.options.length + 1}`, q.options.map((x) => x.key)), text: { en: null, ar: null }, weight: 0 }] })}>
                  {t("addAnswer")}</button>
              </fieldset>
            </li>
          ))}
        </ol>
        <button type="button" className={secondaryButtonClass} disabled={questions.length >= 20}
          onClick={() => setQuestions((qs) => [...qs, {
            key: keyOf(`question-${qs.length + 1}`, qs.map((x) => x.key)), text: { en: null, ar: null }, active: true,
            options: [{ key: "low", text: { en: null, ar: null }, weight: 0 }, { key: "high", text: { en: null, ar: null }, weight: 2 }],
          }])}>{t("addQuestion")}</button>
        <div className="flex flex-wrap gap-4">
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("mediumFrom")}</span>
            <input type="number" min={1} value={mediumFrom} onChange={(e) => setMediumFrom(Number(e.target.value))} className={`${inputClass} w-28`} dir="ltr" />
          </label>
          <label className="space-y-1 text-sm">
            <span className="block font-semibold">{t("highFrom")}</span>
            <input type="number" min={2} value={highFrom} onChange={(e) => setHighFrom(Number(e.target.value))} className={`${inputClass} w-28`} dir="ltr" />
          </label>
        </div>
      </section>
      <section className="space-y-4 rounded-lg border border-line p-4">
        <h3 className="text-lg font-semibold">{t("paths")}</h3>
        <p className="text-sm text-ink-muted">{t("pathsHint")}</p>
        {paths.map((p) => (
          <ApprovalLevelsEditor key={p} legend={t(`path.${p}`)} hint={t(`pathHint.${p}`)} levels={approvalPaths[p]} choices={choices} kinds={changeApproverKinds} conditions={false}
            onChange={(levels) => setApprovalPaths((current) => ({ ...current, [p]: levels }))} />
        ))}
      </section>
      <button type="submit" className={buttonClass} disabled={busy}>{t("save")}</button>
      <ChangeStatusLine status={status} />
    </form>
  );
}

/** Blackout periods for the whole organisation or one configuration item (a business service also covers what it runs on). */
export function BlackoutsPanel({ initial, csrfToken }: { initial: Blackout[]; csrfToken: string }) {
  const t = useTranslations("changes.settings.blackouts");
  const locale = useLocale() as Locale;
  const id = useId();
  const { status, run, busy } = useSaver();
  const [blackouts, setBlackouts] = useState(initial);
  const [en, setEn] = useState("");
  const [ar, setAr] = useState("");
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [item, setItem] = useState<{ id: string; name: string } | null>(null);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="blackouts">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h3>
      <p className="text-sm text-ink-muted">{t("hint")}</p>
      {blackouts.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2">
          {blackouts.map((b) => (
            <li key={b.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md bg-surface-muted p-2">
              <span>
                <span className="font-semibold"><Localized text={b.name} locale={locale} /></span>{" "}
                <span className="text-sm" dir="ltr">{new Date(b.startsAt).toLocaleString(locale === "ar" ? "ar-AE" : "en-GB")} – {new Date(b.endsAt).toLocaleString(locale === "ar" ? "ar-AE" : "en-GB")}</span>{" "}
                <span className="text-sm text-ink-muted">{b.item ? t("item", { name: b.item.name }) : t("all")}</span>
              </span>
              <button type="button" className={smallButton} disabled={busy} aria-label={t("remove", { name: inLanguage(b.name, locale).value })}
                onClick={() => void run(async () => {
                  await deleteRequest(`/v1/change-blackouts/${b.id}`, csrfToken);
                  setBlackouts((current) => current.filter((x) => x.id !== b.id));
                })}>{t("removeShort")}</button>
            </li>
          ))}
        </ul>
      )}
      <form className="space-y-3" onSubmit={(e) => {
        e.preventDefault();
        void run(async () => {
          const created = await postJson<Blackout>("/v1/change-blackouts",
            { name: { en: en || null, ar: ar || null }, startsAt: fromLocalInput(start), endsAt: fromLocalInput(end), configurationItemId: item?.id ?? null }, csrfToken);
          setBlackouts((current) => [...current, created].sort((a, b) => a.startsAt.localeCompare(b.startsAt)));
          setEn(""); setAr(""); setStart(""); setEnd(""); setItem(null);
        });
      }}>
        <fieldset className="space-y-3">
          <legend className="font-semibold">{t("add")}</legend>
          <BilingualInputs id={`${id}-name`} label={t("name")} en={en} ar={ar} maxLength={200} onChange={(v) => (setEn(v.en), setAr(v.ar))} />
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="space-y-1 text-sm"><span className="block">{t("start")}</span>
              <input type="datetime-local" required value={start} onChange={(e) => setStart(e.target.value)} className={inputClass} dir="ltr" /></label>
            <label className="space-y-1 text-sm"><span className="block">{t("end")}</span>
              <input type="datetime-local" required value={end} onChange={(e) => setEnd(e.target.value)} className={inputClass} dir="ltr" /></label>
          </div>
          {item ? (
            <p>{t("item", { name: item.name })} <button type="button" className="font-semibold text-brand underline" onClick={() => setItem(null)}>{t("allInstead")}</button></p>
          ) : (
            <CiLookup id={`${id}-item`} label={t("itemLabel")} onPick={(picked) => setItem({ id: picked.id, name: picked.name })} />
          )}
          <button type="submit" className={buttonClass} disabled={busy || (!en.trim() && !ar.trim()) || !start || !end}>{t("addButton")}</button>
        </fieldset>
      </form>
      <ChangeStatusLine status={status} />
    </section>
  );
}

interface TemplateDraft {
  id: string | null;
  nameEn: string;
  nameAr: string;
  descriptionEn: string;
  descriptionAr: string;
  categoryId: string;
  groupId: string;
  implementationPlan: string;
  testPlan: string;
  backoutPlan: string;
  tasks: { en: string; ar: string; groupId: string; parallel: boolean }[];
  active: boolean;
  etag: string | null;
}

function draftOf(t: ChangeTemplate | null): TemplateDraft {
  return {
    id: t?.id ?? null,
    nameEn: t?.name.en ?? "", nameAr: t?.name.ar ?? "", descriptionEn: t?.description.en ?? "", descriptionAr: t?.description.ar ?? "",
    categoryId: t?.category?.id ?? "", groupId: t?.group?.id ?? "",
    implementationPlan: t?.implementationPlan ?? "", testPlan: t?.testPlan ?? "", backoutPlan: t?.backoutPlan ?? "",
    tasks: t?.tasks.map((k) => ({ en: k.name.en ?? "", ar: k.name.ar ?? "", groupId: k.group?.id ?? "", parallel: k.parallelWithPrevious })) ?? [],
    active: t?.active ?? true,
    etag: t?.etag ?? null,
  };
}

/** The catalog of standard changes: pre-approved changes with their plans and tasks. */
export function ChangeTemplatesPanel({ initial, groups, categories, csrfToken }: {
  initial: ChangeTemplate[];
  groups: GroupSummary[];
  categories: TreeNode[];
  csrfToken: string;
}) {
  const t = useTranslations("changes.settings.templates");
  const locale = useLocale() as Locale;
  const id = useId();
  const { status, run, busy } = useSaver();
  const [templates, setTemplates] = useState(initial);
  const [draft, setDraft] = useState<TemplateDraft | null>(null);
  const set = (patch: Partial<TemplateDraft>) => setDraft((d) => (d ? { ...d, ...patch } : d));

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="change-templates">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h3>
      <p className="text-sm text-ink-muted">{t("hint")}</p>
      {templates.length === 0 ? <p>{t("none")}</p> : (
        <ul className="space-y-2">
          {templates.map((x) => (
            <li key={x.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md bg-surface-muted p-2">
              <span>
                <span className="font-semibold"><Localized text={x.name} locale={locale} /></span>{" "}
                <span className="text-sm text-ink-muted">{t("summary", { tasks: x.tasks.length, changes: x.changeCount })}{x.active ? "" : ` · ${t("inactive")}`}</span>
              </span>
              <button type="button" className={smallButton} onClick={() => setDraft(draftOf(x))}
                aria-label={t("edit", { name: inLanguage(x.name, locale).value })}>{t("editShort")}</button>
            </li>
          ))}
        </ul>
      )}
      {!draft ? (
        <button type="button" className={secondaryButtonClass} onClick={() => setDraft(draftOf(null))}>{t("add")}</button>
      ) : (
        <form className="space-y-3 rounded-md border border-line p-3" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const body = {
              name: { en: draft.nameEn || null, ar: draft.nameAr || null },
              description: { en: draft.descriptionEn || null, ar: draft.descriptionAr || null },
              categoryId: draft.categoryId || null, groupId: draft.groupId || null,
              implementationPlan: draft.implementationPlan, testPlan: draft.testPlan, backoutPlan: draft.backoutPlan,
              tasks: draft.tasks.map((k, i) => ({ name: { en: k.en || null, ar: k.ar || null }, groupId: k.groupId || null, parallelWithPrevious: i > 0 && k.parallel })),
              active: draft.active,
            };
            const saved = draft.id
              ? await putJson<ChangeTemplate>(`/v1/change-templates/${draft.id}`, body, csrfToken, draft.etag ? { "If-Match": draft.etag } : {})
              : await postJson<ChangeTemplate>("/v1/change-templates", body, csrfToken);
            setTemplates((current) => [...current.filter((x) => x.id !== saved.id), saved]);
            setDraft(null);
          });
        }}>
          <BilingualInputs id={`${id}-name`} label={t("name")} en={draft.nameEn} ar={draft.nameAr} maxLength={200} onChange={(v) => set({ nameEn: v.en, nameAr: v.ar })} />
          <BilingualInputs id={`${id}-description`} label={t("description")} en={draft.descriptionEn} ar={draft.descriptionAr} maxLength={4000}
            onChange={(v) => set({ descriptionEn: v.en, descriptionAr: v.ar })} />
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="space-y-1 text-sm"><span className="block">{t("category")}</span>
              <select value={draft.categoryId} onChange={(e) => set({ categoryId: e.target.value })} className={inputClass}>
                <option value="">{t("noCategory")}</option>
                {selectableNodes(categories).map((n) => <option key={n.id} value={n.id}>{inLanguage(n.path, locale).value}</option>)}
              </select></label>
            <label className="space-y-1 text-sm"><span className="block">{t("group")}</span>
              <select value={draft.groupId} onChange={(e) => set({ groupId: e.target.value })} className={inputClass}>
                <option value="">{t("noGroup")}</option>
                {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
              </select></label>
          </div>
          {(["implementationPlan", "testPlan", "backoutPlan"] as const).map((key) => (
            <label key={key} className="block space-y-1"><span className="block font-semibold">{t(key)}</span>
              <textarea value={draft[key]} onChange={(e) => set({ [key]: e.target.value })} rows={3} maxLength={20000} dir="auto" className={inputClass} /></label>
          ))}
          <fieldset className="space-y-2">
            <legend className="font-semibold">{t("tasks")}</legend>
            {draft.tasks.map((k, i) => (
              <div key={i} className="flex flex-wrap items-end gap-2">
                <label className="space-y-1 text-sm"><span className="block">{t("taskEn", { number: i + 1 })}</span>
                  <input value={k.en} onChange={(e) => set({ tasks: draft.tasks.map((x, j) => (j === i ? { ...x, en: e.target.value } : x)) })} maxLength={150} lang="en" dir="ltr" className={inputClass} /></label>
                <label className="space-y-1 text-sm"><span className="block">{t("taskAr", { number: i + 1 })}</span>
                  <input value={k.ar} onChange={(e) => set({ tasks: draft.tasks.map((x, j) => (j === i ? { ...x, ar: e.target.value } : x)) })} maxLength={150} lang="ar" dir="rtl" className={inputClass} /></label>
                <label className="space-y-1 text-sm"><span className="block">{t("taskGroup", { number: i + 1 })}</span>
                  <select value={k.groupId} onChange={(e) => set({ tasks: draft.tasks.map((x, j) => (j === i ? { ...x, groupId: e.target.value } : x)) })} className={inputClass}>
                    <option value="">{t("changeGroup")}</option>
                    {groups.filter((g) => g.active).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
                  </select></label>
                {i > 0 && (
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={k.parallel} className="size-5 accent-brand" onChange={(e) => set({ tasks: draft.tasks.map((x, j) => (j === i ? { ...x, parallel: e.target.checked } : x)) })} />
                    {t("parallel")}
                  </label>
                )}
                <button type="button" className={smallButton} onClick={() => set({ tasks: draft.tasks.filter((_, j) => j !== i) })}
                  aria-label={t("removeTask", { number: i + 1 })}>{t("remove")}</button>
              </div>
            ))}
            <button type="button" className={smallButton} disabled={draft.tasks.length >= 30}
              onClick={() => set({ tasks: [...draft.tasks, { en: "", ar: "", groupId: "", parallel: false }] })}>{t("addTask")}</button>
          </fieldset>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={draft.active} onChange={(e) => set({ active: e.target.checked })} className="size-5 accent-brand" />
            {t("active")}
          </label>
          <div className="flex flex-wrap gap-2">
            <button type="submit" className={buttonClass} disabled={busy || (!draft.nameEn.trim() && !draft.nameAr.trim())}>{t("save")}</button>
            <button type="button" className="px-2 py-2 font-semibold text-brand underline" onClick={() => setDraft(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}
      <ChangeStatusLine status={status} />
    </section>
  );
}
