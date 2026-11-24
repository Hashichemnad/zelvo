import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { mergeMessages, type ChatMessage, type ChatWidget, type Mailbox, type PublicChatWidget } from "@/lib/channels";
import type { Comment } from "@/lib/workspace";
import { isVisitorPath } from "@/lib/visitor-paths";
import { ChatWidgetsPanel, embedCode } from "./ChatWidgetsPanel";
import { ChatWindow } from "./ChatWindow";
import { ConversationList } from "../workspace/Conversation";
import { MailboxForm } from "./MailboxForm";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/mailboxes",
  useRouter: () => ({ push: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

// The live channel is not reachable in tests; polling is enough.
vi.mock("@/lib/live", () => ({ useLive: () => "offline" }));

const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function renderIn(locale: "en" | "ar", node: ReactNode) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const comment = (over: Partial<Comment>): Comment => ({
  id: "m1", visibility: "Public", byStaff: false, language: "en", body: "The printer is jammed.",
  author: { id: "u1", displayName: "Layla Ahmed" }, mentions: [], createdAt: "2026-11-24T08:00:00Z", ...over,
} as Comment);

const comments: Comment[] = [
  comment({ source: "Email", attachments: [{ id: "f1", fileName: "photo.png" }] }),
  comment({ id: "m2", byStaff: true, author: { id: "u2", displayName: "Omar" }, body: "A technician is on the way.", source: "Web", emailDelivery: "Sent" }),
  comment({ id: "m3", language: "ar", body: "شكرًا لكم", source: "Chat" }),
];

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe("the ticket conversation with email and chat (B4's thread)", () => {
  it("shows how each comment came in, its files and, for staff, whether the email went out", () => {
    renderIn("en", <ConversationList comments={comments} display={display} staff />);
    const items = screen.getAllByTestId("comment");
    expect(items).toHaveLength(3);
    expect(within(items[0]!).getByText("By email")).toBeInTheDocument();
    expect(within(items[0]!).getByText(/1 file/)).toBeInTheDocument();
    expect(within(items[0]!).getByText("photo.png")).toBeInTheDocument();
    expect(within(items[1]!).getByText("Email sent")).toBeInTheDocument();
    expect(within(items[2]!).getByText("From a chat")).toBeInTheDocument();
  });

  it("never shows requesters whether an email went out, in Arabic too", () => {
    renderIn("ar", <ConversationList comments={comments} display={{ ...display, locale: "ar" }} staff={false} />);
    expect(screen.queryByText(ar.workspace.conversation.delivery.Sent)).not.toBeInTheDocument();
    expect(screen.getByText(ar.workspace.conversation.sources.Email)).toBeInTheDocument();
  });
});

const widget: PublicChatWidget = {
  id: "0195f000-0000-7000-8000-000000000001",
  name: { en: "Chat with us", ar: "تحدّث معنا" },
  greeting: { en: "Hello, how can we help?", ar: null },
  online: true,
  allowed: true,
  allowedOrigins: ["https://www.entity.example.gov"],
};

const chatMessage = (sequence: number, sender: ChatMessage["sender"], body: string, event: string | null = null): ChatMessage => ({
  id: `c${sequence}`, sequence, sender, authorName: sender === "Visitor" ? null : "Omar", body, event, createdAt: "2026-11-24T08:00:00Z",
});

describe("ChatWindow", () => {
  it("asks visitors for their name and email, starts the chat with the website's origin and keeps the token for the visit", async () => {
    const token = crypto.randomUUID();
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith("/sessions") && init?.method === "POST") return json({ sessionId: "s1", token }, 201);
      return json({ id: "s1", state: "Active", agentName: "Omar", endedBy: null, messages: [chatMessage(1, "Visitor", "My payment failed."), chatMessage(2, "System", "", "accepted")] });
    });
    vi.stubGlobal("fetch", fetchMock);
    renderIn("en", <ChatWindow widget={widget} locale="en" signedIn={false} origin="https://www.entity.example.gov" />);
    expect(screen.getByText("Hello, how can we help?")).toBeInTheDocument();
    const start = screen.getByRole("button", { name: "Start chat" });
    expect(start).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Your name"), { target: { value: "Sara" } });
    fireEvent.change(screen.getByLabelText("Your email address"), { target: { value: "sara@visitors.example.org" } });
    fireEvent.change(screen.getByLabelText("How can we help?"), { target: { value: "My payment failed." } });
    fireEvent.click(start);
    await waitFor(() => expect(screen.getByText("You are chatting with Omar.")).toBeInTheDocument());
    expect(screen.getByText("Omar joined the chat.")).toBeInTheDocument();

    const body = JSON.parse(fetchMock.mock.calls[0]![1]!.body as string);
    expect(body).toMatchObject({ name: "Sara", email: "sara@visitors.example.org", origin: "https://www.entity.example.gov", language: "en" });
    const reload = fetchMock.mock.calls.find(([url]) => url === "/bff/api/v1/chat/sessions/s1")!;
    expect((reload[1]!.headers as Record<string, string>)["X-Chat-Token"]).toBe(token);
    expect(JSON.parse(sessionStorage.getItem(`zelvo-chat:${widget.id}`)!)).toEqual({ sessionId: "s1", token });
  });

  it("offers to leave a message when nobody is online, and signed-in people give no details", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => json({ number: "INC-000042" }, 201));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("ar", <ChatWindow widget={{ ...widget, online: false }} locale="ar" signedIn csrfToken="csrf-2" />);
    expect(screen.queryByLabelText("الاسم")).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("رسالتكم"), { target: { value: "يُرجى الاتصال بي بشأن الترخيص." } });
    fireEvent.click(screen.getByRole("button", { name: "إرسال" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("INC-000042"));
    expect(fetchMock.mock.calls[0]![0]).toBe(`/bff/api/v1/chat/widgets/${widget.id}/offline-messages`);
  });

  it("merges messages by sequence without duplicates", () => {
    const merged = mergeMessages([chatMessage(1, "Visitor", "a"), chatMessage(3, "Agent", "c")], [chatMessage(2, "Agent", "b"), chatMessage(3, "Agent", "c")]);
    expect(merged.map((m) => m.sequence)).toEqual([1, 2, 3]);
  });
});

