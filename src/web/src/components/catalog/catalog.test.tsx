import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { completeCard, uploadFieldOf, type CatalogBrowse, type PublishedService, type ServiceDetail, type ServiceRequest } from "@/lib/catalog";
import { CatalogBrowser } from "./CatalogBrowser";
import { RequestServiceForm } from "./RequestServiceForm";
import { ServiceEditor } from "./ServiceEditor";
import { RequestProgress } from "./ServiceRequests";
import { ServiceView } from "./ServiceView";

const push = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/catalog",
  useRouter: () => ({ push, refresh }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const id = "0190a8b2-6c1e-7d3a-9f00-1234567890ab";
const otherId = "0190a8b2-6c1e-7d3a-9f00-0000000000bb";
const choices = { services: [], catalogs: [], categories: [], locations: [], departments: [], groups: [], roles: [], forms: null };

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

function detail(patch: Partial<ServiceDetail> = {}): ServiceDetail {
  const card = completeCard({ name: { en: "New laptop", ar: "حاسوب جديد" }, days: 2 });
  return {
    id, code: "IT-LAPTOP", state: "Draft", name: { en: "New laptop", ar: "حاسوب جديد" }, parentId: null, catalogIds: [], suspendedMessage: { en: null, ar: null },
    published: null,
    working: { id: "v1", number: 1, state: "Draft", card, changeNote: null, createdBy: "Admin", lastEditedBy: "Admin", submittedBy: null, submittedAt: null, reviewedBy: null, reviewComment: null, publishedAt: null, updatedAt: "2026-11-24T08:00:00Z" },
    problems: [], files: [], actions: ["edit", "submit", "discard", "delete", "copy"], requestCount: 0, etag: "\"41\"",
    createdAt: "2026-11-24T08:00:00Z", updatedAt: "2026-11-24T08:00:00Z", publishedAt: null, retiredAt: null,
    ...patch,
  };
}

const published: PublishedService = {
  id, code: "IT-LAPTOP", state: "Active", suspendedMessage: { en: null, ar: null }, type: "Internal", externalUrl: null,
  name: { en: "New laptop", ar: "حاسوب جديد" }, summary: { en: "A laptop for work.", ar: null }, description: { en: "All about **laptops**.", ar: null },
  icon: "laptop", imageFileId: null, department: null, owner: null, categories: [], catalogs: [],
  documents: [{ key: "passportCopy", name: { en: "Passport copy", ar: "صورة الجواز" }, description: { en: null, ar: null }, required: true, fileTypes: ["pdf"], maxSizeMb: null, templateFileId: null }],
  steps: [{ name: { en: "Received", ar: "استلام" }, description: { en: null, ar: null }, stage: "Submitted" }],
  fee: { paid: true, amountAed: 150, notes: { en: null, ar: null } }, days: 3, channels: ["Portal"], audiences: ["Employees"], partners: [],
  faq: [{ question: { en: "How long?", ar: null }, answer: { en: "Three days.", ar: null } }],
  parent: null, subServices: [], related: [], prerequisites: [], hasForm: false, mayRequest: true, mayRequestOnBehalf: false, signInToRequest: false,
};

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
});

