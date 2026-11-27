import "fake-indexeddb/auto";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { CameraButtons, cameraKinds } from "@/components/pwa/CameraButtons";
import { needsIosGuidance } from "@/components/pwa/AppRuntime";
import { OfflineView } from "@/components/pwa/OfflineView";
import { qrPath } from "@/components/settings/InstallableApp";
import type { PublicBranding } from "../branding";
import { appIconPath, buildManifest, shortName } from "./manifest";
import {
  claimStore,
  clearOfflineData,
  listOutbox,
  listTickets,
  maxTickets,
  queueWork,
  rememberTicket,
  sendQueuedWork,
} from "./offline-store";
import { serviceWorkerSource } from "./service-worker";

const branding: PublicBranding = {
  productName: { en: "Municipality Service Desk", ar: "مكتب خدمات البلدية" },
  lightColour: "#7a1f3d",
  darkColour: "#f2b8c6",
  font: "cairo",
  fontFamily: "Cairo",
  images: {},
  publishedAt: "2026-11-27T08:00:00Z",
};

const ticket = (id: string) => ({
  id,
  number: `INC-${id}`,
  title: `Printer ${id}`,
  state: "In progress",
  team: null,
  description: "The printer on floor 2 jams.",
  href: `/en/my-incidents/${id}`,
  messages: [{ author: "Service desk", text: "We are on it.", at: "27 Nov 2026" }],
  updated: "27 Nov 2026",
});

describe("web app manifest", () => {
  it("uses the tenant's name, colour and app icon in the page's language", () => {
    const app = { offlineData: true, icons: { "any-192": "/api/v1/branding/app-icons/any-192?v=abc123", "any-512": "/api/v1/branding/app-icons/any-512?v=abc123", "maskable-192": "/api/v1/branding/app-icons/maskable-192?v=abc123", "maskable-512": "/api/v1/branding/app-icons/maskable-512?v=abc123", "apple-180": "/api/v1/branding/app-icons/apple-180?v=abc123" } };
    const manifest = buildManifest({ locale: "ar", tenant: "demo-municipality", branding, app, description: "وصف" });
    expect(manifest.name).toBe("مكتب خدمات البلدية");
    expect(manifest.dir).toBe("rtl");
    expect(manifest.lang).toBe("ar");
    expect(manifest.start_url).toBe("/ar?source=app");
    expect(manifest.id).toBe("/");
    expect(manifest.theme_color).toBe("#7a1f3d");
    expect(manifest.icons.map((i) => i.purpose)).toEqual(["any", "any", "maskable", "maskable"]);
    expect(manifest.icons[0]!.src).toBe("/app-icons/demo-municipality/any-192?v=abc123");
  });

  it("falls back to Zelvo's name, colour and icons", () => {
    const manifest = buildManifest({ locale: "en", tenant: "default", branding: null, app: null, description: "d" });
    expect(manifest.name).toBe("Zelvo");
    expect(manifest.dir).toBe("ltr");
    expect(manifest.theme_color).toBe("#0f5c8a");
    expect(manifest.icons[3]!.src).toBe("/zelvo-icons/maskable-512.png");
    expect(appIconPath("default", "apple-180", null)).toBe("/zelvo-icons/apple-180.png");
  });

  it("keeps short names short for the home screen", () => {
    expect(shortName("Zelvo")).toBe("Zelvo");
    expect(shortName("Municipality Service Desk")).toBe("Municipality");
    expect([...shortName("مكتبخدماتالبلديةالمركزي")].length).toBe(12);
  });
});

describe("service worker", () => {
  it("never caches API answers and serves the offline page only for navigations", () => {
    const source = serviceWorkerSource("1.2.3-abc");
    expect(source).toContain('"zelvo-shell-1.2.3-abc"');
    expect(source).toContain('credentials: "omit"');
    expect(source).not.toContain("/bff/api/v1");
    expect(() => new Function(source)).not.toThrow();
  });
});