describe("MailboxForm", () => {
  it("shows the fields of the chosen mail service and sends the credential only when typed", async () => {
    const saved: Partial<Mailbox> = { id: "b1", name: "Support" };
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => json(saved, 201));
    vi.stubGlobal("fetch", fetchMock);
    const onSaved = vi.fn();
    renderIn("en", <MailboxForm mailbox={null} csrfToken="c" categories={[]} groups={[]} onSaved={onSaved} />);
    expect(screen.getByLabelText("IMAP server")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Mail service"), { target: { value: "Microsoft365" } });
    expect(screen.queryByLabelText("IMAP server")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Directory (tenant) ID")).toBeInTheDocument();
    expect(screen.getByLabelText("Client secret")).toHaveAttribute("type", "password");
    fireEvent.change(screen.getByLabelText("Mail service"), { target: { value: "Gmail" } });
    expect(screen.getByLabelText("Service account key (JSON)").tagName).toBe("TEXTAREA");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Support" } });
    fireEvent.change(screen.getByLabelText("Email address"), { target: { value: "support@entity.example.gov" } });
    fireEvent.click(screen.getByLabelText("Only people who already have an account"));
    fireEvent.click(screen.getByRole("button", { name: "Add mailbox" }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    const body = JSON.parse(fetchMock.mock.calls[0]![1]!.body as string);
    expect(body).toMatchObject({ provider: "Gmail", auth: "ServiceAccount", newSenders: "KnownUsers", address: "support@entity.example.gov" });
    expect(body.credential).toBeUndefined();
  });
});

describe("ChatWidgetsPanel", () => {
  const stored: ChatWidget = {
    id: "0195f000-0000-7000-8000-000000000002",
    name: { en: "Help", ar: "المساعدة" },
    greeting: { en: null, ar: null },
    groupId: "g1",
    categoryId: null,
    allowedOrigins: ["https://www.entity.example.gov"],
    onPortal: true,
    active: true,
    virtualAgent: false,
  };

  it("gives the code to embed a widget on the websites it lists", () => {
    renderIn("ar", <ChatWidgetsPanel initial={[stored]} csrfToken="c" appUrl="https://zelvo.entity.example.gov/" categories={[]} groups={[]} />);
    expect(screen.getByText(embedCode("https://zelvo.entity.example.gov/", stored, "ar"))).toBeInTheDocument();
    expect(embedCode("https://zelvo.entity.example.gov/", stored, "en")).toBe(
      `<script src="https://zelvo.entity.example.gov/chat-widget.js" data-widget="${stored.id}" data-language="en" async></script>`,
    );
  });
});

describe("visitor paths of live chat", () => {
  const id = "0195f000-0000-7000-8000-000000000003";
  it.each([
    ["GET", ["v1", "chat", "portal-widget"], true],
    ["GET", ["v1", "chat", "widgets", id, "public"], true],
    ["POST", ["v1", "chat", "widgets", id, "sessions"], true],
    ["POST", ["v1", "chat", "widgets", id, "offline-messages"], true],
    ["GET", ["v1", "chat", "sessions", id], true],
    ["POST", ["v1", "chat", "sessions", id, "messages"], true],
    ["POST", ["v1", "chat", "sessions", id, "end"], true],
    // The chat hub and watch keys are gone: chats reload through the API.
    ["POST", ["v1", "chat", "watch"], false],
    ["POST", ["v1", "chat", "hub", "negotiate"], false],
    // Agents' and administrators' paths stay closed to visitors.
    ["GET", ["v1", "chat", "queue"], false],
    ["POST", ["v1", "chat", "sessions", id, "accept"], false],
    ["GET", ["v1", "chat", "sessions", id, "agent"], false],
    ["GET", ["v1", "chat", "widgets"], false],
    ["POST", ["v1", "chat", "widgets"], false],
    ["DELETE", ["v1", "chat", "sessions", id], false],
    ["GET", ["v1", "chat", "sessions", "not-an-id"], false],
  ] as const)("%s %j → %s", (method, path, allowed) => {
    expect(isVisitorPath(method, [...path])).toBe(allowed);
  });
});
