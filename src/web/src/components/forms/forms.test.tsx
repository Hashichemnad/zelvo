import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { useState } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { Answers, FormDetail, FormField, ResolvedField } from "@/lib/forms/types";
import { FormAnswersView } from "./FormAnswersView";
import { FormBuilder } from "./FormBuilder";
import { FormRenderer } from "./FormRenderer";

const push = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push, refresh: vi.fn() }),
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
});

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const previewMessages = { en: { forms: { renderer: en.forms.renderer } }, ar: { forms: { renderer: ar.forms.renderer } } };

const draft = (fields: FormField[] = []): FormDetail => ({
  id: "f1", key: "laptop", name: { en: "Laptop", ar: "الحاسوب" }, description: { en: null, ar: null }, state: "Draft",
  hasUnpublishedChanges: true, publishedVersion: null, publishedAt: null, usedBy: [], updatedAt: "2026-11-24T08:00:00Z",
  draft: { elements: fields.map((field) => ({ type: "Field", field })) }, draftFields: fields.map((field) => ({ field })), problems: [], etag: 7,
});

describe("FormBuilder", () => {
  it("adds fields from the palette, edits their properties and rules, saves and publishes", async () => {
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const body = init?.body ? JSON.parse(init.body as string) : null;
      if (url.endsWith("/draft")) return Promise.resolve(ok({ ...draft(body.schema.elements.map((e: { field: FormField }) => e.field)), etag: 8 }));
      return Promise.resolve(ok({ ...draft(), state: "Published", publishedVersion: 1, hasUnpublishedChanges: false, etag: 9 }));
    });
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<FormBuilder kind="form" initial={draft()} csrfToken="csrf" fieldSets={[]} locations={[]} previewMessages={previewMessages} />);

    expect(screen.getByText("No fields yet. Add one from the field types, or drag it here.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add Checkbox" }));
    fireEvent.click(screen.getByRole("button", { name: "Add Dropdown" }));
    expect(screen.getAllByTestId("canvas-item")).toHaveLength(2);

    // The new dropdown is selected: give it a key, a bilingual label and a rule on the checkbox.
    const properties = screen.getByRole("heading", { name: "Properties" }).parentElement!;
    fireEvent.change(within(properties).getByLabelText("Key"), { target: { value: "monitorSize" } });
    fireEvent.change(within(properties).getAllByLabelText("In English")[0]!, { target: { value: "Monitor size" } });
    fireEvent.change(within(properties).getAllByLabelText("In Arabic")[0]!, { target: { value: "حجم الشاشة" } });
    fireEvent.click(within(properties).getByRole("button", { name: "Add a rule" }));
    const rule = within(properties).getByRole("group", { name: "Rule 1" });
    expect(within(rule).getByLabelText("Comparison")).toHaveValue("Checked");

    // Move the dropdown above the checkbox from the keyboard-friendly buttons, then back.
    fireEvent.click(screen.getByRole("button", { name: "Move Monitor size up" }));
    expect(within(screen.getAllByTestId("canvas-item")[0]!).getByText("Monitor size")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Move Monitor size down" }));

    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => expect(screen.getByText("Draft saved.")).toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/forms/f1/draft");
    expect(new Headers(init.headers).get("If-Match")).toBe('"7"');
    const schema = JSON.parse(init.body as string).schema;
    expect(schema.elements.map((e: { field: FormField }) => e.field.key)).toEqual(["checkbox1", "monitorSize"]);
    expect(schema.elements[1].field).toMatchObject({
      label: { en: "Monitor size", ar: "حجم الشاشة" },
      rules: [{ action: "Show", match: "All", conditions: [{ field: "checkbox1", operator: "Checked", value: null }] }],
    });

    fireEvent.click(screen.getByRole("button", { name: "Publish" }));
    await waitFor(() => expect(screen.getByText("Published. New tickets use this version.")).toBeInTheDocument());
    expect(new Headers((fetchMock.mock.calls[1] as [string, RequestInit])[1].headers).get("If-Match")).toBe('"8"');
  });

  it("previews the form on mobile and in Arabic, with rules working", () => {
    const fields: FormField[] = [
      { key: "addMonitor", type: "Checkbox", label: { en: "Add a monitor", ar: "إضافة شاشة" } },
      { key: "size", type: "Text", label: { en: "Size", ar: "الحجم" }, rules: [{ action: "Show", match: "All", conditions: [{ field: "addMonitor", operator: "Checked" }] }] },
    ];
    withIntl(<FormBuilder kind="form" initial={draft(fields)} csrfToken="csrf" fieldSets={[]} locations={[]} previewMessages={previewMessages} />);

    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    fireEvent.click(screen.getByRole("button", { name: "Mobile" }));
    fireEvent.click(screen.getByRole("button", { name: "العربية" }));
    const preview = screen.getByTestId("preview-mobile");
    expect(within(preview).getByLabelText("إضافة شاشة")).toBeInTheDocument();
    expect(within(preview).queryByLabelText("الحجم")).not.toBeInTheDocument();
    fireEvent.click(within(preview).getByLabelText("إضافة شاشة"));
    expect(within(preview).getByLabelText("الحجم")).toBeInTheDocument();
    expect(preview.querySelector("[dir='rtl']")).not.toBeNull();
  });

  it("lists the problems that stop publishing, in the page's language", async () => {
    const problems = [{ field: "size", code: "options-required", message: "x" }];
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response(JSON.stringify({ status: 409, code: "form-has-problems", problems }), { status: 409 }))));
    withIntl(<FormBuilder kind="form" initial={{ ...draft(), hasUnpublishedChanges: false }} csrfToken="csrf" fieldSets={[]} locations={[]} previewMessages={previewMessages} />, "ar");

    fireEvent.click(screen.getByRole("button", { name: "نشر" }));
    expect(await screen.findByText("تحتاج القائمة المنسدلة وأزرار الاختيار إلى خيار واحد على الأقل.")).toBeInTheDocument();
    expect(screen.getByText("يُرجى معالجة المشكلات أدناه قبل النشر.")).toBeInTheDocument();
  });
});

