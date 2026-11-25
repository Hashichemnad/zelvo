"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { SaveStatusLine, useSaver } from "@/components/directory/common";
import { fieldInput, primaryAction } from "@/components/forms/ui";
import { postJson } from "@/lib/client-api";
import { type DigitSystem } from "@/lib/format";
import { formatFigure, nameIn, type AppliedTemplate, type DepartmentTemplate, type TemplateTextValue } from "@/lib/reports";

type Pair = { en: string; ar: string };
const pair = (v: TemplateTextValue): Pair => ({ en: v.en ?? "", ar: v.ar ?? "" });
const input = (v: Pair) => ({ en: v.en.trim() || null, ar: v.ar.trim() || null });

function BilingualFields({ legend, value, onChange, idPrefix }: { legend: string; value: Pair; onChange: (v: Pair) => void; idPrefix: string }) {
  const t = useTranslations("setup.templates");
  return (
    <fieldset className="grid gap-2 sm:grid-cols-2">
      <legend className="text-sm font-semibold">{legend}</legend>
      <div className="space-y-1">
        <label htmlFor={`${idPrefix}-en`} className="block text-sm">{t("english")}</label>
        <input id={`${idPrefix}-en`} lang="en" dir="ltr" maxLength={150} value={value.en} onChange={(e) => onChange({ ...value, en: e.target.value })} className={fieldInput} />
      </div>
      <div className="space-y-1">
        <label htmlFor={`${idPrefix}-ar`} lang="ar" className="block text-sm">{t("arabic")}</label>
        <input id={`${idPrefix}-ar`} lang="ar" dir="rtl" maxLength={150} value={value.ar} onChange={(e) => onChange({ ...value, ar: e.target.value })} className={fieldInput} />
      </div>
    </fieldset>
  );
}

/**
 * Department templates in the setup wizard (task F2): the four templates Zelvo ships, each with a
 * full preview of what it creates; the department and group names can be changed in both
 * languages before applying. Applying never changes what exists.
 */
