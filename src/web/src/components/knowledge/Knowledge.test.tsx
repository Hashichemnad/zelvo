import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { Page } from "@/lib/integrations";
import { imageMarkdown, type ArticleDetail, type ArticleSummary, type ArticleVersion, type PublishedArticle } from "@/lib/knowledge";
import { sectionsFor } from "@/lib/settings-sections";
import { ArticleEditor } from "./ArticleEditor";
import { ArticleWorkspace } from "./ArticleWorkspace";
import { KnowledgeList } from "./KnowledgeList";
import { MarkdownView } from "./MarkdownView";
import { PublishedArticleView } from "./PublishedArticleView";

const push = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/knowledge",
  useRouter: () => ({ push }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const id = "0190a8b2-6c1e-7d3a-9f00-1234567890ab";
const imageId = "0190a8b2-6c1e-7d3a-9f00-0000000000aa";
const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      {children}
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const summary: ArticleSummary = {
  id,
  number: "KB-000001",
  state: "InReview",
  visibility: "External",
  categoryId: null,
  title: { en: "Connect to the VPN", ar: null, missing: ["ar"] },
  summary: { en: "Install and sign in.", ar: null },
  pendingState: "InReview",
  submittedAt: "2026-11-24T08:00:00Z",
  reviewComment: null,
  canReview: true,
  viewCount: 12,
  helpfulCount: 3,
  notHelpfulCount: 1,
  createdById: "u1",
  sourceTicketId: null,
  publishedAt: null,
  retiredAt: null,
  createdAt: "2026-11-23T08:00:00Z",
  updatedAt: "2026-11-24T08:00:00Z",
};

const version: ArticleVersion = {
  id: "v1",
  number: 1,
  state: "InReview",
  title: { en: "Connect to the VPN", ar: null },
  summary: { en: "Install and sign in.", ar: null },
  body: { en: "## Install\n\n1. Download the client.", ar: null },
  keywords: ["vpn"],
  visibility: "External",
  categoryId: null,
  changeNote: null,
  createdBy: { id: "u1", name: "Aisha" },
  lastEditedBy: { id: "u1", name: "Aisha" },
  submittedBy: { id: "u1", name: "Aisha" },
  submittedAt: "2026-11-24T08:00:00Z",
  reviewedBy: null,
  reviewedAt: null,
  reviewComment: null,
  publishedAt: null,
  createdAt: "2026-11-23T08:00:00Z",
  updatedAt: "2026-11-24T08:00:00Z",
};

const detail: ArticleDetail = {
  article: summary,
  published: null,
  working: version,
  sourceTicket: null,
  images: [],
  actions: { edit: false, submit: false, review: true, retire: false, reopen: false, discard: true },
};

function keys(value: unknown, prefix = ""): string[] {
  return value && typeof value === "object"
    ? Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => keys(v, `${prefix}${k}.`))
    : [prefix];
}

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
  window.localStorage.clear();
});

describe("knowledge base texts and settings", () => {
  it("has every text in English and Arabic", () => {
    expect(keys(ar.knowledge).sort()).toEqual(keys(en.knowledge).sort());
    expect(keys(ar.settings.knowledge).sort()).toEqual(keys(en.settings.knowledge).sort());
  });

  it("shows Settings → Knowledge to settings managers", () => {
    expect(sectionsFor(["settings.manage"]).map((s) => s.key)).toContain("knowledge");
    expect(sectionsFor(["knowledge.publish"]).map((s) => s.key)).not.toContain("knowledge");
  });
});

describe("MarkdownView", () => {
  it("renders Markdown but never raw HTML, unsafe links or images from elsewhere", () => {
    const markdown = [
      "## Steps",
      "",
      "| Step | What |",
      "| --- | --- |",
      "| 1 | Open the portal |",
      "",
      "<script>alert(1)</script><b>bold html</b>",
      "",
      "[safe](https://example.ae) [bad](javascript:alert(1))",
      "",
      imageMarkdown({ id: imageId, fileName: "dock.png" }),
      "![tracker](https://evil.example/pixel.png)",
    ].join("\n");
    const { container } = wrap(<MarkdownView markdown={markdown} articleId={id} />);

    expect(screen.getByRole("heading", { name: "Steps" })).toBeInTheDocument();
    expect(screen.getByRole("table")).toHaveTextContent("Open the portal");
    expect(container.querySelector("script")).toBeNull();
    expect(container.querySelector("b")).toBeNull();
    expect(screen.getByRole("link", { name: "safe" })).toHaveAttribute("href", "https://example.ae");
    expect(screen.getByText("bad").closest("a")?.getAttribute("href") ?? "").not.toContain("javascript");
    expect(screen.getByRole("img", { name: "dock" })).toHaveAttribute("src", `/bff/api/v1/knowledge/articles/${id}/images/${imageId}`);
    expect(screen.queryByRole("img", { name: "tracker" })).toBeNull();
  });
});