describe("offline store", () => {
  beforeEach(async () => {
    await clearOfflineData();
  });

  it("keeps the newest tickets of one person and clears them for anyone else", async () => {
    await claimStore("person-a", true);
    for (let i = 0; i < maxTickets + 3; i++) {
      await rememberTicket(ticket(String(i)), new Date(Date.UTC(2026, 10, 27, 8, i)));
    }
    const kept = await listTickets();
    expect(kept).toHaveLength(maxTickets);
    expect(kept[0]!.id).toBe(String(maxTickets + 2));

    await claimStore("person-a", true);
    expect(await listTickets()).toHaveLength(maxTickets);
    await claimStore("person-b", true);
    expect(await listTickets()).toHaveLength(0);
  });

  it("clears everything after signing out or when the tenant turns offline data off", async () => {
    await claimStore("person-a", true);
    await rememberTicket(ticket("1"));
    await queueWork({ kind: "reply", path: "/v1/incidents/1/comments", body: { body: "hi" }, summary: "hi" });
    await claimStore(null, true);
    expect(await listTickets()).toHaveLength(0);
    expect(await listOutbox()).toHaveLength(0);

    await claimStore("person-a", true);
    await rememberTicket(ticket("1"));
    await claimStore("person-a", false);
    expect(await listTickets()).toHaveLength(0);
  });

  it("sends unsent work in order, keeps refused items and stops when the connection is gone", async () => {
    await claimStore("person-a", true);
    await queueWork({ kind: "incident", path: "/v1/incidents", body: { title: "one" }, summary: "one" }, new Date(1));
    await queueWork({ kind: "incident", path: "/v1/incidents", body: { title: "two" }, summary: "two" }, new Date(2));
    await queueWork({ kind: "incident", path: "/v1/incidents", body: { title: "three" }, summary: "three" }, new Date(3));
    const sent: string[] = [];
    const result = await sendQueuedWork(async (item) => {
      if (item.summary === "two") throw new Error("refused 400");
      if (item.summary === "three") throw new TypeError("Failed to fetch");
      sent.push(item.summary);
    });
    expect(sent).toEqual(["one"]);
    expect(result).toEqual({ sent: 1, failed: 1 });
    const left = await listOutbox();
    expect(left.map((i) => [i.summary, Boolean(i.failed)])).toEqual([["two", true], ["three", false]]);
  });
});

describe("offline page", () => {
  beforeEach(async () => {
    await clearOfflineData();
  });

  it("shows the requester's kept tickets read-only and the unsent work, in Arabic too", async () => {
    await claimStore("person-a", true);
    await rememberTicket(ticket("7"));
    await queueWork({ kind: "reply", path: "/v1/incidents/7/comments", body: { body: "شكرًا" }, summary: "شكرًا", ticketNumber: "INC-7" });
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <OfflineView />
      </NextIntlClientProvider>,
    );
    expect(await screen.findByText("INC-7")).toBeInTheDocument();
    expect(screen.getByText("رد على INC-7")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "حذف" }));
    await act(async () => undefined);
    expect(await listOutbox()).toHaveLength(0);
  });
});

describe("camera buttons", () => {
  it("offer photos and videos only where those types are accepted", () => {
    expect(cameraKinds(["pdf", "jpg"])).toEqual({ photo: true, video: false });
    expect(cameraKinds(["mp4"])).toEqual({ photo: false, video: true });
    expect(cameraKinds([])).toEqual({ photo: true, video: true });
  });

  it("show on touch devices and hand the camera's file to the same upload", () => {
    const matchMedia = window.matchMedia;
    window.matchMedia = ((query: string) => ({ matches: query === "(pointer: coarse)", media: query, addEventListener: () => undefined, removeEventListener: () => undefined })) as unknown as typeof window.matchMedia;
    const onFile = vi.fn();
    const { container } = render(
      <NextIntlClientProvider locale="en" messages={en}>
        <CameraButtons allowed={["jpg", "png"]} onFile={onFile} />
      </NextIntlClientProvider>,
    );
    expect(screen.getByRole("button", { name: "Take a photo" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record a video" })).toBeNull();
    const input = container.querySelector<HTMLInputElement>('input[capture="environment"]')!;
    expect(input.accept).toBe("image/*");
    const file = new File(["x"], "photo.jpg", { type: "image/jpeg" });
    fireEvent.change(input, { target: { files: [file] } });
    expect(onFile).toHaveBeenCalledWith(file);
    window.matchMedia = matchMedia;
  });
});

describe("install guidance", () => {
  it("tells iPhone Safari users how to add the app, not other browsers", () => {
    const safari = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    expect(needsIosGuidance(safari, false)).toBe(true);
    expect(needsIosGuidance(safari, true)).toBe(false);
    expect(needsIosGuidance(safari.replace("Version/18.0", "CriOS/130.0"), false)).toBe(false);
    expect(needsIosGuidance("Mozilla/5.0 (Linux; Android 15) Chrome/130.0 Mobile Safari/537.36", false)).toBe(false);
  });
});

describe("QR code", () => {
  it("encodes the installation's address", () => {
    const { size, path } = qrPath("https://support.entity.gov.ae");
    expect(size).toBeGreaterThanOrEqual(25);
    expect(path).toMatch(/^M\d+ \d+h1v1h-1z/);
  });
});
