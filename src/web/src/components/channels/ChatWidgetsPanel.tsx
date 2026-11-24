"use client";

import { useId, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ApiError, getJson, patchJson, postJson } from "@/lib/client-api";
import { textIn, type ChatWidget } from "@/lib/channels";
import { selectableNodes, textOf, type GroupSummary, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/i18n/routing";
import { codeClass, inputClass, primaryButton, secondaryButton } from "../settings/integrations/ui";

/** The script tag that embeds a widget on another website. */
export function embedCode(appUrl: string, widget: ChatWidget, language: string): string {
  const base = appUrl.replace(/\/+$/, "");
  return `<script src="${base}/chat-widget.js" data-widget="${widget.id}" data-language="${language}" async></script>`;
}

/**
 * Settings → Live chat (module 6, task E2): widgets with their bilingual name and greeting, the
 * group that answers, the category of tickets, whether they show on the portal, and the websites
 * that may embed them, with the code to embed.
 */
export function ChatWidgetsPanel({
  initial,
  csrfToken,
  appUrl,
  categories,
  groups,
}: {
  initial: ChatWidget[];
  csrfToken: string;
  appUrl: string;
  categories: TreeNode[];
  groups: GroupSummary[];
}) {
  const t = useTranslations("channels.widgets");
  const [widgets, setWidgets] = useState(initial);
  const [editing, setEditing] = useState<string | "new" | null>(initial.length === 0 ? "new" : null);

  async function reload() {
    setWidgets(await getJson<ChatWidget[]>("/v1/chat/widgets"));
    setEditing(null);
  }

  return (
    <div className="space-y-6" data-testid="chat-widgets-panel">
      <p className="max-w-3xl">{t("intro")}</p>
      {widgets.map((widget) =>
        editing === widget.id ? (
          <WidgetForm key={widget.id} widget={widget} csrfToken={csrfToken} categories={categories} groups={groups} onSaved={reload} onCancel={() => setEditing(null)} />
        ) : (
          <WidgetCard key={widget.id} widget={widget} appUrl={appUrl} groups={groups} onEdit={() => setEditing(widget.id)} />
        ),
      )}
      {editing === "new" ? (
        <WidgetForm widget={null} csrfToken={csrfToken} categories={categories} groups={groups} onSaved={reload} onCancel={widgets.length > 0 ? () => setEditing(null) : undefined} />
      ) : (
        <button type="button" className={primaryButton} onClick={() => setEditing("new")}>{t("add")}</button>
      )}
    </div>
  );
}

function WidgetCard({ widget, appUrl, groups, onEdit }: { widget: ChatWidget; appUrl: string; groups: GroupSummary[]; onEdit: () => void }) {
  const t = useTranslations("channels.widgets");
  const locale = useLocale() as Locale;
  const id = useId();
  const group = groups.find((g) => g.id === widget.groupId);
  return (
    <section aria-labelledby={`${id}-name`} className="max-w-3xl space-y-2 rounded-md border border-line p-4" data-testid="chat-widget">
      <h3 id={`${id}-name`} className="text-lg font-semibold">{textIn(widget.name, locale)}</h3>
      <p className="text-sm">
        {widget.active ? t("active") : t("inactive")} · {group ? textOf(group.name, locale) : "—"}
        {widget.onPortal && <> · {t("onPortal")}</>}
      </p>
      {widget.allowedOrigins.length > 0 ? (
        <>
          <p className="text-sm">{t("websites")}: <span dir="ltr">{widget.allowedOrigins.join(", ")}</span></p>
          <p className="font-semibold">{t("embed")}</p>
          <code dir="ltr" lang="en" className={codeClass}>{embedCode(appUrl, widget, locale)}</code>
          <p className="text-sm text-ink-muted">{t("embedHelp")}</p>
        </>
      ) : (
        <p className="text-sm text-ink-muted">{t("noWebsites")}</p>
      )}
      <button type="button" className={secondaryButton} onClick={onEdit}>{t("edit")}<span className="sr-only">: {textIn(widget.name, locale)}</span></button>
    </section>
  );
}

function WidgetForm({
  widget,
  csrfToken,
  categories,
  groups,
  onSaved,
  onCancel,
}: {
  widget: ChatWidget | null;
  csrfToken: string;
  categories: TreeNode[];
  groups: GroupSummary[];
  onSaved: () => Promise<void>;
  onCancel?: () => void;
}) {
  const t = useTranslations("channels.widgets");
  const locale = useLocale() as Locale;
  const id = useId();
  const [nameEn, setNameEn] = useState(widget?.name.en ?? "");
  const [nameAr, setNameAr] = useState(widget?.name.ar ?? "");
  const [greetingEn, setGreetingEn] = useState(widget?.greeting.en ?? "");
  const [greetingAr, setGreetingAr] = useState(widget?.greeting.ar ?? "");
  const [groupId, setGroupId] = useState(widget?.groupId ?? groups.find((g) => g.active)?.id ?? "");
  const [categoryId, setCategoryId] = useState(widget?.categoryId ?? "");
  const [origins, setOrigins] = useState((widget?.allowedOrigins ?? []).join("\n"));
  const [onPortal, setOnPortal] = useState(widget?.onPortal ?? true);
  const [active, setActive] = useState(widget?.active ?? true);
  const [error, setError] = useState<string | null>(null);

  async function save(event: FormEvent) {
    event.preventDefault();
    setError(null);
    const body = {
      name: { en: nameEn.trim() || null, ar: nameAr.trim() || null },
      greeting: { en: greetingEn.trim() || null, ar: greetingAr.trim() || null },
      groupId: groupId || null,
      categoryId: categoryId || null,
      clearCategory: !categoryId,
      allowedOrigins: origins.split(/[\s,]+/).map((o) => o.trim()).filter(Boolean),
      onPortal,
      active,
    };
    try {
      if (widget) await patchJson(`/v1/chat/widgets/${widget.id}`, body, csrfToken);
      else await postJson("/v1/chat/widgets", body, csrfToken);
      await onSaved();
    } catch (refused) {
      const fields = refused instanceof ApiError ? Object.keys((refused.details.errors as Record<string, unknown>) ?? {}) : [];
      setError(fields.includes("allowedOrigins") ? t("originsInvalid") : fields.length > 0 ? t("invalid") : t("failed"));
    }
  }

  const input = (key: string, label: string, value: string, set: (v: string) => void, dir: "ltr" | "rtl", multiline = false) => (
    <div>
      <label htmlFor={`${id}-${key}`} className="font-semibold">{label}</label>
      {multiline ? (
        <textarea id={`${id}-${key}`} dir={dir} rows={2} maxLength={500} value={value} onChange={(e) => set(e.target.value)} className={inputClass} />
      ) : (
        <input id={`${id}-${key}`} dir={dir} maxLength={100} value={value} onChange={(e) => set(e.target.value)} className={inputClass} />
      )}
    </div>
  );

  return (
    <form onSubmit={save} className="max-w-3xl space-y-4 rounded-md border border-line p-4" noValidate aria-label={widget ? t("edit") : t("add")}>
      <div className="grid gap-3 sm:grid-cols-2">
        {input("name-en", t("nameEn"), nameEn, setNameEn, "ltr")}
        {input("name-ar", t("nameAr"), nameAr, setNameAr, "rtl")}
        {input("greeting-en", t("greetingEn"), greetingEn, setGreetingEn, "ltr", true)}
        {input("greeting-ar", t("greetingAr"), greetingAr, setGreetingAr, "rtl", true)}
        <div>
          <label htmlFor={`${id}-group`} className="font-semibold">{t("group")}</label>
          <select id={`${id}-group`} value={groupId} onChange={(e) => setGroupId(e.target.value)} className={inputClass} aria-describedby={`${id}-group-help`}>
            {groups.filter((g) => g.active || g.id === groupId).map((g) => <option key={g.id} value={g.id}>{textOf(g.name, locale)}</option>)}
          </select>
          <p id={`${id}-group-help`} className="text-sm text-ink-muted">{t("groupHelp")}</p>
        </div>
        <div>
          <label htmlFor={`${id}-category`} className="font-semibold">{t("category")}</label>
          <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
            <option value="">{t("none")}</option>
            {selectableNodes(categories).map((c) => <option key={c.id} value={c.id}>{textOf(c.path, locale)}</option>)}
          </select>
        </div>
      </div>
      <div>
        <label htmlFor={`${id}-origins`} className="font-semibold">{t("websites")}</label>
        <textarea id={`${id}-origins`} dir="ltr" rows={3} value={origins} onChange={(e) => setOrigins(e.target.value)} className={inputClass} aria-describedby={`${id}-origins-help`} />
        <p id={`${id}-origins-help`} className="text-sm text-ink-muted">{t("websitesHelp")}</p>
      </div>
      <div className="flex flex-wrap gap-6">
        <label className="flex items-center gap-2"><input type="checkbox" checked={onPortal} onChange={() => setOnPortal(!onPortal)} />{t("showOnPortal")}</label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={active} onChange={() => setActive(!active)} />{t("acceptsChats")}</label>
      </div>
      {error && <p role="alert" className="text-bad">{error}</p>}
      <div className="flex gap-2">
        <button type="submit" disabled={!groupId || (!nameEn.trim() && !nameAr.trim())} className={primaryButton}>{t("save")}</button>
        {onCancel && <button type="button" className={secondaryButton} onClick={onCancel}>{t("cancel")}</button>}
      </div>
    </form>
  );
}