describe("PublishedArticleView", () => {
  const article: PublishedArticle = {
    article: { id, number: "KB-000001", visibility: "External", categoryId: null, title: { en: "Reset your password", ar: null, missing: ["ar"] }, summary: { en: "Two minutes.", ar: null }, versionNumber: 1, viewCount: 4, helpfulCount: 2, notHelpfulCount: 0, publishedAt: "2026-11-20T08:00:00Z" },
    body: { en: "Open the password page.", ar: null, missing: ["ar"] },
    keywords: [],
    images: [],
  };

  it("shows the article in the language it exists in, counts the visitor's view and records the answer", async () => {
    const fetchMock = vi.fn((url: string, init?: RequestInit) =>
      Promise.resolve(
        url.endsWith("/views")
          ? json({ viewCount: 5, counted: true })
          : init?.method === "PUT"
            ? json({ helpful: true, comment: null, answeredAt: "2026-11-24T08:00:00Z", helpfulCount: 3, notHelpfulCount: 0 })
            : json({ helpful: null, comment: null, answeredAt: null, helpfulCount: 2, notHelpfulCount: 0 }),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<PublishedArticleView article={article} display={{ ...display, locale: "ar", digits: "arab" }} csrfToken={null} />, "ar");

    expect(screen.getByTestId("other-language-note")).toHaveTextContent("غير متاحة بلغتك");
    expect(screen.getByRole("heading", { level: 1 })).toHaveAttribute("lang", "en");
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith("/views"))).toBe(true));
    const view = fetchMock.mock.calls.find(([url]) => String(url).endsWith("/views")) as unknown as [string, RequestInit];
    expect(JSON.parse(view[1].body as string).visitorId).toMatch(/^[0-9a-f-]{36}$/);

    fireEvent.click(screen.getByRole("button", { name: "نعم" }));
    expect(await screen.findByText("شكراً على إجابتك.")).toBeInTheDocument();
    const put = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "PUT") as unknown as [string, RequestInit];
    expect(put[0]).toBe(`/bff/api/v1/knowledge/published/${id}/feedback`);
    expect(JSON.parse(put[1].body as string)).toMatchObject({ helpful: true, visitorId: JSON.parse(view[1].body as string).visitorId });
  });
});

describe("ArticleEditor", () => {
  it("writes each language in its own direction and saves both, splitting keywords on either comma", () => {
    const onSave = vi.fn();
    wrap(<ArticleEditor initial={null} articleId={null} images={[]} categories={[]} locale="en" saving={false} error={null} onSave={onSave} csrfToken="c" submitLabel="Save as draft" />);

    fireEvent.click(screen.getByRole("button", { name: "Save as draft" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Give the article a title");
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Printer setup" } });
    fireEvent.click(screen.getByRole("tab", { name: /Arabic/ }));
    expect(screen.getByLabelText("Title")).toHaveAttribute("dir", "rtl");
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "إعداد الطابعة" } });
    fireEvent.change(screen.getByLabelText("Article"), { target: { value: "## الخطوات" } });
    fireEvent.change(screen.getByLabelText("Keywords"), { target: { value: "طابعة، printer" } });
    fireEvent.click(screen.getByRole("radio", { name: /External/ }));
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    expect(within(screen.getByTestId("article-preview")).getByRole("heading", { name: "الخطوات" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save as draft" }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      title: { en: "Printer setup", ar: "إعداد الطابعة" },
      body: { en: null, ar: "## الخطوات" },
      keywords: ["طابعة", "printer"],
      visibility: "External",
    }));
  });
});

describe("ArticleWorkspace", () => {
  it("lets a reviewer approve, and asks for a comment before sending back", async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(json(url.endsWith("/approve")
        ? { ...detail, article: { ...summary, state: "Published" }, published: { ...version, state: "Published" }, working: null, actions: { ...detail.actions, review: false, retire: true } }
        : detail)),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<ArticleWorkspace initial={detail} categories={[]} display={{ ...display, locale: "en" }} csrfToken="c" />);

    const review = screen.getByTestId("review-box");
    fireEvent.click(within(review).getByRole("button", { name: "Send back" }));
    expect(within(review).getByText("Say what the author should change.")).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();

    fireEvent.click(within(review).getByRole("button", { name: "Approve and publish" }));
    expect(await screen.findByText("Approved and published.")).toBeInTheDocument();
    expect(screen.getByTestId("article-state")).toHaveTextContent("Published");
    expect(fetchMock.mock.calls[0]![0]).toBe(`/bff/api/v1/knowledge/articles/${id}/approve`);
  });

  it("says why a publisher cannot review their own article", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json({ code: "own-article" }, 403))));
    wrap(<ArticleWorkspace initial={detail} categories={[]} display={{ ...display, locale: "en" }} csrfToken="c" />);

    fireEvent.click(screen.getByRole("button", { name: "Approve and publish" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Someone other than the author reviews an article");
  });
});

describe("KnowledgeList", () => {
  it("lists articles with their state, visibility and a missing language, in Arabic", () => {
    const page: Page<ArticleSummary> = { items: [summary], page: 1, pageSize: 25, totalCount: 1 };
    wrap(<KnowledgeList initial={page} display={{ ...display, locale: "ar", digits: "arab" }} />, "ar");

    const row = screen.getByTestId("knowledge-row");
    expect(row).toHaveTextContent("قيد المراجعة");
    expect(row).toHaveTextContent("خارجية");
    expect(row).toHaveTextContent("تنقصها لغة");
    expect(row).toHaveTextContent("١٢");
    expect(within(row).getByRole("link", { name: "Connect to the VPN" })).toHaveAttribute("lang", "en");
  });
});
