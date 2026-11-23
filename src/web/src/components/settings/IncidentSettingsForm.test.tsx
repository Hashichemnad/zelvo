import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import en from "../../../messages/en.json";
import { settings } from "../incidents/fixtures";
import { AttachmentSettingsForm } from "./AttachmentSettingsForm";
import { IncidentSettingsForm } from "./IncidentSettingsForm";

afterEach(() => vi.unstubAllGlobals());

const wrap = (node: React.ReactNode) => render(<NextIntlClientProvider locale="en" messages={en}>{node}</NextIntlClientProvider>);

describe("IncidentSettingsForm", () => {
  it("saves the matrix, reasons, prefix and auto-close period", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(settings), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<IncidentSettingsForm initial={settings} csrfToken="c" />);

    fireEvent.change(screen.getByLabelText("Priority for impact 3 and urgency 3"), { target: { value: "4" } });
    fireEvent.change(screen.getByLabelText("Incident number prefix"), { target: { value: "hd" } });
    fireEvent.change(screen.getByLabelText("Close resolved incidents after (days)"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Remove reason old-reason" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The incident settings are saved."));
    const body = JSON.parse((fetchMock.mock.calls[0] as [string, RequestInit])[1].body as string);
    expect(body.priorityMatrix.matrix[2]).toEqual([3, 4, 4]);
    expect(body.numberPrefixes).toEqual({ Incident: "HD" });
    expect(body.autoCloseDays).toBe(3);
    expect(body.onHoldReasons.map((r: { key: string }) => r.key)).toEqual(["awaiting-requester"]);
  });

  it("explains that a reason in use cannot be removed", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: "on-hold-reason-in-use" }), { status: 409 })));
    wrap(<IncidentSettingsForm initial={settings} csrfToken="c" />);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Make it inactive instead."));
  });
});

describe("AttachmentSettingsForm", () => {
  it("saves the size limit and accepted types and says whether files are scanned", async () => {
    const initial = { maxSizeMb: 25, allowedExtensions: ["pdf", "png"], availableExtensions: ["pdf", "png", "zip"], scanningEnabled: true, updatedAt: null };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...initial, maxSizeMb: 10, allowedExtensions: ["pdf", "zip"] }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AttachmentSettingsForm initial={initial} csrfToken="c" />);

    expect(screen.getByText(/checked for malware before anyone can open it/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Largest file (MB)"), { target: { value: "10" } });
    fireEvent.click(screen.getByLabelText(".png"));
    fireEvent.click(screen.getByLabelText(".zip"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The attachment settings are saved."));
    expect(JSON.parse((fetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ maxSizeMb: 10, allowedExtensions: ["pdf", "zip"] });
  });
});
