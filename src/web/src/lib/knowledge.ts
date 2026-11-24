/** Knowledge base (module 7, task D1) as the API returns it. */

import type { Bilingual } from "./incidents";

export type ArticleState = "Draft" | "InReview" | "Published" | "Retired";
export type ArticleVisibility = "Internal" | "External";
export type VersionState = "Draft" | "InReview" | "Published" | "Superseded" | "Retired";

export const articleStates: ArticleState[] = ["Draft", "InReview", "Published", "Retired"];

export interface Person {
  id: string;
  name: string | null;
}

export interface ArticleSummary {
  id: string;
  number: string;
  state: ArticleState;
  visibility: ArticleVisibility;
  categoryId: string | null;
  title: Bilingual;
  summary: Bilingual;
  pendingState: VersionState | null;
  submittedAt: string | null;
  reviewComment: string | null;
  canReview: boolean;
  viewCount: number;
  helpfulCount: number;
  notHelpfulCount: number;
  createdById: string | null;
  sourceTicketId: string | null;
  publishedAt: string | null;
  retiredAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ArticleVersion {
  id: string;
  number: number;
  state: VersionState;
  title: Bilingual;
  summary: Bilingual;
  body: Bilingual;
  keywords: string[];
  visibility: ArticleVisibility;
  categoryId: string | null;
  changeNote: string | null;
  createdBy: Person | null;
  lastEditedBy: Person | null;
  submittedBy: Person | null;
  submittedAt: string | null;
  reviewedBy: Person | null;
  reviewedAt: string | null;
  reviewComment: string | null;
  publishedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ArticleImage {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: "Pending" | "Clean" | "Infected" | "NotScanned";
  available: boolean;
  uploadedAt: string;
}

export interface ArticleActions {
  edit: boolean;
  submit: boolean;
  review: boolean;
  retire: boolean;
  reopen: boolean;
  discard: boolean;
}

export interface ArticleDetail {
  article: ArticleSummary;
  published: ArticleVersion | null;
  working: ArticleVersion | null;
  sourceTicket: { id: string; number: string } | null;
  images: ArticleImage[];
  actions: ArticleActions;
}

export interface PublishedSummary {
  id: string;
  number: string;
  visibility: ArticleVisibility;
  categoryId: string | null;
  title: Bilingual;
  summary: Bilingual;
  versionNumber: number;
  viewCount: number;
  helpfulCount: number;
  notHelpfulCount: number;
  publishedAt: string | null;
}

export interface PublishedArticle {
  article: PublishedSummary;
  body: Bilingual;
  keywords: string[];
  images: ArticleImage[];
}

export interface MyFeedback {
  helpful: boolean | null;
  comment: string | null;
  answeredAt: string | null;
  helpfulCount: number;
  notHelpfulCount: number;
}

export interface FeedbackEntry {
  id: string;
  helpful: boolean;
  comment: string | null;
  userName: string | null;
  visitor: boolean;
  versionNumber: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface KnowledgeSettings {
  publicAccess: boolean;
  numberPrefix: string;
  updatedAt: string | null;
}

/** Image references in Markdown: ![text](zelvo-image:<id>). */
export const imageScheme = "zelvo-image:";

/** Markdown that shows an uploaded image. Brackets in the text would end it, so they are dropped. */
export function imageMarkdown(image: Pick<ArticleImage, "id" | "fileName">, text?: string): string {
  const alt = (text ?? image.fileName.replace(/\.[a-z0-9]+$/i, "")).replace(/[[\]]/g, "");
  return `![${alt}](${imageScheme}${image.id})`;
}

/** Browser address of an article image (through the web app's proxy). */
export function imageUrl(articleId: string, imageId: string): string {
  return `/bff/api/v1/knowledge/articles/${articleId}/images/${imageId}`;
}

const visitorKey = "zelvo-visitor";

/**
 * A random id this browser keeps, so a visitor's views and "Was this helpful?" count once. Not
 * personal; the API stores only a hash. Null when the browser keeps nothing (private mode).
 */
export function visitorId(): string | null {
  try {
    const existing = window.localStorage.getItem(visitorKey);
    if (existing && /^[0-9a-f-]{16,64}$/i.test(existing)) {
      return existing;
    }
    const created = crypto.randomUUID();
    window.localStorage.setItem(visitorKey, created);
    return created;
  } catch {
    return null;
  }
}

/** Holds a knowledge staff permission (sees the staff knowledge pages). */
export function isKnowledgeStaff(permissions: readonly string[]): boolean {
  return permissions.some((p) => p === "knowledge.read" || p === "knowledge.write" || p === "knowledge.publish");
}
