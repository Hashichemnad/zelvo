"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import type { TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { ArticleDetail } from "@/lib/knowledge";
import { ArticleEditor, type ArticleContent } from "./ArticleEditor";

/** Starts a new article as a draft, then opens it. */
export function NewArticle({ categories, locale, csrfToken }: { categories: TreeNode[]; locale: Locale; csrfToken: string }) {
  const t = useTranslations("knowledge.workspace");
  const tErrors = useTranslations("knowledge.errors");
  const router = useRouter();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function create(content: ArticleContent) {
    setSaving(true);
    setError(null);
    try {
      const created = await postJson<ArticleDetail>("/v1/knowledge/articles", content, csrfToken);
      router.push(`/knowledge/${created.article.id}`);
    } catch (refused) {
      setError(tErrors(refused instanceof ApiError && refused.code === "validation-failed" ? "validation-failed" : "other"));
      setSaving(false);
    }
  }

  return (
    <ArticleEditor initial={null} articleId={null} images={[]} categories={categories} locale={locale} saving={saving} error={error} onSave={create} csrfToken={csrfToken} submitLabel={t("createDraft")} />
  );
}

/** "Turn this ticket into an article" on a resolved or closed incident. */
export function CreateArticleFromIncident({ incidentId, csrfToken }: { incidentId: string; csrfToken: string }) {
  const t = useTranslations("knowledge.fromIncident");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function create() {
    setBusy(true);
    setFailed(false);
    try {
      const created = await postJson<ArticleDetail>(`/v1/incidents/${incidentId}/knowledge-article`, undefined, csrfToken);
      router.push(`/knowledge/${created.article.id}`);
    } catch {
      setFailed(true);
      setBusy(false);
    }
  }

  return (
    <div className="space-y-1">
      <button type="button" disabled={busy} onClick={create} className="rounded-md border border-line px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand" data-testid="create-article-from-incident">
        {t("button")}
      </button>
      <p className="text-sm text-ink-muted">{t("help")}</p>
      {failed && <p role="alert" className="text-bad">{t("failed")}</p>}
    </div>
  );
}
