import type { AnchorHTMLAttributes } from "react";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { Page } from "@/lib/integrations";
import type { AppNotification, NotificationDelivery, NotificationEventSettings, NotificationPreference } from "@/lib/notifications";
import { sectionsFor } from "@/lib/settings-sections";
import { DeliveryLogPanel } from "./DeliveryLogPanel";
import { NotificationBell, bellPollMilliseconds } from "./NotificationBell";
import { NotificationList } from "./NotificationList";
import { NotificationPreferencesForm } from "./NotificationPreferencesForm";
import { NotificationSettingsPanel } from "./NotificationSettingsPanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/notifications",
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function wrap(ui: React.ReactElement, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      {ui}
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function page<T>(items: T[]): Page<T> {
  return { items, page: 1, pageSize: 25, totalCount: items.length };
}

const received: AppNotification = {
  id: "0193f1c2-0000-7000-8000-000000000001",
  eventType: "incident.received",
  title: "INC-000123: we have received your incident",
  text: "We have received your incident “Printer jam”.",
  language: "en",
  linkPath: "/my-incidents/0193f1c2-0000-7000-8000-0000000000aa",
  createdAt: "2026-11-24T06:30:00Z",
  readAt: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("NotificationBell", () => {
  it("links to the notifications page and tells screen readers how many are unread", () => {
    wrap(<NotificationBell initialCount={3} locale="en" digits="latn" />);

    const bell = screen.getByTestId("notification-bell");
    expect(bell).toHaveAttribute("href", "/en/notifications");
    expect(bell).toHaveAccessibleName(/^Notifications\s*,\s*3 unread notifications$/);
    expect(screen.getByTestId("notification-count")).toHaveTextContent("3");
  });

  it("shows Arabic-Indic digits and checks the count again later", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn().mockResolvedValue(json({ count: 5 }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<NotificationBell initialCount={2} locale="ar" digits="arab" />, "ar");
    expect(screen.getByTestId("notification-count")).toHaveTextContent("٢");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(bellPollMilliseconds);
    });

    expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/me/notifications/unread-count", expect.anything());
    expect(screen.getByTestId("notification-count")).toHaveTextContent("٥");
    expect(screen.getByTestId("notification-announcement")).toHaveTextContent(/غير مقروء/);
    expect(screen.queryByRole("status")).not.toBeInTheDocument(); // Pages keep a single status message.
  });

  it("hides the count when nothing is unread", () => {
    wrap(<NotificationBell initialCount={0} locale="en" digits="latn" />);
    expect(screen.queryByTestId("notification-count")).not.toBeInTheDocument();
    expect(screen.getByTestId("notification-bell")).toHaveAccessibleName(/no unread notifications/);
  });
});