function Filled({ fields, defaults = {}, csrfToken }: { fields: ResolvedField[]; defaults?: Answers; csrfToken?: string }) {
  const [answers, setAnswers] = useState<Answers>({});
  return (
    <>
      <FormRenderer fields={fields} answers={answers} onChange={setAnswers} defaults={defaults} errors={[]} contentLocale="en" locations={[]} csrfToken={csrfToken} idPrefix="t" />
      <output data-testid="answers">{JSON.stringify(answers)}</output>
    </>
  );
}

describe("FormRenderer", () => {
  it("fills tables, typed signatures and several choices", () => {
    withIntl(
      <Filled
        fields={[
          { field: { key: "items", type: "Table", label: { en: "Items" }, columns: [{ key: "name", type: "Text", label: { en: "Item" } }, { key: "qty", type: "Number", label: { en: "Quantity" } }] } },
          { field: { key: "sign", type: "Signature", label: { en: "Signature" } } },
          { field: { key: "tags", type: "Dropdown", multiple: true, label: { en: "Tags" }, options: [{ value: "a", label: { en: "Alpha" } }, { value: "b", label: { en: "Beta" } }] } },
        ]}
      />,
    );
    expect(screen.getByRole("img", { name: /Signature drawing area/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add a row" }));
    fireEvent.change(screen.getByLabelText("Item row 1"), { target: { value: "Mouse" } });
    fireEvent.change(screen.getByLabelText("Quantity row 1"), { target: { value: "2" } });
    fireEvent.click(screen.getByLabelText("Type my name"));
    fireEvent.change(screen.getByLabelText("Full name"), { target: { value: "Sara Ali" } });
    fireEvent.click(screen.getByLabelText("I confirm that this is my signature."));
    fireEvent.click(screen.getByLabelText("Beta"));

    expect(JSON.parse(screen.getByTestId("answers").textContent!)).toEqual({
      items: [{ name: "Mouse", qty: 2 }], sign: { typedName: "Sara Ali", confirmed: true }, tags: ["b"],
    });
  });

  it("uploads files for their field and finds people by name", async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(url.includes("/forms/people")
        ? ok([{ id: "u1", displayName: "Mariam Saeed" }])
        : ok({ id: "up1", field: "evidence", fileName: "receipt.pdf", contentType: "application/pdf", size: 10, scanStatus: "Pending" }, 201)));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(
      <Filled csrfToken="csrf" fields={[{ field: { key: "evidence", type: "File", label: { en: "Evidence" } } }, { field: { key: "who", type: "User", label: { en: "Who is affected" } } }]} />,
    );

    const input = screen.getByLabelText("Evidence");
    fireEvent.change(input, { target: { files: [new File(["%PDF-1.7"], "receipt.pdf", { type: "application/pdf" })] } });
    await waitFor(() => expect(JSON.parse(screen.getByTestId("answers").textContent!)).toMatchObject({ evidence: ["up1"] }));
    const upload = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(upload[0]).toBe("/bff/api/v1/form-uploads");
    expect((upload[1].body as FormData).get("field")).toBe("evidence");
    expect(screen.getByText("receipt.pdf")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Who is affected"), { target: { value: "Mar" } });
    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    fireEvent.click(await screen.findByRole("button", { name: "Choose Mariam Saeed" }));
    expect(JSON.parse(screen.getByTestId("answers").textContent!)).toMatchObject({ who: "u1" });
    expect(screen.getByText("Mariam Saeed")).toBeInTheDocument();
  });
});

describe("FormAnswersView", () => {
  it("shows answers with option labels, people, files and tables in the page's language", () => {
    withIntl(
      <FormAnswersView
        attachmentBase="/bff/api/v1/incidents/i1/attachments"
        view={{
          form: { id: "f1", key: "k", name: { en: "Laptop", ar: "الحاسوب" }, description: { en: null, ar: null }, versionId: "v", versionNumber: 2 },
          fields: [
            { field: { key: "size", type: "Dropdown", label: { en: "Size", ar: "الحجم" }, options: [{ value: "27", label: { en: "27 inch", ar: "27 بوصة" } }] } },
            { field: { key: "ok", type: "Checkbox", label: { en: "Agree", ar: "موافق" } } },
            { field: { key: "file", type: "File", label: { en: "File", ar: "الملف" } } },
            { field: { key: "boss", type: "User", label: { en: "Manager", ar: "المدير" } } },
          ],
          answers: { size: "27", ok: false, file: ["a1"], boss: "u1" },
          references: { users: { u1: "Hamad" }, locations: {}, files: { a1: { id: "a1", fileName: "photo.png", contentType: "image/png", size: 1, scanStatus: "Clean" } } },
        }}
      />,
      "ar",
    );
    expect(screen.getByRole("heading", { name: "الإجابات على \"الحاسوب\"" })).toBeInTheDocument();
    expect(screen.getByText("27 بوصة")).toBeInTheDocument();
    expect(screen.getByText("لا")).toBeInTheDocument();
    expect(screen.getByText("Hamad")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "photo.png" })).toHaveAttribute("href", "/bff/api/v1/incidents/i1/attachments/a1/content");
  });
});
