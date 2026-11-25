import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { completeCard } from "@/lib/catalog";
import type { DateDisplayOptions } from "@/lib/format";
import { emptyConnector, formatAed, linesOf, listOf, mapOf, type Payment } from "@/lib/payments";
import { FeePaymentEditor } from "./FeePaymentEditor";
import { PaymentPanel } from "./PaymentPanel";
import { PaymentSettingsPanel } from "./PaymentSettingsPanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/my-requests",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const display: DateDisplayOptions = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" };
const choices = { services: [], categories: [], locations: [], departments: [], roles: [], groups: [] };

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const payment = (patch: Partial<Payment> = {}): Payment => ({
  id: "p1", request: { id: "r1", number: "REQ-000004", title: "Trade licence" }, service: null, payer: { id: "u1", displayName: "Mariam" },
  amountAed: 150.5, refundedAed: 0, currency: "AED", lines: [{ name: { en: "Trade licence", ar: "رخصة تجارية" }, amountAed: 100 }, { name: { en: "Urgent delivery", ar: "تسليم عاجل" }, amountAed: 50.5 }],
  timing: "OnSubmission", state: "AwaitingPayment", gateway: "Test pay", receiptNumber: null, transactionId: null, expiresAt: "2026-11-27T08:00:00Z",
  paidAt: null, createdAt: "2026-11-25T08:00:00Z", attempts: [], refunds: [], ...patch,
});

afterEach(() => vi.restoreAllMocks());

describe("payments", () => {
  it("reads and writes the connector's name = value lines and lists", () => {
    expect(mapOf("Authorization = Bearer {{secret.apiKey}}\n\nbad line\norder.ref = {{reference}}")).toEqual({ Authorization: "Bearer {{secret.apiKey}}", "order.ref": "{{reference}}" });
    expect(linesOf({ a: "1", b: "2" })).toBe("a = 1\nb = 2");
    expect(listOf("CAPTURED, SUCCESS ,")).toEqual(["CAPTURED", "SUCCESS"]);
    expect(formatAed(150.5, "ar", "arab")).toBe("١٥٠٫٥٠");
  });

  it("shows the fee and sends the requester to the gateway's page", async () => {
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ paymentId: "p1", reference: "ZPABC", redirectUrl: "https://pay.example/checkout/1" }));
    wrap(<PaymentPanel payments={[payment()]} mayPay mayRefund={false} display={display} csrfToken="csrf" />);
    expect(screen.getByText("Awaiting payment · 150.50 AED")).toBeInTheDocument();
    expect(screen.getByText("Urgent delivery: 50.50 AED")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Pay now" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith("https://pay.example/checkout/1"));
    expect(String(fetch.mock.calls[0]![0])).toContain("/v1/payments/p1/pay");
    vi.unstubAllGlobals();
  });

  it("links a paid fee to its receipt and lets finance refund it, in Arabic", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValue(json({}));
    wrap(<PaymentPanel payments={[payment({ state: "Paid", receiptNumber: "RCT-000001", paidAt: "2026-11-25T09:00:00Z" })]} mayPay={false} mayRefund display={{ ...display, locale: "ar" }} csrfToken="csrf" />, "ar");
    expect(screen.getByRole("link", { name: "الإيصال RCT-000001" })).toHaveAttribute("href", "/en/payments/p1/receipt");
    expect(screen.queryByRole("button", { name: "السداد الآن" })).toBeNull();
    fireEvent.change(screen.getByLabelText("السبب"), { target: { value: "خطأ في المبلغ" } });
    fireEvent.click(screen.getByRole("button", { name: "استرداد" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("سُجّل الاسترداد."));
    expect(JSON.parse(String(fetch.mock.calls[0]![1]!.body))).toEqual({ reason: "خطأ في المبلغ" });
  });

  it("sets when a fee is paid and an amount the answers add", () => {
    let fee = { ...completeCard({ fee: { paid: true, amountAed: 100 } } as never).fee };
    const { rerender } = wrap(<FeePaymentEditor fee={fee} gateways={[{ id: "g1", name: "AjmanPay" }]} choices={choices} onChange={(f) => { fee = f; }} />);
    fireEvent.change(screen.getByLabelText("When it is paid online"), { target: { value: "AfterApproval" } });
    expect(fee.timing).toBe("AfterApproval");
    fireEvent.click(screen.getByRole("button", { name: "Add an amount" }));
    rerender(<NextIntlClientProvider locale="en" messages={en}><FeePaymentEditor fee={fee} gateways={null} choices={choices} onChange={(f) => { fee = f; }} /></NextIntlClientProvider>);
    const rule = screen.getByTestId("fee-rule");
    fireEvent.change(within(rule).getByLabelText("Amount (AED)"), { target: { value: "25" } });
    expect(fee.rules[0]!.amountAed).toBe(25);
    expect(screen.queryByLabelText("Payment gateway")).toBeNull();
  });

  it("connects a gateway with write-only credentials and lists the API's problems", async () => {
    const credential = crypto.randomUUID();
    const fetch = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json({ code: "validation-failed", problems: [{ field: "create.url", code: "url-invalid", message: "Use an HTTPS address." }] }, 400))
      .mockResolvedValueOnce(json({ id: "g1", name: "AjmanPay", merchantId: null, mode: "Test", enabled: true, isDefault: true, connector: emptyConnector(), credentialNames: ["apiKey"], testPaymentSucceededAt: null, updatedAt: "" }, 201));
    wrap(<PaymentSettingsPanel initialSettings={{ enabled: false, unpaidExpiryHours: 72 }} initialGateways={[]} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "Connect a gateway" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "AjmanPay" } });
    fireEvent.change(screen.getByLabelText("Value of credential 1"), { target: { value: credential } });
    fireEvent.click(screen.getByRole("button", { name: "Save the gateway" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("create.url: Use an HTTPS address."));
    const sent = JSON.parse(String(fetch.mock.calls[0]![1]!.body));
    expect(sent.credentials).toEqual({ apiKey: credential });
    fireEvent.click(screen.getByRole("button", { name: "Save the gateway" }));
    await waitFor(() => expect(screen.getByTestId("gateway")).toHaveTextContent("AjmanPay"));
    expect(screen.getByTestId("gateway")).toHaveTextContent("Sandbox (test)");
    expect(screen.getByRole("button", { name: "Go live" })).toBeDisabled();
  });
});