export function TemplatePicker({ templates, csrfToken, digits, onApplied }: {
  templates: DepartmentTemplate[];
  csrfToken: string;
  digits: DigitSystem;
  onApplied: (result: AppliedTemplate) => void;
}) {
  const t = useTranslations("setup.templates");
  const tTypes = useTranslations("reports.types");
  const locale = useLocale();
  const id = useId();
  const display = { locale, calendar: "gregory" as const, digits };
  const n = (v: number) => formatFigure(v, display);
  const hours = (minutes: number) => t("hours", { value: n(Math.round((minutes / 60) * 10) / 10) });
  const [key, setKey] = useState<string | null>(null);
  const template = templates.find((x) => x.key === key) ?? null;
  const [department, setDepartment] = useState<Pair>({ en: "", ar: "" });
  const [groups, setGroups] = useState<Record<string, Pair>>({});
  const [includeServices, setIncludeServices] = useState(true);
  const [includeResponses, setIncludeResponses] = useState(true);
  const [result, setResult] = useState<AppliedTemplate | null>(null);
  const { status, run, busy } = useSaver();

  function choose(next: DepartmentTemplate) {
    setKey(next.key);
    setDepartment(pair(next.name));
    setGroups(Object.fromEntries(next.groups.map((g) => [g.key, pair(g.name)])));
    setResult(null);
  }

  async function apply() {
    if (!template) return;
    const applied = await run(() => postJson<AppliedTemplate>(`/v1/setup/templates/${template.key}/apply`, {
      departmentName: input(department),
      groupNames: Object.fromEntries(Object.entries(groups).map(([k, v]) => [k, input(v)])),
      includeServices, includeResponses,
    }, csrfToken));
    if (applied) {
      setResult(applied);
      onApplied(applied);
    }
  }

  const groupName = (k: string) => {
    const g = groups[k];
    return g ? (locale === "ar" ? g.ar || g.en : g.en || g.ar) : k;
  };

  return (
    <div className="space-y-4" data-testid="template-picker">
      <fieldset className="space-y-2">
        <legend className="font-semibold">{t("choose")}</legend>
        <div className="grid gap-3 sm:grid-cols-2">
          {templates.map((x) => (
            <div key={x.key} className={`rounded-lg border-2 p-3 ${key === x.key ? "border-brand" : "border-line"}`}>
              <label className="flex cursor-pointer items-center gap-2 font-semibold">
                <input type="radio" name={`${id}-template`} value={x.key} checked={key === x.key} onChange={() => choose(x)} aria-describedby={`${id}-${x.key}-description`} />
                {nameIn(x.name, locale)}
              </label>
              <p id={`${id}-${x.key}-description`} className="mt-1 text-sm text-ink-muted">{nameIn(x.description, locale)}</p>
            </div>
          ))}
        </div>
      </fieldset>

      {template && (
        <section aria-labelledby={`${id}-preview`} className="space-y-4 rounded-lg border border-line bg-surface p-4" data-testid="template-preview">
          <h4 id={`${id}-preview`} className="text-lg font-semibold">{t("preview", { name: nameIn(template.name, locale) })}</h4>
          <p className="text-sm">{t("previewIntro")}</p>
          <BilingualFields legend={t("departmentName")} value={department} onChange={setDepartment} idPrefix={`${id}-dept`} />

          <div className="space-y-2">
            <h5 className="font-semibold">{t("groups")}</h5>
            {template.groups.map((g) => (
              <BilingualFields key={g.key} legend={t("groupName", { name: nameIn(g.name, locale) })} value={groups[g.key] ?? pair(g.name)}
                onChange={(v) => setGroups((current) => ({ ...current, [g.key]: v }))} idPrefix={`${id}-group-${g.key}`} />
            ))}
          </div>

          <div className="space-y-1">
            <h5 className="font-semibold">{t("categories")}</h5>
            <ul className="list-disc space-y-1 ps-5 text-sm">
              {template.categories.map((c, i) => (
                <li key={i}>
                  {nameIn(c.name, locale)} → <bdi>{groupName(c.group)}</bdi>
                  {c.children.length > 0 && <span className="text-ink-muted"> ({c.children.map((ch) => nameIn(ch, locale)).join(locale === "ar" ? "، " : ", ")})</span>}
                </li>
              ))}
            </ul>
          </div>

          <div className="space-y-2">
            <h5 className="font-semibold">{t("policies")}</h5>
            <p className="text-sm text-ink-muted">{t("policiesIntro")}</p>
            {template.policies.map((p) => (
              <div key={p.recordType} className="overflow-x-auto" tabIndex={0}>
                <table className="w-full border-collapse text-sm">
                  <caption className="text-start font-semibold">{tTypes(p.recordType)}</caption>
                  <thead><tr>
                    <th scope="col" className="border-b border-line px-2 py-1 text-start">{t("priority")}</th>
                    <th scope="col" className="border-b border-line px-2 py-1 text-end">{t("response")}</th>
                    <th scope="col" className="border-b border-line px-2 py-1 text-end">{t("resolution")}</th>
                  </tr></thead>
                  <tbody>
                    {p.targets.map((target) => (
                      <tr key={target.priority}>
                        <th scope="row" className="border-b border-line px-2 py-1 text-start font-normal">{t("priorityValue", { value: n(target.priority) })}</th>
                        <td className="border-b border-line px-2 py-1 text-end tabular-nums">{hours(target.responseMinutes)}</td>
                        <td className="border-b border-line px-2 py-1 text-end tabular-nums">{hours(target.resolutionMinutes)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
          </div>

          <div className="space-y-1">
            <label className="inline-flex items-center gap-2 font-semibold">
              <input type="checkbox" checked={includeServices} onChange={(e) => setIncludeServices(e.target.checked)} />
              {t("includeServices")}
            </label>
            <ul className="list-disc ps-5 text-sm">
              {template.services.map((s) => <li key={s.code}>{nameIn(s.name, locale)} (<bdi dir="ltr">{s.code}</bdi>)</li>)}
            </ul>
            <p className="text-sm text-ink-muted">{t("servicesHint")}</p>
          </div>

          <div className="space-y-1">
            <label className="inline-flex items-center gap-2 font-semibold">
              <input type="checkbox" checked={includeResponses} onChange={(e) => setIncludeResponses(e.target.checked)} />
              {t("includeResponses")}
            </label>
            <ul className="list-disc ps-5 text-sm">
              {template.responses.map((r, i) => <li key={i}><span className="font-semibold">{nameIn(r.title, locale)}</span>: {nameIn(r.body, locale)}</li>)}
            </ul>
          </div>

          <p className="text-sm text-ink-muted">{t("neverChanges")}</p>
          <button type="button" className={primaryAction} disabled={busy || (!department.en.trim() && !department.ar.trim())} onClick={() => void apply()} data-testid="apply-template">
            {t("apply")}
          </button>
          <SaveStatusLine status={status} />
          {result && (
            <p role="status" className="rounded-md border border-ok p-3 text-sm" data-testid="template-result">
              {t("applied", { groups: n(result.groupIds.length), categories: n(result.categories), policies: n(result.policies), services: n(result.serviceIds.length), responses: n(result.responses) })}
            </p>
          )}
        </section>
      )}
    </div>
  );
}
