import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { useEffect, useState, type AnchorHTMLAttributes, type ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { completeCard, type RequestTask, type ServiceFulfilment } from "@/lib/catalog";
import type { DateDisplayOptions } from "@/lib/format";
import { FulfilmentEditor } from "./FulfilmentEditor";
import { TaskTable } from "./RequestTasks";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/tasks",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const choices = {
  services: [], categories: [], locations: [], departments: [], roles: [],
  groups: [
    { id: "g1", name: { en: "IT", ar: "تقنية المعلومات" }, description: null, active: true, memberCount: 1, leadCount: 0 },
    { id: "g2", name: { en: "Facilities", ar: "المرافق" }, description: null, active: true, memberCount: 1, leadCount: 0 },
  ],
};
const display: DateDisplayOptions = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" };

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

let latest: ServiceFulfilment | null = null;

function Editor() {
  const [value, setValue] = useState(completeCard({}).fulfilment);
  useEffect(() => {
    latest = value;
  }, [value]);
  return <FulfilmentEditor value={value} choices={choices} onChange={setValue} />;
}

const task = (patch: Partial<RequestTask> = {}): RequestTask => ({
  id: "t1", requestId: "r1", requestNumber: "REQ-000007", requestTitle: "New joiner", key: "laptop", name: { en: "Prepare the laptop", ar: "تجهيز الحاسوب" },
  description: { en: null, ar: null }, position: 0, stage: 0, state: "Open", group: { id: "g1", name: { en: "IT", ar: "تقنية المعلومات" } }, assignee: null,
  targetMinutes: 60, dueAt: "2026-11-24T10:00:00Z", overdue: true, notes: null, openedAt: "2026-11-24T09:00:00Z", startedAt: null, completedAt: null, completedBy: null,
  mayWork: true, mayAssign: false, etag: "\"3\"", ...patch,
});

afterEach(() => vi.restoreAllMocks());

describe("FulfilmentEditor", () => {
  it("sets the first group, adds tasks in sequence and in parallel, and the service SLA", () => {
    wrap(<Editor />);
    fireEvent.change(screen.getByLabelText("Group that receives new requests"), { target: { value: "g1" } });
    fireEvent.click(screen.getByRole("button", { name: "Add a task" }));
    fireEvent.click(screen.getByRole("button", { name: "Add a task" }));
    const tasks = screen.getAllByTestId("fulfilment-task");
    // The first task has no "parallel" choice; the second has.
    expect(within(tasks[0]!).queryByLabelText("Runs at the same time as the task before")).toBeNull();
    fireEvent.click(within(tasks[1]!).getByLabelText("Runs at the same time as the task before"));
    fireEvent.change(within(tasks[1]!).getByLabelText("Group"), { target: { value: "g2" } });
    fireEvent.change(screen.getByLabelText("Ownership: minutes until an agent starts work"), { target: { value: "60" } });
    fireEvent.click(screen.getByRole("button", { name: "Move task 2 up" }));
    expect(latest).toMatchObject({ groupId: "g1", ownershipMinutes: 60, completionMinutes: null });
    expect(latest!.tasks.map((t) => [t.key, t.groupId, t.parallelWithPrevious])).toEqual([["task2", "g2", true], ["task1", null, false]]);
  });

  it("adds an override by form answer, in Arabic", () => {
    wrap(<Editor />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "إضافة استثناء" }));
    const override = screen.getByTestId("group-override");
    fireEvent.change(within(override).getByLabelText("مجموعة الاستثناء 1"), { target: { value: "g2" } });
    fireEvent.change(within(override).getByLabelText("مفتاح حقل النموذج"), { target: { value: "city" } });
    fireEvent.change(within(override).getByLabelText("القيمة"), { target: { value: "أبوظبي" } });
    expect(latest!.overrides).toEqual([{ groupId: "g2", method: "GroupQueue", conditions: [{ field: "answer.city", op: "eq", value: "أبوظبي" }] }]);
  });
});

describe("TaskTable", () => {
  it("says a task is overdue in words and starts it", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(task({ state: "InProgress", overdue: false })));
    const changed = vi.fn();
    wrap(<TaskTable tasks={[task()]} display={display} csrfToken="csrf" showRequest onChanged={changed} />);
    const row = screen.getByTestId("task-row");
    expect(row).toHaveTextContent("Overdue");
    expect(within(row).getByRole("link", { name: "REQ-000007" })).toHaveAttribute("href", "/en/requests/r1");
    fireEvent.click(within(row).getByRole("button", { name: "Start Prepare the laptop" }));
    await waitFor(() => expect(changed).toHaveBeenCalled());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/request-tasks/t1/start");
  });

  it("asks for notes when marking a task done", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(task({ state: "Done", overdue: false })));
    wrap(<TaskTable tasks={[task()]} display={display} csrfToken="csrf" showRequest={false} onChanged={vi.fn()} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "تعيين تجهيز الحاسوب منجزة" }));
    fireEvent.change(screen.getByLabelText("ما تم إنجازه (اختياري)"), { target: { value: "تم." } });
    fireEvent.click(screen.getByRole("button", { name: "تعيينها منجزة" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(JSON.parse(String((fetchMock.mock.calls[0]![1] as RequestInit).body))).toEqual({ notes: "تم." });
  });
});
