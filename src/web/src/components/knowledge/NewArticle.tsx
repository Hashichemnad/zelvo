"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "@/i18n/navigation";
import { ApiError, postJson } from "@/lib/client-api";
import type { TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import type { ArticleDetail } from "@/lib/knowledge";
import { aiErrorKey } from "@/lib/ai-features";
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

/**
 * "Turn this ticket into an article" on a resolved or closed incident. Task E5: with the AI feature
 * articleDrafts on, the AI can write the draft in English and Arabic from the whole conversation;
 * it is still a draft for the four-eyes review.
 */
export function CreateArticleFromIncident({ incidentId, csrfToken, withAi = false }: { incidentId: string; csrfToken: string; withAi?: boolean }) {
  const t = useTranslations("knowledge.fromIncident");
  const tAi = useTranslations("ai.assist");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  async function create(draftWithAi: boolean) {
    setBusy(true);
    setFailed(null);
    try {
      const created = await postJson<ArticleDetail>(`/v1/incidents/${incidentId}/knowledge-article${draftWithAi ? "?draftWithAi=true" : ""}`, undefined, csrfToken);
      router.push(`/knowledge/${created.article.id}`);
    } catch (refused) {
      setFailed(draftWithAi ? tAi(aiErrorKey(refused instanceof ApiError ? refused.code : null)) : t("failed"));
      setBusy(false);
    }
  }

  const button = "rounded-md border border-line px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand";
  return (
    <div className="space-y-1">
      <div className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} onClick={() => void create(false)} className={button} data-testid="create-article-from-incident">
          {t("button")}
        </button>
        {withAi && (
          <button type="button" disabled={busy} onClick={() => void create(true)} className={button} data-testid="draft-article-with-ai">
            {busy ? tAi("working") : t("withAi")}
          </button>
        )}
      </div>
      <p className="text-sm text-ink-muted">{withAi ? t("helpWithAi") : t("help")}</p>
      {failed && <p role="alert" className="text-bad">{failed}</p>}
    </div>
  );
}
