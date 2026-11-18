import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { SearchStatus } from "@/lib/api";
import { SearchIndexPanel } from "./SearchIndexPanel";

const idle: SearchStatus = {
  available: true,
  documents: 1250,
  pendingUpdates: 0,
  failedUpdates: 0,
  lastError: null,
  lastErrorAt: null,
  lastIndexedAt: "2026-11-18T08:00:00Z",
  schemaVersion: 1,
  currentSchemaVersion: 1,
  rebuildRequestedAt: null,
  rebuildCompletedAt: null,
  rebuildInProgress: false,
};

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function renderPanel(status: SearchStatus, locale: "en" | "ar" = "en", digits: "latn" | "arab" = "latn") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <SearchIndexPanel initial={status} csrfToken="csrf-9" display={{ ...display, locale, digits }} />
    </NextIntlClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("SearchIndexPanel", () => {
  it("shows documents, waiting updates and the last error", () => {
    renderPanel({ ...idle, failedUpdates: 2, lastError: "article/42: mapper_parsing_exception", lastErrorAt: "2026-11-18T09:00:00Z" });

    expect(screen.getByTestId("search-state")).toHaveTextContent("Up to date");
    expect(screen.getByTestId("search-documents")).toHaveTextContent("1,250");
    expect(screen.getByTestId("search-pending")).toHaveTextContent("0");
    expect(screen.getByTestId("search-failed")).toHaveTextContent("2");
    const detail = screen.getByText("article/42: mapper_parsing_exception");
    expect(detail).toHaveAttribute("dir", "ltr");
  });

  it("shows Arabic with Arabic-Indic digits when the user chose them", () => {
    renderPanel({ ...idle, pendingUpdates: 3 }, "ar", "arab");

    expect(screen.getByTestId("search-documents")).toHaveTextContent("١٬٢٥٠");
    expect(screen.getByTestId("search-pending")).toHaveTextContent("٣");
    expect(screen.getByTestId("search-state")).toHaveTextContent("جارٍ فهرسة التغييرات");
    expect(screen.getByTestId("search-last-error")).toHaveTextContent("لا يوجد");
  });

  it("says when the search engine cannot be reached", () => {
    renderPanel({ ...idle, available: false, documents: null });

    expect(screen.getByTestId("search-state")).toHaveTextContent("The search engine is not reachable");
    expect(screen.getByTestId("search-documents")).toHaveTextContent("Unknown");
  });

  it("asks for confirmation, then starts a rebuild with the CSRF token", async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(
        url.endsWith("/rebuild")
          ? new Response(JSON.stringify({ documentsQueued: 12 }), { status: 202 })
          : new Response(JSON.stringify({ ...idle, pendingUpdates: 13, rebuildInProgress: true }), { status: 200 }),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    renderPanel(idle);

    fireEvent.click(screen.getByRole("button", { name: "Rebuild index" }));
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByText("Rebuild the search index now? This is recorded in the audit log.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Yes, rebuild the index" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Rebuild started: 12 records were sent to the index."));
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/search/rebuild");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-9");
    await waitFor(() => expect(screen.getByTestId("search-state")).toHaveTextContent("Rebuilding"));
    expect(screen.getByRole("button", { name: "Rebuild index" })).toBeDisabled();
  });

  it("can cancel the confirmation", () => {
    renderPanel(idle);

    fireEvent.click(screen.getByRole("button", { name: "Rebuild index" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.getByRole("button", { name: "Rebuild index" })).toBeEnabled();
  });

  it("reports a rebuild that could not start", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("{}", { status: 503 })));
    renderPanel(idle);

    fireEvent.click(screen.getByRole("button", { name: "Rebuild index" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, rebuild the index" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The rebuild could not be started. Please try again."));
  });
});
