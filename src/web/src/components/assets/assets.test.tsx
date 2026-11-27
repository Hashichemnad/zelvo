import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { daysUntil, formatDay, type Asset, type AssetSummary, type PersonAsset, type SoftwareLicense } from "@/lib/assets";
import { sectionsFor } from "@/lib/settings-sections";
import { AssetDetail } from "./AssetDetail";
import { AssetScanner } from "./AssetScanner";
import { AssetTable } from "./AssetTable";
import { LicensesPanel } from "./LicensesPanel";
import { PersonAssets } from "./PersonAssets";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
  useRouter: () => ({ push: vi.fn() }),
}));

afterEach(() => vi.unstubAllGlobals());

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const id = () => crypto.randomUUID();
const display = (locale: "en" | "ar" = "en") => ({ locale, calendar: "gregory" as const, digits: (locale === "ar" ? "arab" : "latn") as "arab" | "latn", timeZone: "Asia/Dubai" });
const summary = (tag: string): AssetSummary => ({
  id: id(), tag, number: "CI-000042", name: "LT-0042", class: { id: id(), key: "laptop", family: "Hardware", path: { en: "Hardware › Laptop", ar: "الأجهزة › حاسوب محمول" } },
  state: "Assigned", itemStatus: "Active", assignedTo: { id: id(), displayName: "Fatima" }, warrantyEndsOn: "2026-12-15",
});

describe("asset dates", () => {
  it("counts days to an end date and shows it without a time in either language", () => {
    expect(daysUntil("2026-12-01", new Date(Date.UTC(2026, 10, 27)))).toBe(4);
    expect(daysUntil("2026-11-20", new Date(Date.UTC(2026, 10, 27)))).toBe(-7);
    expect(formatDay("2026-12-01", "en", "gregory", "latn")).toBe("1 December 2026");
    expect(formatDay("2026-12-01", "ar", "gregory", "arab")).toContain("٢٠٢٦");
  });
});

describe("AssetScanner", () => {
  it("finds an asset by the tag read from a picture of its label", async () => {
    const asset = summary("AST-000123");
    const fetchMock = vi.fn(async () => ok(asset));
    vi.stubGlobal("fetch", fetchMock);
    const onFound = vi.fn();
    const decode = vi.fn(async () => "ast-000123");
    const { container } = withIntl(<AssetScanner onFound={onFound} decode={decode} />);
    const input = container.querySelector("input[type=file]") as HTMLInputElement;
    expect(input.getAttribute("capture")).toBe("environment");
    fireEvent.change(input, { target: { files: [new File(["x"], "label.jpg", { type: "image/jpeg" })] } });
    await waitFor(() => expect(onFound).toHaveBeenCalledWith(asset));
    expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/assets/lookup?tag=AST-000123", expect.anything());
    expect(screen.getByText("Found AST-000123: LT-0042.")).toBeInTheDocument();
  });

  it("says so when a picture has no readable code, and lets the tag be typed instead, in Arabic", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ok({ code: "asset-not-found" }, 404)));
    const { container } = withIntl(<AssetScanner onFound={vi.fn()} decode={async () => null} />, "ar");
    fireEvent.change(container.querySelector("input[type=file]") as HTMLInputElement, { target: { files: [new File(["x"], "blur.jpg")] } });
    expect(await screen.findByText(/تعذرت قراءة رمز QR/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("أو اكتبوا الوسم"), { target: { value: "nope-1" } });
    fireEvent.click(screen.getByRole("button", { name: "بحث" }));
    expect(await screen.findByText("لا يوجد أصل بالوسم NOPE-1.")).toBeInTheDocument();
  });
});

describe("AssetTable", () => {
  it("asks the worker for a label sheet of the chosen assets", async () => {
    const one = summary("AST-000001");
    const fetchMock = vi.fn(async () => ok({ id: id(), status: "Failed" }, 202));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AssetTable items={[one, summary("AST-000002")]} csrfToken="csrf" display={display()} warningDays={30} />);
    expect(screen.getByText("Choose assets to print their labels.")).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("Choose AST-000001"));
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Large" } });
    fireEvent.click(screen.getByTestId("export-pdf"));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url] = fetchMock.mock.calls[0] as unknown as [string];
    expect(url).toContain("/bff/api/v1/assets/labels/exports?");
    expect(url).toContain(`ids=${one.id}`);
    expect(url).toContain("layout=Large");
  });
});

