import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { inLanguage, levelName } from "@/lib/incidents";
import { AttachmentsPanel } from "./AttachmentsPanel";
import { attachment, display, incident, settings } from "./fixtures";
import { IncidentHistory } from "./IncidentHistory";
import { IncidentWorkPanel } from "./IncidentWorkPanel";
import { ReportIncidentForm } from "./ReportIncidentForm";

const push = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push, refresh }),
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
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
const problem = (status: number, code: string) => new Response(JSON.stringify({ status, code }), { status });

describe("bilingual helpers", () => {
  it("fall back to the other language and say so", () => {
    expect(inLanguage({ en: "High", ar: "عالٍ" }, "ar")).toEqual({ value: "عالٍ" });
    expect(inLanguage({ en: "P1", ar: null }, "ar")).toEqual({ value: "P1", lang: "en", dir: "ltr" });
    expect(levelName(settings.priorityMatrix.impacts, 3, "ar")).toBe("منخفض");
  });
});

describe("ReportIncidentForm", () => {
  it("lets a requester report an incident and opens it", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ ...incident, id: "new-id" }, 201));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<ReportIncidentForm csrfToken="csrf-1" settings={settings} basePath="/my-incidents" />);

    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "  Printer jams  " } });
    fireEvent.change(screen.getByLabelText("What happened?"), { target: { value: "Paper jam" } });
    fireEvent.click(screen.getByRole("button", { name: "Report incident" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/my-incidents/new-id"));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/incidents");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
    expect(JSON.parse(init.body as string)).toEqual({ title: "Printer jams", description: "Paper jam" });
    expect(screen.queryByLabelText("Impact")).not.toBeInTheDocument();
  });

  it("asks for a title before calling the API", () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<ReportIncidentForm csrfToken="x" settings={settings} basePath="/my-incidents" />);

    fireEvent.click(screen.getByRole("button", { name: "Report incident" }));

    expect(screen.getByRole("status")).toHaveTextContent("Enter a title of at least 3 characters.");
    expect(screen.getByLabelText("Title")).toHaveAttribute("aria-invalid", "true");
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("lets staff choose requester, channel, impact and urgency and previews the priority", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ ...incident, id: "staff-id" }, 201));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(
      <ReportIncidentForm
        csrfToken="x"
        settings={settings}
        basePath="/incidents"
        staff={{ requesters: [{ id: "r1", displayName: "Mariam" }], assignees: [{ id: "a1", displayName: "Hamad" }] }}
      />,
    );

    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Email down" } });
    fireEvent.change(screen.getByLabelText("Requester"), { target: { value: "r1" } });
    fireEvent.change(screen.getByLabelText("Impact"), { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Urgency"), { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Assign to"), { target: { value: "a1" } });
    expect(screen.getByText("Priority: P1")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Log incident" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/incidents/staff-id"));
    expect(JSON.parse((fetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toMatchObject({
      requesterId: "r1",
      channel: "Phone",
      impact: 1,
      urgency: 1,
      assigneeId: "a1",
    });
  });

  it("is labelled in Arabic", () => {
    withIntl(<ReportIncidentForm csrfToken="x" settings={settings} basePath="/my-incidents" />, "ar");
    expect(screen.getByLabelText("العنوان")).toHaveAttribute("dir", "auto");
    expect(screen.getByRole("button", { name: "تقديم البلاغ" })).toBeInTheDocument();
  });
});

