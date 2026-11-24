"use client";

import Markdown, { defaultUrlTransform } from "react-markdown";
import remarkGfm from "remark-gfm";
import { imageScheme, imageUrl } from "@/lib/knowledge";

/**
 * Article text written in Markdown (headings, lists, links, tables, code, images). Raw HTML in the
 * text is never rendered (react-markdown leaves it out), links get only safe protocols, and
 * images come only from the article's own scanned uploads.
 */
export function MarkdownView({ markdown, articleId, lang, dir }: { markdown: string; articleId: string; lang?: string; dir?: "ltr" | "rtl" }) {
  return (
    <div className="article-body max-w-none space-y-3" lang={lang} dir={dir}>
      <Markdown
        remarkPlugins={[remarkGfm]}
        skipHtml
        urlTransform={(url, key) => {
          if (key === "src") {
            return url.startsWith(imageScheme) && /^[0-9a-f-]{36}$/i.test(url.slice(imageScheme.length))
              ? imageUrl(articleId, url.slice(imageScheme.length))
              : "";
          }
          return defaultUrlTransform(url);
        }}
        components={{
          h1: ({ children }) => <h2 className="text-xl font-bold">{children}</h2>,
          h2: ({ children }) => <h3 className="text-lg font-bold">{children}</h3>,
          h3: ({ children }) => <h4 className="font-bold">{children}</h4>,
          ul: ({ children }) => <ul className="list-disc space-y-1 ps-6">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal space-y-1 ps-6">{children}</ol>,
          a: ({ children, href }) => (
            <a href={href} className="text-brand underline" rel="noopener noreferrer nofollow">
              {children}
            </a>
          ),
          // eslint-disable-next-line @next/next/no-img-element -- article images come through the API proxy
          img: ({ src, alt }) => (src ? <img src={String(src)} alt={alt ?? ""} className="max-w-full rounded border border-line" loading="lazy" /> : <span>{alt}</span>),
          code: ({ children }) => <code className="rounded bg-surface-muted px-1" dir="ltr">{children}</code>,
          pre: ({ children }) => <pre className="overflow-x-auto rounded bg-surface-muted p-3" dir="ltr">{children}</pre>,
          table: ({ children }) => (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-start text-sm">{children}</table>
            </div>
          ),
          th: ({ children }) => <th className="border-b border-line px-2 py-1 text-start">{children}</th>,
          td: ({ children }) => <td className="border-b border-line px-2 py-1 text-start align-top">{children}</td>,
          blockquote: ({ children }) => <blockquote className="border-s-4 border-line ps-3">{children}</blockquote>,
        }}
      >
        {markdown}
      </Markdown>
    </div>
  );
}
