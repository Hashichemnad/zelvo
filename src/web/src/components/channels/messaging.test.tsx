import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { MessagingChannel, MyNotificationChannels } from "@/lib/messaging";
import type { Comment } from "@/lib/workspace";
import { ConversationList } from "../workspace/Conversation";
import { NotificationChannelsForm } from "../notifications/NotificationChannelsForm";
import { MessagingPanel } from "./MessagingPanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/messaging",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
}));

const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function renderIn(locale: "en" | "ar", node: ReactNode) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const whatsApp: MessagingChannel = {
  id: "c1", name: "Customer care", kind: "WhatsApp", provider: "WhatsAppCloud", accountId: "1098765432", sender: "+97142345678", directoryId: null,
  credentialSet: true, credentialSetAt: "2026-11-25T08:00:00Z", appSecretSet: true,
  webhookUrl: "https://zelvo.entity.gov.ae/hooks/entity/whatsapp/c1", verifyToken: "verify-made-by-zelvo",
  gateway: null, whatsApp: { defaultTemplate: "ticket_update", templates: {}, englishCode: "en", arabicCode: "ar" },
  newSenders: "Anyone", categoryId: null, groupId: null, active: true, lastInboundAt: null, lastError: null, lastErrorAt: null,
  createdAt: "2026-11-25T08:00:00Z", updatedAt: "2026-11-25T08:00:00Z",
};

afterEach(() => vi.unstubAllGlobals());

describe("Settings → Messaging (task E3)", () => {
  it("shows each channel's webhook address and verify token, and tests it", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ ok: true, detail: "Entity +97142345678", error: null, testMessageId: null }));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("en", <MessagingPanel initial={[whatsApp]} csrfToken="csrf" categories={[]} groups={[]} display={display} />);
    expect(screen.getByRole("table", { name: "Messaging" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Open Customer care/ }));
    const detail = screen.getByTestId("messaging-detail");
    expect(within(detail).getByTestId("webhook-url")).toHaveTextContent("/hooks/entity/whatsapp/c1");
    expect(within(detail).getByText("verify-made-by-zelvo")).toBeInTheDocument();
    fireEvent.click(within(detail).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(within(detail).getByText(/The provider accepted the account/)).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/messaging/channels/c1/test");
  });

  it("adds a generic SMS gateway with its request template, never showing secrets again", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ ...whatsApp, id: "c2", name: "Operator", kind: "Sms", provider: "GenericHttp", verifyToken: null }, 201));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("ar", <MessagingPanel initial={[]} csrfToken="csrf" categories={[]} groups={[]} display={{ ...display, locale: "ar" }} />);
    fireEvent.click(screen.getByRole("button", { name: "إضافة بوابة رسائل نصية" }));
    const form = screen.getByTestId("messaging-form");
    fireEvent.change(within(form).getByLabelText("الاسم"), { target: { value: "Operator" } });
    fireEvent.click(within(form).getByLabelText("بوابة أخرى (HTTP)"));
    fireEvent.change(within(form).getByLabelText("العنوان"), { target: { value: "https://sms.operator.example.ae/send" } });
    fireEvent.change(within(form).getByLabelText("سر البوابة"), { target: { value: "s3" } });
    fireEvent.click(within(form).getByRole("button", { name: "حفظ" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const body = JSON.parse(fetchMock.mock.calls[0]![1].body as string);
    expect(body).toMatchObject({ kind: "Sms", provider: "GenericHttp", credential: "s3", gateway: { url: "https://sms.operator.example.ae/send", auth: "Bearer" } });
    expect(body.gateway.bodyTemplate).toContain("{{to}}");
  });
});

describe("the conversation with WhatsApp, SMS and Teams", () => {
  const base: Comment = { id: "m1", ticketId: "t1", visibility: "Public", byStaff: false, language: "ar", body: "", author: { id: "u1", displayName: "Layla" }, mentions: [], cannedResponseId: null, createdAt: "2026-11-25T08:00:00Z" };
  it("shows voice notes with their transcript and how a staff reply went out", () => {
    const comments: Comment[] = [
      { ...base, body: "", source: "WhatsApp", attachments: [{ id: "a1", fileName: "voice-note.ogg" }], voiceNote: { attachmentId: "a1", status: "Done", transcript: "الطابعة لا تعمل", language: "ar" } },
      { ...base, id: "m2", body: "", source: "WhatsApp", voiceNote: { attachmentId: "a2", status: "Off", transcript: null, language: null } },
      { ...base, id: "m3", language: "en", byStaff: true, body: "On the way.", source: "Web", messageDelivery: "Read" },
    ];
    renderIn("en", <ConversationList comments={comments} display={display} staff />);
    const items = screen.getAllByTestId("comment");
    expect(within(items[0]!).getByText("By WhatsApp")).toBeInTheDocument();
    expect(within(items[0]!).getByText("الطابعة لا تعمل")).toHaveAttribute("lang", "ar");
    expect(within(items[1]!).getByText(/Transcription is off/)).toBeInTheDocument();
    expect(within(items[2]!).getByTestId("message-delivery")).toHaveTextContent("Message read");
  });
});

describe("Account → Notifications channels", () => {
  const offered: MyNotificationChannels = { whatsApp: false, sms: false, teams: true, phone: null, whatsAppOffered: true, smsOffered: false, teamsOffered: true, teamsLinked: false };

  it("offers only the organisation's channels and asks for a phone number first", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ code: "validation" }, 400)));
    renderIn("en", <NotificationChannelsForm initial={offered} csrfToken="csrf" />);
    expect(screen.queryByText("Text message (SMS)")).not.toBeInTheDocument();
    expect(screen.getAllByText("Add your mobile number to your profile first.")).toHaveLength(1);
    fireEvent.click(screen.getByRole("checkbox", { name: /WhatsApp/ }));
    await waitFor(() => expect(screen.getAllByText("Add your mobile number to your profile first.")).toHaveLength(2));
  });

  it("is hidden when the organisation has no messaging channel", () => {
    const { container } = renderIn("ar", <NotificationChannelsForm initial={{ ...offered, whatsAppOffered: false, teamsOffered: false }} csrfToken="csrf" />);
    expect(container).toBeEmptyDOMElement();
  });
});