describe("IncidentWorkPanel", () => {
  const permissions = ["incidents.read", "incidents.edit", "incidents.assign"];

  it("puts an incident on hold with an active reason only", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok(incident));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<IncidentWorkPanel incident={incident} settings={settings} csrfToken="c" permissions={permissions} assignees={[]} />);

    fireEvent.click(screen.getByRole("button", { name: "Put on hold" }));
    const reason = screen.getByLabelText("On-hold reason");
    expect(within(reason).queryByText("Old reason")).not.toBeInTheDocument();
    fireEvent.change(reason, { target: { value: "awaiting-requester" } });
    fireEvent.click(screen.getAllByRole("button", { name: "Put on hold" })[1]!);

    await waitFor(() => expect(refresh).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`/bff/api/v1/incidents/${incident.id}/transitions`);
    expect(JSON.parse(init.body as string)).toEqual({ state: "OnHold", onHoldReason: "awaiting-requester" });
  });

  it("explains a refused change in words", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "assignee-required")));
    withIntl(<IncidentWorkPanel incident={incident} settings={settings} csrfToken="c" permissions={permissions} assignees={[]} />);

    fireEvent.click(screen.getByRole("button", { name: "Start work" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Assign the incident before starting work on it."));
  });

  it("offers no actions to a reader and none at all on a closed incident", () => {
    withIntl(<IncidentWorkPanel incident={{ ...incident, nextStates: [] }} settings={settings} csrfToken="c" permissions={["incidents.read"]} assignees={null} />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});

describe("AttachmentsPanel", () => {
  it("offers download only for files scanned clean and shows each scan status", () => {
    withIntl(
      <AttachmentsPanel
        incidentId={incident.id}
        attachments={[attachment(), attachment({ id: "p", fileName: "log.txt", scanStatus: "Pending", downloadable: false }), attachment({ id: "i", fileName: "bad.zip", scanStatus: "Infected", downloadable: false, canDelete: false })]}
        csrfToken="c"
        canUpload
        limits={{ maxSizeMb: 25, allowedExtensions: ["png", "txt"] }}
        display={display}
      />,
    );

    expect(screen.getByRole("link", { name: "Download screenshot.png" })).toHaveAttribute("href", `/bff/api/v1/incidents/${incident.id}/attachments/${attachment().id}/content`);
    expect(screen.queryByRole("link", { name: "Download log.txt" })).not.toBeInTheDocument();
    expect(screen.getByText("Being checked for malware")).toBeInTheDocument();
    expect(screen.getByText("Rejected: malware found")).toBeInTheDocument();
    expect(screen.getByText("Up to 25 MB. Accepted types: png, txt.")).toBeInTheDocument();
    expect(screen.getByLabelText("File to attach")).toHaveAttribute("accept", ".png,.txt");
  });

  it("uploads a file with the CSRF token and explains refusals", async () => {
    const fetchMock = vi.fn().mockResolvedValue(problem(422, "attachment-type-not-allowed"));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AttachmentsPanel incidentId={incident.id} attachments={[]} csrfToken="csrf-9" canUpload limits={null} display={display} />);

    fireEvent.change(screen.getByLabelText("File to attach"), { target: { files: [new File(["MZ"], "tool.exe")] } });
    fireEvent.click(screen.getByRole("button", { name: "Attach file" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("This type of file is not accepted."));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`/bff/api/v1/incidents/${incident.id}/attachments`);
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-9");
    expect((init.body as FormData).get("file")).toBeInstanceOf(File);
  });
});

describe("IncidentHistory", () => {
  it("shows changes with translated states and level names", () => {
    withIntl(
      <IncidentHistory
        settings={settings}
        display={display}
        entries={[
          { sequence: 1, occurredAt: "2026-11-22T08:00:00Z", category: "Create", action: "ticket.created", entityType: "ticket", actorType: "User", actorName: "Hamad", changes: { title: { old: null, new: "Printer" } }, details: null },
          { sequence: 2, occurredAt: "2026-11-22T08:05:00Z", category: "Change", action: "ticket.updated", entityType: "ticket", actorType: "User", actorName: "Hamad", changes: { state: { old: "Assigned", new: "InProgress" }, updatedAt: { old: "a", new: "b" } }, details: null },
          { sequence: 3, occurredAt: "2026-11-22T08:06:00Z", category: "Change", action: "incident.updated", entityType: "incident", actorType: "User", actorName: "Hamad", changes: { priority: { old: 5, new: 1 } }, details: null },
          { sequence: 4, occurredAt: "2026-11-22T08:07:00Z", category: "Security", action: "tickets.attachment.infected", entityType: "ticket", actorType: "System", actorName: "Malware scan", changes: null, details: { fileName: "bad.zip" } },
        ]}
      />,
    );

    expect(screen.getByText("State: Assigned → In progress")).toBeInTheDocument();
    expect(screen.getByText("Priority: P5 → P1")).toBeInTheDocument();
    expect(screen.getByText(/Rejected bad.zip: malware found/)).toBeInTheDocument();
    expect(screen.queryByText(/updatedAt/)).not.toBeInTheDocument();
  });
});
