"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, deleteRequest, getJson, postJson, putJson } from "@/lib/client-api";
import type { GroupSummary } from "@/lib/directory";
import { inLanguage, type Locale } from "@/lib/incidents";
import { responsePlaceholders, type CannedResponse, type WorkspaceScope } from "@/lib/workspace";
import { inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";

interface Draft {
  id: string | null;
  scope: WorkspaceScope;
  groupId: string;
  titleEn: string;
  titleAr: string;
  bodyEn: string;
  bodyAr: string;
  active: boolean;
}

const empty: Draft = { id: null, scope: "Personal", groupId: "", titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", active: true };

/**
 * Canned responses the person may change, and a form with English and Arabic side by side
 * (LANG-5: a language left empty is reported as missing in the list).
 */
export function CannedResponsesPanel({ initial, groups, permissions, csrfToken }: {
  initial: CannedResponse[];
  groups: GroupSummary[];
  permissions: string[];
  csrfToken: string;
}) {
  const t = useTranslations("workspace.responses");
  const tCommon = useTranslations("common");
  const locale = useLocale() as Locale;
  const id = useId();
  const [responses, setResponses] = useState(initial);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const mayShareAll = permissions.includes("responses.manage");

  async function reload() {
    setResponses(await getJson<CannedResponse[]>("/v1/canned-responses?manage=true"));
  }

  function problem(error: unknown): string {
    if (error instanceof ApiError) {
      if (error.rateLimited) return tCommon("rateLimited");
      const errors = (error.details.errors ?? {}) as Record<string, string[]>;
      if (Object.keys(errors).some((k) => k.startsWith("body"))) return t("errors.placeholders");
      if (errors.scope) return t("errors.scope");
      if (errors.title) return t("errors.title");
      if (errors.groupId) return t("errors.group");
    }
    return t("errors.failed");
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!draft) return;
    const body = {
      scope: draft.scope,
      groupId: draft.scope === "Group" ? draft.groupId || null : null,
      title: { en: draft.titleEn.trim() || null, ar: draft.titleAr.trim() || null },
      body: { en: draft.bodyEn.trim() || null, ar: draft.bodyAr.trim() || null },
      active: draft.active,
    };
    try {
      if (draft.id) await putJson(`/v1/canned-responses/${draft.id}`, body, csrfToken);
      else await postJson("/v1/canned-responses", body, csrfToken);
      setDraft(null);
      setMessage({ ok: true, text: t("saved") });
      await reload();
    } catch (error) {
      setMessage({ ok: false, text: problem(error) });
    }
  }

  async function remove(response: CannedResponse) {
    try {
      await deleteRequest(`/v1/canned-responses/${response.id}`, csrfToken);
      setMessage({ ok: true, text: t("removed") });
      await reload();
    } catch (error) {
      setMessage({ ok: false, text: problem(error) });
    }
  }

  const edit = (r: CannedResponse) => setDraft({
    id: r.id, scope: r.scope, groupId: r.group?.id ?? "", titleEn: r.title.en ?? "", titleAr: r.title.ar ?? "",
    bodyEn: r.body.en ?? "", bodyAr: r.body.ar ?? "", active: r.active,
  });

  return (
    <div className="space-y-4">
      <p className="max-w-2xl">{t("intro")}</p>
      <p className="max-w-2xl text-sm text-ink-muted">
        {t("placeholders")} {responsePlaceholders.map((p) => <code key={p} dir="ltr" className="me-2">{`{{${p}}}`}</code>)}
      </p>
      <button type="button" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface" onClick={() => setDraft({ ...empty })}>
        {t("new")}
      </button>
      <p aria-live="polite" className={message?.ok === false ? "text-bad" : "text-ok"}>{message?.text}</p>

      {draft && (
        <form onSubmit={save} aria-labelledby={`${id}-form`} className="space-y-3 rounded-lg border border-line p-3">
          <h3 id={`${id}-form`} className="text-lg font-semibold">{draft.id ? t("editHeading") : t("newHeading")}</h3>
          <div className="flex flex-wrap gap-3">
            <label className="space-y-1 text-sm">
              <span className="block font-semibold">{t("scope")}</span>
              <select value={draft.scope} onChange={(e) => setDraft({ ...draft, scope: e.target.value as WorkspaceScope })} className={inputClass}>
                <option value="Personal">{t("scopes.Personal")}</option>
                <option value="Group">{t("scopes.Group")}</option>
                {mayShareAll && <option value="Tenant">{t("scopes.Tenant")}</option>}
              </select>
            </label>
            {draft.scope === "Group" && (
              <label className="space-y-1 text-sm">
                <span className="block font-semibold">{t("group")}</span>
                <select value={draft.groupId} onChange={(e) => setDraft({ ...draft, groupId: e.target.value })} required className={inputClass}>
                  <option value="">{t("chooseGroup")}</option>
                  {groups.map((g) => <option key={g.id} value={g.id}>{inLanguage(g.name, locale).value}</option>)}
                </select>
              </label>
            )}
            <label className="flex items-center gap-2 self-end pb-2 text-sm">
              <input type="checkbox" checked={draft.active} onChange={(e) => setDraft({ ...draft, active: e.target.checked })} />
              {t("active")}
            </label>
          </div>
          <div className="grid gap-3 md:grid-cols-2">
            <fieldset className="space-y-2" lang="en" dir="ltr">
              <legend className="font-semibold">{t("english")}</legend>
              <label className="block space-y-1 text-sm">
                <span className="block">{t("title")}</span>
                <input value={draft.titleEn} onChange={(e) => setDraft({ ...draft, titleEn: e.target.value })} maxLength={200} className={`${inputClass} w-full`} />
              </label>
              <label className="block space-y-1 text-sm">
                <span className="block">{t("body")}</span>
                <textarea value={draft.bodyEn} onChange={(e) => setDraft({ ...draft, bodyEn: e.target.value })} rows={6} maxLength={10000} className={`${inputClass} w-full`} />
              </label>
            </fieldset>
            <fieldset className="space-y-2" lang="ar" dir="rtl">
              <legend className="font-semibold">{t("arabic")}</legend>
              <label className="block space-y-1 text-sm">
                <span className="block">{t("title")}</span>
                <input value={draft.titleAr} onChange={(e) => setDraft({ ...draft, titleAr: e.target.value })} maxLength={200} className={`${inputClass} w-full`} />
              </label>
              <label className="block space-y-1 text-sm">
                <span className="block">{t("body")}</span>
                <textarea value={draft.bodyAr} onChange={(e) => setDraft({ ...draft, bodyAr: e.target.value })} rows={6} maxLength={10000} className={`${inputClass} w-full`} />
              </label>
            </fieldset>
          </div>
          <div className="flex gap-2">
            <button type="submit" className="rounded-md bg-brand px-4 py-2 font-semibold text-surface">{t("save")}</button>
            <button type="button" className={secondaryButtonClass} onClick={() => setDraft(null)}>{t("cancel")}</button>
          </div>
        </form>
      )}

      {responses.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ul className="space-y-2" data-testid="canned-list">
          {responses.map((r) => {
            const title = inLanguage(r.title, locale);
            const missing = [...new Set([...(r.title.missing ?? []), ...(r.body.missing ?? [])])];
            return (
              <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-line p-3">
                <div className="space-y-1">
                  <p className="font-semibold" lang={title.lang} dir={title.dir}>{title.value}</p>
                  <p className="text-sm text-ink-muted">
                    {t(`scopes.${r.scope}`)}
                    {r.group && <> · {inLanguage(r.group.name, locale).value}</>}
                    {!r.active && <> · {t("inactive")}</>}
                    {missing.length > 0 && <> · <span className="text-warn">{t("missing", { languages: missing.map((l) => t(l === "ar" ? "arabic" : "english")).join(locale === "ar" ? "، " : ", ") })}</span></>}
                  </p>
                </div>
                {r.canEdit && (
                  <div className="flex gap-2">
                    <button type="button" className={secondaryButtonClass} onClick={() => edit(r)} aria-label={t("editNamed", { name: title.value })}>{t("edit")}</button>
                    <button type="button" className={secondaryButtonClass} onClick={() => void remove(r)} aria-label={t("removeNamed", { name: title.value })}>{t("remove")}</button>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
