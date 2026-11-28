import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { PushDevicesPanel } from "@/components/notifications/PushDevicesPanel";
import { serviceWorkerSource } from "./service-worker";
import type { PushStatus } from "./push";

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

type Listener = (event: Record<string, unknown>) => void;

/** Runs the generated service worker against a minimal `self` and returns its listeners. */
function runWorker() {
  const listeners: Record<string, Listener> = {};
  const shown: { title: string; options: Record<string, unknown> }[] = [];
  const opened: string[] = [];
  const self = {
    location: { origin: "https://help.example" },
    addEventListener: (type: string, listener: Listener) => (listeners[type] = listener),
    registration: { showNotification: (title: string, options: Record<string, unknown>) => (shown.push({ title, options }), Promise.resolve()) },
    clients: { matchAll: () => Promise.resolve([]), openWindow: (url: string) => (opened.push(url), Promise.resolve()), claim: () => Promise.resolve() },
    skipWaiting: () => Promise.resolve(),
  };
  new Function("self", "caches", serviceWorkerSource("test"))(self, {});
  return { listeners, shown, opened };
}

async function dispatch(listener: Listener, event: Record<string, unknown>) {
  let work: Promise<unknown> = Promise.resolve();
  listener({ ...event, waitUntil: (p: Promise<unknown>) => (work = p) });
  await work;
}

describe("service worker web push (task J2)", () => {
  it("shows the message in its language and direction, and opens only pages of this site", async () => {
    const { listeners, shown, opened } = runWorker();
    await dispatch(listeners.push!, {
      data: { json: () => ({ title: "بلاغ مسند إليكم", body: "INC-000123", url: "/ar/incidents/1", tag: "t1", lang: "ar", dir: "rtl" }) },
    });
    expect(shown[0]).toMatchObject({ title: "بلاغ مسند إليكم", options: { body: "INC-000123", lang: "ar", dir: "rtl", tag: "t1", data: { url: "/ar/incidents/1" } } });

    // An address of another site in a message is never opened.
    await dispatch(listeners.push!, { data: { json: () => ({ title: "x", url: "https://evil.example/", lang: "en" }) } });
    expect(shown[1]!.options.data).toEqual({ url: "/en/notifications" });
    await dispatch(listeners.push!, { data: { json: () => ({ title: "x", url: "//evil.example/", lang: "en" }) } });
    expect(shown[2]!.options.data).toEqual({ url: "/en/notifications" });

    // A message that cannot be read still shows something in English.
    await dispatch(listeners.push!, { data: { json: () => { throw new Error("bad"); } } });
    expect(shown[3]!.title).toBe("New notification");

    const close = vi.fn();
    await dispatch(listeners.notificationclick!, { notification: { close, data: shown[0]!.options.data } });
    expect(close).toHaveBeenCalled();
    expect(opened).toEqual(["https://help.example/ar/incidents/1"]);
  });
});

describe("PushDevicesPanel (task J2)", () => {
  const status: PushStatus = {
    available: true,
    publicKey: "BA",
    devices: [{ id: "d1", deviceName: "Chrome on Android", createdAt: "2026-11-20T08:00:00Z", lastSuccessAt: null }],
  };

  afterEach(() => vi.unstubAllGlobals());

  it("says so when the installation has no web push", () => {
    wrap(<PushDevicesPanel initial={{ available: false, publicKey: null, devices: [] }} csrfToken="c" />, "ar");
    expect(screen.getByTestId("push-not-available")).toHaveTextContent("الإشعارات الفورية على الأجهزة غير متاحة في هذه المنظومة");
  });

  it("lists the person's devices, removes one and sends a test notification", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(json({ devices: 1 }, 202))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(json({ ...status, devices: [] }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<PushDevicesPanel initial={status} csrfToken="csrf-9" />);

    expect(screen.getByText("Chrome on Android")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Send a test notification" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/me/push/test", expect.objectContaining({ method: "POST" })));

    fireEvent.click(screen.getByRole("button", { name: "Remove Chrome on Android" }));
    await waitFor(() => expect(screen.getByText("No device receives notifications yet.")).toBeInTheDocument());
    expect(fetchMock.mock.calls[1]![0]).toBe("/bff/api/v1/me/push/subscriptions/d1");
    expect((fetchMock.mock.calls[1]![1] as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf-9" });
  });
});
