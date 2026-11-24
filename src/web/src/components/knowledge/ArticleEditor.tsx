"use client";

import { useId, useRef, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ApiError, postFile } from "@/lib/client-api";
import { textOf, type TreeNode } from "@/lib/directory";
import type { Locale } from "@/lib/incidents";
import { imageMarkdown, type ArticleImage, type ArticleVersion, type ArticleVisibility } from "@/lib/knowledge";
import { inputClass, primaryButton, secondaryButton } from "../settings/integrations/ui";
import { MarkdownView } from "./MarkdownView";

/** What the editor saves (the API's article content). */
export interface ArticleContent {
  title: { en: string | null; ar: string | null };
  summary: { en: string | null; ar: string | null };
  body: { en: string | null; ar: string | null };
  keywords: string[];
  visibility: ArticleVisibility;
  categoryId: string | null;
  changeNote: string | null;
}

type Language = "en" | "ar";

const blank = (value: string) => (value.trim() ? value : null);

/**
 * The article editor: title, summary and Markdown body per language (English and Arabic tabs,
 * each written in its own direction), a preview, images, keywords, visibility and category.
 * Either language may be left empty; the missing one is reported.
 */
export function ArticleEditor({
  initial,
  articleId,
  images,
  categories,
  locale,
  saving,
  error,
  onSave,
  onImageAdded,
  csrfToken,
  submitLabel,
}: {
  initial: ArticleVersion | null;
  articleId: string | null;
  images: ArticleImage[];
  categories: TreeNode[];
  locale: Locale;
  saving: boolean;
  error: string | null;
  onSave: (content: ArticleContent) => void;
  onImageAdded?: (image: ArticleImage) => void;
  csrfToken: string;
  submitLabel: string;
}) {
  const t = useTranslations("knowledge.editor");
  const tVisibility = useTranslations("knowledge.visibility");
  const id = useId();
  const [language, setLanguage] = useState<Language>(locale);
  const [preview, setPreview] = useState(false);
  const [title, setTitle] = useState({ en: initial?.title.en ?? "", ar: initial?.title.ar ?? "" });
  const [summary, setSummary] = useState({ en: initial?.summary.en ?? "", ar: initial?.summary.ar ?? "" });
  const [body, setBody] = useState({ en: initial?.body.en ?? "", ar: initial?.body.ar ?? "" });
  const [keywords, setKeywords] = useState((initial?.keywords ?? []).join(", "));
  const [visibility, setVisibility] = useState<ArticleVisibility>(initial?.visibility ?? "Internal");
  const [categoryId, setCategoryId] = useState(initial?.categoryId ?? "");
  const [changeNote, setChangeNote] = useState(initial?.changeNote ?? "");
  const [imageError, setImageError] = useState<string | null>(null);
  const [localError, setLocalError] = useState<string | null>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);
  const dir = language === "ar" ? "rtl" : "ltr";

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!title.en.trim() && !title.ar.trim()) {
      setLocalError(t("titleRequired"));
      return;
    }
    setLocalError(null);
    onSave({
      title: { en: blank(title.en), ar: blank(title.ar) },
      summary: { en: blank(summary.en), ar: blank(summary.ar) },
      body: { en: blank(body.en), ar: blank(body.ar) },
      keywords: keywords.split(/[,،]/).map((k) => k.trim()).filter(Boolean),
      visibility,
      categoryId: categoryId || null,
      changeNote: blank(changeNote),
    });
  }

  async function upload(file: File) {
    if (!articleId) return;
    setImageError(null);
    try {
      const image = await postFile<ArticleImage>(`/v1/knowledge/articles/${articleId}/images`, file, csrfToken);
      onImageAdded?.(image);
      insert(imageMarkdown(image));
    } catch (refused) {
      const code = refused instanceof ApiError ? refused.code : null;
      setImageError(t(code === "image-too-large" || code === "image-type-not-allowed" || code === "image-content-mismatch" ? `imageErrors.${code}` : "imageErrors.other"));
    }
  }

  function insert(markdown: string) {
    const area = bodyRef.current;
    const current = body[language];
    const at = area?.selectionStart ?? current.length;
    const next = `${current.slice(0, at)}${at > 0 && current[at - 1] !== "\n" ? "\n\n" : ""}${markdown}\n\n${current.slice(at)}`;
    setBody({ ...body, [language]: next });
  }

  const missing = (["en", "ar"] as const).filter((l) => !title[l].trim() || !body[l].trim());
  return (
    <form onSubmit={submit} className="space-y-4" aria-labelledby={`${id}-heading`} noValidate data-testid="article-editor">
      <h2 id={`${id}-heading`} className="sr-only">{t("heading")}</h2>
      <div role="tablist" aria-label={t("languages")} className="flex gap-2 border-b border-line">
        {(["en", "ar"] as const).map((l) => (
          <button
            key={l}
            type="button"
            role="tab"
            id={`${id}-tab-${l}`}
            aria-selected={language === l}
            aria-controls={`${id}-panel`}
            onClick={() => setLanguage(l)}
            className={`-mb-px border-b-2 px-3 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-brand ${language === l ? "border-brand text-brand" : "border-transparent"}`}
          >
            {t(`language.${l}`)}
            {missing.includes(l) && <span className="ms-1 text-sm text-bad">({t("incomplete")})</span>}
          </button>
        ))}
      </div>

      <div id={`${id}-panel`} role="tabpanel" aria-labelledby={`${id}-tab-${language}`} className="space-y-4">
        <div>
          <label htmlFor={`${id}-title`} className="font-semibold">{t("title")}</label>
          <input id={`${id}-title`} lang={language} dir={dir} maxLength={200} value={title[language]} onChange={(e) => setTitle({ ...title, [language]: e.target.value })} className={inputClass} />
        </div>
        <div>
          <label htmlFor={`${id}-summary`} className="font-semibold">{t("summary")}</label>
          <input id={`${id}-summary`} lang={language} dir={dir} maxLength={500} value={summary[language]} onChange={(e) => setSummary({ ...summary, [language]: e.target.value })} className={inputClass} />
        </div>
        <div className="space-y-2">
          <div className="flex flex-wrap items-end justify-between gap-2">
            <label htmlFor={`${id}-body`} className="font-semibold">{t("body")}</label>
            <button type="button" onClick={() => setPreview(!preview)} aria-pressed={preview} className={secondaryButton}>
              {preview ? t("write") : t("preview")}
            </button>
          </div>
          {preview ? (
            <div className="min-h-40 rounded-md border border-line p-3" data-testid="article-preview">
              {body[language].trim() ? (
                <MarkdownView markdown={body[language]} articleId={articleId ?? "00000000-0000-0000-0000-000000000000"} lang={language} dir={dir} />
              ) : (
                <p className="text-ink-muted">{t("nothingToPreview")}</p>
              )}
            </div>
          ) : (
            <textarea
              id={`${id}-body`}
              ref={bodyRef}
              lang={language}
              dir={dir}
              rows={16}
              maxLength={100000}
              value={body[language]}
              onChange={(e) => setBody({ ...body, [language]: e.target.value })}
              aria-describedby={`${id}-body-help`}
              className={`${inputClass} font-mono text-sm`}
            />
          )}
          <p id={`${id}-body-help`} className="text-sm text-ink-muted">{t("bodyHelp")}</p>
        </div>
      </div>

      {articleId ? (
        <div className="space-y-2">
          <label htmlFor={`${id}-image`} className="font-semibold">{t("addImage")}</label>
          <input
            id={`${id}-image`}
            type="file"
            accept="image/png,image/jpeg,image/gif,image/webp"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) void upload(file);
              e.target.value = "";
            }}
            aria-describedby={`${id}-image-help`}
            className="block"
          />
          <p id={`${id}-image-help`} className="text-sm text-ink-muted">{t("imageHelp")}</p>
          {imageError && <p role="alert" className="text-bad">{imageError}</p>}
          {images.length > 0 && (
            <ul className="flex flex-wrap gap-2 text-sm" aria-label={t("images")}>
              {images.map((image) => (
                <li key={image.id}>
                  <button type="button" onClick={() => insert(imageMarkdown(image))} className={secondaryButton}>
                    {t("insertImage", { name: image.fileName })}
                  </button>
                  {!image.available && <span className="ms-1 text-ink-muted">{t("imageScanning")}</span>}
                </li>
              ))}
            </ul>
          )}
        </div>
      ) : (
        <p className="text-sm text-ink-muted">{t("imagesAfterSave")}</p>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <fieldset className="space-y-1">
          <legend className="font-semibold">{t("visibility")}</legend>
          {(["Internal", "External"] as const).map((v) => (
            <label key={v} className="flex items-center gap-2">
              <input type="radio" name={`${id}-visibility`} value={v} checked={visibility === v} onChange={() => setVisibility(v)} />
              <span>{tVisibility(v)}</span>
              <span className="text-sm text-ink-muted">— {tVisibility(`${v}Help`)}</span>
            </label>
          ))}
        </fieldset>
        <div className="space-y-3">
          <div>
            <label htmlFor={`${id}-category`} className="font-semibold">{t("category")}</label>
            <select id={`${id}-category`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}>
              <option value="">{t("noCategory")}</option>
              {categories.map((c) => (
                <option key={c.id} value={c.id}>{textOf(c.path, locale)}</option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor={`${id}-keywords`} className="font-semibold">{t("keywords")}</label>
            <input id={`${id}-keywords`} value={keywords} onChange={(e) => setKeywords(e.target.value)} aria-describedby={`${id}-keywords-help`} className={inputClass} />
            <p id={`${id}-keywords-help`} className="text-sm text-ink-muted">{t("keywordsHelp")}</p>
          </div>
        </div>
      </div>
      <div>
        <label htmlFor={`${id}-note`} className="font-semibold">{t("changeNote")}</label>
        <input id={`${id}-note`} maxLength={1000} value={changeNote} onChange={(e) => setChangeNote(e.target.value)} className={inputClass} />
      </div>
      {(localError ?? error) && <p role="alert" className="text-bad">{localError ?? error}</p>}
      <button type="submit" disabled={saving} className={primaryButton}>{submitLabel}</button>
    </form>
  );
}