describe("LicensesPanel", () => {
  it("shows a shortfall in words beside what is owned and used", () => {
    const license: SoftwareLicense = {
      id: id(), name: "Microsoft 365 Apps", publisher: "Microsoft", products: [{ key: "microsoft|m365", name: "Microsoft 365 Apps" }], metric: "Device", quantity: 2,
      startsOn: null, endsOn: null, supplier: null, reference: null, costAed: null, notes: null, owner: null, devices: 3, people: 2, used: 3, shortfall: 1, spare: 0, etag: "\"1\"",
    };
    withIntl(<LicensesPanel initial={[license]} canManage={false} csrfToken="csrf" display={display()} warningDays={30} />);
    expect(screen.getByText("Short by one")).toBeInTheDocument();
    expect(screen.getByText("3 devices, 2 people")).toBeInTheDocument();
  });
});

describe("AssetDetail", () => {
  it("moves an asset to the next state with its ETag and names the person when it is assigned", async () => {
    const asset: Asset = {
      ...summary("AST-000009"), state: "InStock", nextStates: ["Assigned", "InRepair", "Retired"], assignedTo: null, assignedAt: null, request: null, purchaseDate: "2026-01-10",
      purchaseCost: 4200, supplier: "Fictional Laptops", orderNumber: "PO-1", notes: null, contracts: [], licenses: [], createdAt: "2026-01-10T00:00:00Z", updatedAt: "2026-01-10T00:00:00Z",
      etag: "\"7\"",
    };
    const person = { id: id(), displayName: "Omar" };
    const fetchMock = vi.fn(async (url: string) =>
      url.includes("/v1/users") ? ok({ items: [person], page: 1, pageSize: 10, totalCount: 1 }) : ok({ ...asset, state: "Assigned", assignedTo: person, nextStates: ["InStock"] }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AssetDetail initial={asset} canManage csrfToken="csrf" display={display()} warningDays={30} />);
    fireEvent.click(screen.getByLabelText("Assigned"));
    fireEvent.change(screen.getByLabelText("Assign to"), { target: { value: "Omar" } });
    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    fireEvent.click(await screen.findByRole("button", { name: "Omar" }));
    fireEvent.click(screen.getByRole("button", { name: "Change the state" }));
    expect(await screen.findByText("The state was changed.")).toBeInTheDocument();
    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith("/state")) as unknown as [string, RequestInit];
    expect(JSON.parse(String(call[1].body))).toMatchObject({ state: "Assigned", assignedToId: person.id });
    expect(new Headers(call[1].headers).get("If-Match")).toBe("\"7\"");
  });
});

describe("PersonAssets", () => {
  it("lists a person's assets with their warranty and tickets for staff, in Arabic", () => {
    const assets: PersonAsset[] = [{ id: id(), tag: "AST-000001", number: "CI-000001", name: "LT-0042", classPath: { en: "Laptop", ar: "حاسوب محمول" }, state: "Assigned", assignedAt: null,
      warrantyEndsOn: "2026-12-15", ticketCount: 2 }];
    withIntl(<PersonAssets assets={assets} display={display("ar")} staff />, "ar");
    expect(screen.getByRole("link", { name: /AST-000001/ })).toHaveAttribute("href", `/assets/${assets[0]!.id}`);
    expect(screen.getByText("مرتبط بتذكرتين")).toBeInTheDocument();
    expect(screen.getByText(/الضمان حتى/)).toBeInTheDocument();
  });
});

describe("Settings → Assets", () => {
  it("is for holders of assets.manage", () => {
    expect(sectionsFor(["assets.manage"]).map((s) => s.key)).toEqual(["assets"]);
    expect(sectionsFor(["assets.read"]).map((s) => s.key)).toEqual([]);
  });
});

describe("labels and the scanner's library", () => {
  // The QR code Zelvo.Tickets' encoder makes for AST-000123 (BarcodeTests checks the same rows).
  const rows = [
    "111111100111101111111", "100000101001001000001", "101110100110001011101", "101110100010101011101", "101110101011101011101", "100000100111001000001",
    "111111101010101111111", "000000000110000000000", "101010100110100010010", "101100001001010011000", "011101110101011011011", "001101010011110100000",
    "111011100101010011101", "000000001100010010010", "111111100000100010111", "100000100010010101011", "101110101100110010000", "101110100001010010110",
    "101110101011010011101", "100000100001110101010", "111111101101001011011",
  ];

  it("reads the tag back from the QR code on a label", async () => {
    const { BinaryBitmap, HybridBinarizer, QRCodeReader, RGBLuminanceSource } = await import("@zxing/library");
    const scale = 4;
    const quiet = 4;
    const size = (rows.length + quiet * 2) * scale;
    const pixels = new Uint8ClampedArray(size * size).fill(255);
    rows.forEach((row, r) => [...row].forEach((v, c) => {
      if (v !== "1") return;
      for (let y = 0; y < scale; y++) for (let x = 0; x < scale; x++) pixels[((r + quiet) * scale + y) * size + (c + quiet) * scale + x] = 0;
    }));
    const result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(pixels, size, size))));
    expect(result.getText()).toBe("AST-000123");
  });
});
