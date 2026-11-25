import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { display, incident, settings } from "@/components/incidents/fixtures";
import type { AiInsights } from "@/lib/ai-features";
import type { Comment } from "@/lib/workspace";
import { Composer, ConversationList } from "@/components/workspace/Conversation";
import { ChatWindow } from "@/components/channels/ChatWindow";
import { AiAssistantPanel } from "./AiAssistantPanel";

const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh }),
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  refresh.mockReset();
});

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const insights: AiInsights = {
  available: ["triage", "summary", "translation"],
  triage: {
    id: "s1", status: "Pending", category: { id: "cat-1", name: { en: "Printers", ar: "الطابعات" } }, impact: 1, urgency: 2,
    group: { id: "g1", name: { en: "Service desk", ar: "مكتب الخدمة" } }, confidence: 85, reason: "Whole floor.", createdAt: "2026-11-24T08:00:00Z",
    decidedAt: null, final: null,
  },
  sentiment: "Frustrated",
  sentimentReason: "Third time asking.",
  sentimentAt: "2026-11-24T08:10:00Z",
  frustratedSince: "2026-11-24T08:10:00Z",
  duplicates: [{ id: "d1", incident: { id: "other", number: "INC-000041", title: "Printer jammed", state: "New" }, confidence: 80, reason: "Same printer.", status: "Pending", createdAt: "2026-11-24T08:00:00Z" }],
  summary: null,
};

const categories = [{ id: "cat-2", parentId: null, depth: 1, position: 1, name: { en: "Network", ar: "الشبكة" }, path: { en: "Network", ar: "الشبكة" }, active: true, selectable: true, childCount: 0 }];
const groups = [{ id: "g2", name: { en: "Facilities", ar: "المرافق" }, description: null, active: true, memberCount: 1, leadCount: 0 }];

describe("AiAssistantPanel", () => {
  it("shows the suggestion, the frustrated requester and possible duplicates, and accepts as suggested", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AiAssistantPanel incidentId={incident.id} insights={insights} csrfToken="csrf" permissions={["incidents.read", "incidents.edit", "incidents.assign"]}
      settings={settings} categories={categories} groups={groups} display={display} />);
    const panel = screen.getByTestId("ai-assistant");
    expect(within(panel).getByTestId("ai-sentiment")).toHaveTextContent("frustrated");
    expect(within(panel).getByTestId("ai-triage")).toHaveTextContent("Printers");
    expect(within(panel).getByTestId("ai-triage")).toHaveTextContent("Service desk");
    expect(within(panel).getByTestId("ai-duplicates")).toHaveTextContent("INC-000041");

    fireEvent.click(within(panel).getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(`/bff/api/v1/incidents/${incident.id}/ai/triage/s1/accept`);
    expect((init as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf" });
  });

  it("sends corrections, in Arabic too", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AiAssistantPanel incidentId={incident.id} insights={insights} csrfToken="csrf" permissions={["incidents.read", "incidents.edit", "incidents.assign"]}
      settings={settings} categories={categories} groups={groups} display={{ ...display, locale: "ar" }} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "تصحيح" }));
    const form = screen.getByRole("form", { name: "تصحيح" });
    fireEvent.change(within(form).getAllByRole("combobox")[0]!, { target: { value: "cat-2" } });
    fireEvent.click(within(form).getByRole("button", { name: "تطبيق مع التصحيح" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toMatchObject({ categoryId: "cat-2", impact: 1, urgency: 2 });
  });

  it("says when the AI is not set up, and requesters' staff without edit rights only read", () => {
    withIntl(<AiAssistantPanel incidentId={incident.id} insights={{ ...insights, available: [], triage: null, sentiment: null, duplicates: [], summary: null }}
      csrfToken="c" permissions={["incidents.read"]} settings={settings} categories={[]} groups={[]} display={display} />);
    expect(screen.getByText(/once an administrator sets up the AI features/)).toBeInTheDocument();

    withIntl(<AiAssistantPanel incidentId={incident.id} insights={insights} csrfToken="c" permissions={["incidents.read"]}
      settings={settings} categories={[]} groups={[]} display={display} />);
    expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Link as duplicate" })).not.toBeInTheDocument();
  });

  it("makes a summary in the agent's language", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ text: "- ملخص", language: "ar", createdAt: "2026-11-24T09:00:00Z", stale: false }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AiAssistantPanel incidentId={incident.id} insights={{ ...insights, triage: null }} csrfToken="c" permissions={["incidents.read"]}
      settings={settings} categories={[]} groups={[]} display={{ ...display, locale: "ar" }} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "تلخيص المحادثة" }));
    expect(await screen.findByText("- ملخص")).toHaveAttribute("lang", "ar");
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ refresh: false, language: "ar" });
  });
});