describe("ServiceEditor", () => {
  it("shows the actions the API allows and saves the draft with If-Match", async () => {
    const saved = detail({ etag: "\"42\"" });
    const fetchMock = vi.fn(() => Promise.resolve(json(saved)));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<ServiceEditor initial={detail()} choices={choices} csrfToken="csrf" />);

    const toolbar = screen.getByRole("toolbar", { name: "Service actions" });
    expect(within(toolbar).getByRole("button", { name: "Send for review" })).toBeInTheDocument();
    expect(within(toolbar).queryByRole("button", { name: "Approve and publish" })).not.toBeInTheDocument();
    expect(within(toolbar).queryByRole("button", { name: "Retire" })).not.toBeInTheDocument();

    const name = screen.getAllByLabelText("In English")[0]!;
    fireEvent.change(name, { target: { value: "New laptop or tablet" } });
    fireEvent.click(within(toolbar).getByRole("button", { name: "Save the draft" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`/bff/api/v1/catalog/services/${id}/draft`);
    expect(init.method).toBe("PUT");
    expect((init.headers as Record<string, string>)["If-Match"]).toBe("\"41\"");
    const body = JSON.parse(String(init.body)) as { card: { name: { en: string } } };
    expect(body.card.name.en).toBe("New laptop or tablet");
  });

  it("lists the problems that stop review in the page's language", () => {
    wrap(<ServiceEditor initial={detail({ problems: [{ field: "fee.amountAed", code: "fee-invalid", message: "x" }] })} choices={choices} csrfToken="csrf" />, "ar");
    const problems = screen.getByTestId("service-problems");
    expect(within(problems).getByText(/يجب أن يكون للخدمة المدفوعة مبلغ/)).toBeInTheDocument();
  });

  it("offers approval to another publisher while the card is in review, and the card is read-only", () => {
    const working = detail().working!;
    wrap(<ServiceEditor initial={detail({ state: "UnderReview", working: { ...working, state: "UnderReview" }, actions: ["approve", "sendBack"] })} choices={choices} csrfToken="csrf" />);
    expect(screen.getByRole("button", { name: "Approve and publish" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save the draft" })).not.toBeInTheDocument();
    expect(screen.getAllByLabelText("In English")[0]).toBeDisabled();
  });
});

describe("CatalogBrowser", () => {
  const data: CatalogBrowse = {
    catalogs: [{ id: otherId, key: "it", name: { en: "IT services", ar: "خدمات تقنية المعلومات" }, description: { en: null, ar: null }, serviceCount: 2 }],
    services: [
      { id, code: "IT-LAPTOP", state: "Active", type: "Internal", name: { en: "New laptop", ar: "حاسوب جديد" }, summary: { en: "A laptop", ar: null }, icon: "laptop", imageFileId: null,
        parentId: null, catalogIds: [otherId], categoryIds: [], paid: false, amountAed: null, days: 5, subServiceCount: 2 },
      { id: otherId, code: "IT-MONITOR", state: "Suspended", type: "Internal", name: { en: "Monitor", ar: null }, summary: { en: null, ar: null }, icon: null, imageFileId: null,
        parentId: null, catalogIds: [otherId], categoryIds: [], paid: true, amountAed: 100, days: null, subServiceCount: 0 },
    ],
  };

  it("shows catalogs, services with fee, days and sub-services, and suspended ones marked", () => {
    wrap(<CatalogBrowser data={data} query="" catalogId={null} locale="en" digits="latn" />);
    expect(screen.getByRole("link", { name: /IT services/ })).toHaveAttribute("href", `/en/catalog?catalog=${otherId}`);
    const tiles = screen.getAllByTestId("catalog-service");
    expect(within(tiles[0]!).getByText("Free")).toBeInTheDocument();
    expect(within(tiles[0]!).getByText("5 working days")).toBeInTheDocument();
    expect(within(tiles[0]!).getByText("2 sub-services")).toBeInTheDocument();
    expect(within(tiles[1]!).getByText("Suspended")).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Find a service" })).toBeInTheDocument();
  });

  it("works in Arabic with Arabic-Indic digits", () => {
    wrap(<CatalogBrowser data={data} query="" catalogId={otherId} locale="ar" digits="arab" />, "ar");
    expect(screen.getByRole("link", { name: /خدمات تقنية المعلومات/ })).toHaveAttribute("aria-current", "page");
    expect(screen.getByText("١٠٠ درهم")).toBeInTheDocument();
  });
});

describe("ServiceView", () => {
  it("shows the card with documents, steps, FAQ and the request button", () => {
    wrap(<ServiceView service={published} locale="en" digits="latn" signInHref="/auth/login" />);
    expect(screen.getByRole("heading", { level: 1, name: "New laptop" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Request this service" })).toHaveAttribute("href", `/en/catalog/${id}/request`);
    expect(screen.getByText("Passport copy")).toBeInTheDocument();
    expect(screen.getByText("150 AED")).toBeInTheDocument();
    expect(screen.getByText("How long?")).toBeInTheDocument();
  });

  it("asks visitors to sign in and links external services to the provider", () => {
    const { unmount } = wrap(<ServiceView service={{ ...published, mayRequest: false, signInToRequest: true }} locale="en" digits="latn" signInHref="/auth/login?x" />);
    expect(screen.getByRole("link", { name: "Sign in to request" })).toHaveAttribute("href", "/auth/login?x");
    unmount();
    wrap(<ServiceView service={{ ...published, type: "External", externalUrl: "https://provider.example/visa" }} locale="en" digits="latn" signInHref="/auth/login" />);
    expect(screen.getByRole("link", { name: /Go to the provider's site/ })).toHaveAttribute("href", "https://provider.example/visa");
  });
});

describe("RequestServiceForm", () => {
  it("uploads each document under its document field and shows the server's document errors", async () => {
    expect(uploadFieldOf("passportCopy")).toBe("documentPassportCopy");
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (url.endsWith("/form-uploads")) {
        return Promise.resolve(json({ id: otherId, fileName: "passport.pdf" }, 201));
      }
      void init;
      return Promise.resolve(json({ code: "validation-failed", documentErrors: [{ field: "documents.passportCopy", code: "file-type" }] }, 400));
    });
    vi.stubGlobal("fetch", fetchMock);
    wrap(<RequestServiceForm service={published} initialForm={null} csrfToken="csrf" locations={[]} />);
    expect(screen.queryByTestId("on-behalf")).not.toBeInTheDocument();

    // Nothing is sent while a mandatory document is missing.
    fireEvent.click(screen.getByRole("button", { name: "Send the request" }));
    expect(await screen.findByText("Upload this document.")).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();

    const input = screen.getByLabelText(/Passport copy/);
    fireEvent.change(input, { target: { files: [new File(["%PDF"], "passport.pdf", { type: "application/pdf" })] } });
    await screen.findByText("passport.pdf");
    const upload = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect((upload[1].body as FormData).get("field")).toBe("documentPassportCopy");

    fireEvent.click(screen.getByRole("button", { name: "Send the request" }));
    expect(await screen.findByText("This type of file is not accepted for this document.")).toBeInTheDocument();
    const sent = fetchMock.mock.calls[1] as unknown as [string, RequestInit];
    expect(sent[0]).toBe("/bff/api/v1/service-requests");
    expect(JSON.parse(String(sent[1].body))).toMatchObject({ serviceId: id, documents: { passportCopy: [otherId] } });
  });

  it("offers requesting for someone else only when the service allows the caller's role", () => {
    wrap(<RequestServiceForm service={{ ...published, mayRequestOnBehalf: true }} initialForm={null} csrfToken="csrf" locations={[]} />);
    const choice = screen.getByTestId("on-behalf");
    fireEvent.click(within(choice).getByRole("radio", { name: "For someone else" }));
    expect(within(choice).getByRole("searchbox", { name: "Find the person by name" })).toBeInTheDocument();
  });

  it("goes to the new request after sending it", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json({ id: otherId, number: "REQ-000001" }, 201))));
    wrap(<RequestServiceForm service={{ ...published, documents: [] }} initialForm={null} csrfToken="csrf" locations={[]} />);
    fireEvent.click(screen.getByRole("button", { name: "Send the request" }));
    await waitFor(() => expect(push).toHaveBeenCalledWith(`/my-requests/${otherId}`));
  });
});

describe("RequestProgress", () => {
  it("marks the current step of the service's journey", () => {
    const request = {
      id, number: "REQ-000001", title: "New laptop", state: "InProgress", currentStep: 1,
      steps: [
        { name: { en: "Received", ar: "استلام" }, description: { en: null, ar: null }, stage: "Submitted" },
        { name: { en: "Working", ar: "تنفيذ" }, description: { en: null, ar: null }, stage: "InProgress" },
        { name: { en: "Done", ar: "اكتمل" }, description: { en: null, ar: null }, stage: "Resolved" },
      ],
    } as unknown as ServiceRequest;
    wrap(<RequestProgress request={request} />, "ar");
    const bar = screen.getByRole("progressbar");
    expect(bar).toHaveAttribute("aria-valuenow", "50");
    expect(bar).toHaveAttribute("aria-valuetext", "الخطوة 2 من 3: تنفيذ");
    expect(screen.getByText("تنفيذ").closest("li")).toHaveAttribute("aria-current", "step");
  });
});