describe("NotificationList", () => {
  it("links each notification to its page in the current language and marks it read", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ count: 0 }));
    vi.stubGlobal("fetch", fetchMock);
    const changed = vi.fn();
    window.addEventListener("zelvo:notifications-changed", changed);
    wrap(<NotificationList initial={page([received])} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    const link = screen.getByRole("link", { name: received.title! });
    expect(link).toHaveAttribute("href", "/en/my-incidents/0193f1c2-0000-7000-8000-0000000000aa");
    expect(screen.getByText("Unread")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Mark as read" }));

    await waitFor(() => expect(screen.queryByText("Unread")).not.toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(`/bff/api/v1/me/notifications/${received.id}/read`);
    expect((init as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf-1" });
    expect(changed).toHaveBeenCalled();
    window.removeEventListener("zelvo:notifications-changed", changed);
  });

  it("marks a notification written in the other language with its language and direction", () => {
    const arabic: AppNotification = { ...received, id: "0193f1c2-0000-7000-8000-000000000002", title: "بلاغ رقم INC-000123: تم استلام بلاغكم", language: "ar" };
    wrap(<NotificationList initial={page([arabic])} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    const block = screen.getByRole("link", { name: arabic.title! }).closest("div");
    expect(block).toHaveAttribute("lang", "ar");
    expect(block).toHaveAttribute("dir", "rtl");
  });

  it("does not follow links outside the web app", () => {
    wrap(<NotificationList initial={page([{ ...received, linkPath: "//evil.example/x" }])} csrfToken="c" display={{ ...display, locale: "en" }} />);
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    expect(screen.getByText(received.title!)).toBeInTheDocument();
  });

  it("says so when there is nothing", () => {
    wrap(<NotificationList initial={page([])} csrfToken="c" display={{ ...display, locale: "ar" }} />, "ar");
    expect(screen.getByText("لا توجد لديكم إشعارات.")).toBeInTheDocument();
  });
});

describe("NotificationPreferencesForm", () => {
  const preferences: NotificationPreference[] = [
    { eventType: "incident.received", audience: "Requester", email: true, canChange: true, enabled: true, emailEnabled: true },
    { eventType: "incident.closed", audience: "Requester", email: true, canChange: true, enabled: false, emailEnabled: false },
    { eventType: "sla.threshold", audience: "Staff", email: true, canChange: false, enabled: true, emailEnabled: true },
  ];

  it("groups events, keeps SLA email on and saves the choices that can change", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(preferences.map((p) => (p.eventType === "incident.received" ? { ...p, email: false } : p))));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<NotificationPreferencesForm initial={preferences} csrfToken="csrf-2" />);

    expect(screen.getByRole("group", { name: "Your incidents" })).toBeInTheDocument();
    const sla = within(screen.getByRole("group", { name: "Your work" })).getByRole("checkbox");
    expect(sla).toBeDisabled();
    expect(sla).toHaveAccessibleDescription("Always sent by email; it cannot be turned off.");
    expect(screen.getByText("Your organisation has turned this notification off.")).toBeInTheDocument();

    const received = screen.getByRole("checkbox", { name: /Your incident was received/ });
    fireEvent.click(received);
    fireEvent.click(screen.getByRole("button", { name: "Save email choices" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Your email choices are saved."));
    const body = JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string);
    expect(body.events).toEqual([
      { eventType: "incident.received", email: false },
      { eventType: "incident.closed", email: true },
    ]);
  });

  it("is in Arabic for Arabic readers", () => {
    wrap(<NotificationPreferencesForm initial={preferences} csrfToken="c" />, "ar");
    expect(screen.getByRole("group", { name: "بلاغاتكم" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "حفظ خيارات البريد الإلكتروني" })).toBeInTheDocument();
  });
});

describe("NotificationSettingsPanel", () => {
  const field = (en: string, ar: string) => ({ en, ar, missing: [] });
  const event: NotificationEventSettings = {
    eventType: "incident.received",
    audience: "Requester",
    emailOptional: true,
    enabled: true,
    email: true,
    placeholders: ["recipient.name", "tenant.name", "ticket.number"],
    customised: false,
    template: null,
    default: {
      subject: field("{{ticket.number}}: we have received your incident", "بلاغ رقم {{ticket.number}}: تم استلام بلاغكم"),
      body: field("Dear {{recipient.name}},", "تحية طيبة، {{recipient.name}}"),
      inApp: field("Received", "تم الاستلام"),
      action: field("View your incident", "عرض البلاغ"),
    },
  };

  it("warns when the installation has no mail server and switches an event's email off", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ ...event, email: false }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<NotificationSettingsPanel initial={{ mailServerConfigured: false, events: [event] }} csrfToken="csrf-3" />);

    expect(screen.getByTestId("no-mail-server")).toBeInTheDocument();
    fireEvent.click(within(screen.getByTestId("event-incident.received")).getByRole("checkbox", { name: /Send email/ }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The change is saved."));
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/notifications/settings/events/incident.received");
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ enabled: true, email: false });
  });

  it("edits both languages side by side, previews the email in a sandbox and shows refused placeholders", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ language: "ar", subject: "بلاغ INC-000123", inApp: "تم", html: "<html><body>معاينة</body></html>", text: "معاينة" }))
      .mockResolvedValueOnce(json({ errors: { subject: ["Unknown placeholder {{ticket.secret}}."] } }, 400));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<NotificationSettingsPanel initial={{ mailServerConfigured: true, events: [event] }} csrfToken="csrf-3" />);

    fireEvent.click(screen.getByRole("button", { name: "Edit wording" }));
    const arabicSubject = screen.getAllByLabelText("Arabic")[0]!;
    expect(arabicSubject).toHaveAttribute("dir", "rtl");
    expect(arabicSubject).toHaveValue("بلاغ رقم {{ticket.number}}: تم استلام بلاغكم");
    expect(screen.getByText("{{ticket.number}}")).toBeInTheDocument();

    fireEvent.change(arabicSubject, { target: { value: "بلاغ {{ticket.number}}" } });
    fireEvent.click(screen.getByRole("button", { name: "Preview in Arabic" }));
    await waitFor(() => expect(screen.getByTestId("preview-subject")).toHaveTextContent("بلاغ INC-000123"));
    const frame = screen.getByTitle("Email as it will look");
    expect(frame).toHaveAttribute("sandbox", "");
    const previewBody = JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string);
    expect(previewBody.language).toBe("ar");
    expect(previewBody.template.subject.ar).toBe("بلاغ {{ticket.number}}");

    fireEvent.click(screen.getByRole("button", { name: "Save wording" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Unknown placeholder {{ticket.secret}}."));
  });
});

describe("DeliveryLogPanel", () => {
  const failed: NotificationDelivery = {
    id: 7, eventType: "incident.resolved", address: "f***@example.ae", language: "ar", subject: "بلاغ رقم INC-000123: تمت معالجة بلاغكم",
    status: "Failed", attempts: 2, manual: false, lastError: "The mail server answered 550: no such user", createdAt: "2026-11-24T06:30:00Z",
    sentAt: null, nextAttemptAt: null,
  };

  it("lists emails with masked addresses, filters by status, opens one and sends it again", async () => {
    const fetchMock = vi.fn(async (url: string) => {
      if (url.includes("/resend")) return json({ ...failed, id: 8, status: "Pending" }, 202);
      if (url.endsWith("/deliveries/7")) return json({ delivery: failed, address: "fatima@example.ae", html: "<p>x</p>", text: "x" });
      return json(page([failed]));
    });
    vi.stubGlobal("fetch", fetchMock);
    wrap(<DeliveryLogPanel initial={page([failed])} csrfToken="csrf-4" display={{ ...display, locale: "en" }} />);

    expect(screen.getByText("f***@example.ae")).toHaveAttribute("dir", "ltr");
    expect(screen.getByText(failed.subject)).toHaveAttribute("lang", "ar");
    expect(within(screen.getByRole("table")).getByText("Failed")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Failed" } });
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/notifications/deliveries?page=1&pageSize=25&status=Failed", expect.anything()));

    fireEvent.click(screen.getByRole("button", { name: "View" }));
    await waitFor(() => expect(screen.getByTestId("delivery-detail")).toHaveTextContent("Email to fatima@example.ae"));
    fireEvent.click(screen.getByRole("button", { name: "Send again" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The email will be sent again in a few seconds."));
  });
});

describe("Settings menu", () => {
  it("shows Notifications to those who may manage them", () => {
    expect(sectionsFor(["notifications.manage"]).map((s) => s.key)).toEqual(["notifications"]);
  });
});