describe("AI in the conversation", () => {
  const comments: Comment[] = [
    { id: "c1", ticketId: incident.id, visibility: "Public", body: "الطابعة لا تعمل", language: "ar", author: { id: "r", displayName: "Mariam" }, byStaff: false, mentions: [], cannedResponseId: null, createdAt: "2026-11-24T08:00:00Z" },
    { id: "c2", ticketId: incident.id, visibility: "Public", body: "On it.", language: "en", author: { id: "a", displayName: "Hamad" }, byStaff: true, mentions: [], cannedResponseId: null, createdAt: "2026-11-24T08:05:00Z" },
  ];

  it("translates messages in the other language with one click", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok({ text: "The printer does not work", language: "en", cached: false })));
    withIntl(<ConversationList comments={comments} display={display} staff translate={{ incidentId: incident.id, csrfToken: "c" }} />);
    const buttons = screen.getAllByRole("button", { name: "Translate" });
    expect(buttons).toHaveLength(1);
    fireEvent.click(buttons[0]!);
    expect(await screen.findByTestId("translation")).toHaveTextContent("The printer does not work");
    expect(screen.getByText("Machine translation")).toBeInTheDocument();
  });

  it("suggests a reply in the chosen language and translates the draft", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(ok({ text: "Please restart the printer.", language: "en", articles: [{ id: "a1", number: "KB-000003", title: "Printer restart" }] }))
      .mockResolvedValueOnce(ok({ text: "يُرجى إعادة تشغيل الطابعة.", language: "ar", cached: false }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<Composer incidentId={incident.id} csrfToken="c" staff responses={[]} people={[]} requesterLanguage="ar" ai={{ reply: true, translation: true }} />);
    fireEvent.click(screen.getByTestId("suggest-reply"));
    await waitFor(() => expect(screen.getByTestId("composer-body")).toHaveValue("Please restart the printer."));
    expect(screen.getByText(/KB-000003/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Language you write in"), { target: { value: "ar" } });
    fireEvent.click(screen.getByRole("button", { name: "Translate into Arabic" }));
    await waitFor(() => expect(screen.getByTestId("composer-body")).toHaveValue("يُرجى إعادة تشغيل الطابعة."));
    expect(fetchMock.mock.calls[1]![0]).toBe("/bff/api/v1/ai/translate");
  });

  it("offers no AI buttons when the features are off", () => {
    withIntl(<Composer incidentId={incident.id} csrfToken="c" staff responses={[]} people={[]} />);
    expect(screen.queryByTestId("suggest-reply")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Translate/ })).not.toBeInTheDocument();
  });
});

describe("ChatWindow with the virtual agent", () => {
  const widget = { id: "w1", name: { en: "Help", ar: "المساعدة" }, greeting: { en: null, ar: null }, online: false, allowed: true, allowedOrigins: [], virtualAgent: true };

  it("starts while nobody is online, shows the answer with its source and offers to leave the conversation", async () => {
    const assistant = {
      id: "s1", state: "Assistant", agentName: null, endedBy: null,
      messages: [
        { id: "m1", sequence: 1, sender: "Visitor", authorName: null, body: "Password?", event: null, createdAt: "2026-11-24T08:00:00Z", citations: [] },
        { id: "m2", sequence: 2, sender: "Assistant", authorName: null, body: "Use the self-service page.", event: null, createdAt: "2026-11-24T08:00:01Z", citations: [{ id: "art-1", number: "KB-000007" }] },
      ],
    };
    const fetchMock = vi.fn(async (url: string) => {
      if (url.endsWith("/sessions")) return ok({ sessionId: "s1", token: "t" }, 201);
      if (url.endsWith("/handover")) return ok({ code: "chat-offline" }, 409);
      return ok(assistant);
    });
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<ChatWindow widget={widget} locale="en" signedIn csrfToken="c" />);
    fireEvent.change(screen.getByLabelText("How can we help?"), { target: { value: "Password?" } });
    fireEvent.click(screen.getByRole("button", { name: "Start chat" }));
    expect(await screen.findByText("Use the self-service page.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "KB-000007" })).toHaveAttribute("href", "/en/help/articles/art-1");
    expect(screen.getByText(/chatting with the virtual agent/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Talk to a person" }));
    expect(await screen.findByRole("button", { name: "Leave it as a message" })).toBeInTheDocument();
  });
});
