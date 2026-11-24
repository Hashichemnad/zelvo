import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { AssignmentRule, Group, GroupSummary, PermissionCatalog, Role, Skill, TreeNode } from "@/lib/directory";
import { incident } from "../incidents/fixtures";
import { AssignmentPicker } from "../incidents/AssignmentPicker";
import { AssignmentRulesPanel, RuleForm } from "./AssignmentRulesPanel";
import { GroupDetailPanel } from "./GroupDetailPanel";
import { RoleEditForm } from "./RoleEditForm";
import { TreeEditor } from "./TreeEditor";
import { UserEditForm } from "./UserEditForm";

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
const bodyOf = (call: unknown) => JSON.parse(((call as [string, RequestInit])[1].body as string) ?? "null");
const urlOf = (call: unknown) => (call as [string, RequestInit])[0];
const methodOf = (call: unknown) => (call as [string, RequestInit])[1]?.method ?? "GET";

const node = (id: string, name: string, overrides: Partial<TreeNode> = {}): TreeNode => ({
  id,
  parentId: null,
  depth: 1,
  position: 0,
  name: { en: name, ar: null, missing: ["ar"] },
  path: { en: name, ar: null },
  active: true,
  selectable: true,
  childCount: 0,
  ...overrides,
});

const hardware = node("c1", "Hardware", { childCount: 1 });
const laptop = node("c2", "Laptop", { parentId: "c1", depth: 2, path: { en: "Hardware › Laptop", ar: null }, childCount: 1 });
const battery = node("c3", "Battery", { parentId: "c2", depth: 3, path: { en: "Hardware › Laptop › Battery", ar: null } });
const software = node("c4", "Software", { position: 1 });

describe("TreeEditor", () => {
  it("shows the tree and offers children only down to the third level", () => {
    withIntl(<TreeEditor kind="categories" initial={[hardware, laptop, battery, software]} csrfToken="c" canManage />);

    expect(screen.getAllByTestId("tree-node")).toHaveLength(4);
    expect(screen.getByRole("button", { name: "Add below Hardware" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add below Laptop" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add below Battery" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Move Hardware up" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move Software down" })).toBeDisabled();
  });

  it("adds a child in both languages and reloads the tree", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(ok(node("c5", "Printer", { parentId: "c1", depth: 2 }), 201))
      .mockResolvedValueOnce(ok([hardware, node("c5", "Printer", { parentId: "c1", depth: 2 })]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<TreeEditor kind="categories" initial={[hardware]} csrfToken="csrf-9" canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Add below Hardware" }));
    const form = screen.getByRole("form", { name: "Name of the new entry below Hardware" });
    fireEvent.change(within(form).getByLabelText("In English"), { target: { value: " Printer " } });
    fireEvent.change(within(form).getByLabelText("In Arabic"), { target: { value: "طابعة" } });
    fireEvent.click(within(form).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getAllByTestId("tree-node")).toHaveLength(2));
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/categories");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ name: { en: "Printer", ar: "طابعة" }, parentId: "c1" });
    expect(new Headers((fetchMock.mock.calls[0] as [string, RequestInit])[1].headers).get("X-CSRF-Token")).toBe("csrf-9");
    expect(urlOf(fetchMock.mock.calls[1])).toBe("/bff/api/v1/categories?includeInactive=true");
  });

  it("moves a node by its position and explains why a node in use cannot be deleted", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(ok(software)).mockResolvedValueOnce(ok([hardware, software])).mockResolvedValueOnce(problem(409, "in-use"));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<TreeEditor kind="locations" initial={[hardware, software]} csrfToken="c" canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Move Software up" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/locations/c4");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ position: 0 });

    fireEvent.click(screen.getByRole("button", { name: "Delete Hardware" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("It is still in use, so it cannot be deleted."));
  });

  it("is read-only without directory.manage and works in Arabic", () => {
    withIntl(<TreeEditor kind="categories" initial={[{ ...hardware, name: { en: "Hardware", ar: "الأجهزة" } }]} csrfToken="c" canManage={false} />, "ar");

    expect(screen.getByText("الأجهزة")).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});

const groups: GroupSummary[] = [
  { id: "g1", name: { en: "Service desk", ar: "مكتب الخدمة" }, description: null, active: true, memberCount: 2, leadCount: 1 },
  { id: "g2", name: { en: "Network team", ar: null }, description: null, active: true, memberCount: 1, leadCount: 0 },
];
const skills: Skill[] = [{ id: "s1", name: { en: "Networking", ar: "الشبكات" }, active: true, userCount: 1 }];
const choices = { categories: [hardware, laptop], locations: [], groups, skills };

const rule = (id: string, name: string, position: number, overrides: Partial<AssignmentRule> = {}): AssignmentRule => ({
  id,
  position,
  name: { en: name, ar: null },
  active: true,
  category: null,
  location: null,
  language: null,
  group: { id: "g1", name: groups[0]!.name },
  method: "GroupQueue",
  skill: null,
  ...overrides,
});

describe("RuleForm", () => {
  it("asks for a skill only for round-robin and sends the rule", () => {
    const onSubmit = vi.fn();
    withIntl(<RuleForm rule={null} choices={choices} onSubmit={onSubmit} onCancel={() => {}} busy={false} />);
    const form = screen.getByTestId("rule-form");

    expect(within(form).queryByLabelText("Only agents with the skill")).not.toBeInTheDocument();
    fireEvent.change(within(form).getByLabelText("In English"), { target: { value: "Laptops" } });
    fireEvent.change(within(form).getByLabelText("Category"), { target: { value: "c2" } });
    fireEvent.change(within(form).getByLabelText("Requester's language"), { target: { value: "ar" } });
    fireEvent.change(within(form).getByLabelText("Group"), { target: { value: "g2" } });
    fireEvent.change(within(form).getByLabelText("How"), { target: { value: "RoundRobin" } });
    fireEvent.change(within(form).getByLabelText("Only agents with the skill"), { target: { value: "s1" } });
    expect(within(form).getByRole("option", { name: "Hardware › Laptop" })).toBeInTheDocument();
    fireEvent.click(within(form).getByRole("button", { name: "Save rule" }));

    expect(onSubmit).toHaveBeenCalledWith({
      name: { en: "Laptops", ar: null },
      active: true,
      categoryId: "c2",
      locationId: null,
      language: "ar",
      groupId: "g2",
      method: "RoundRobin",
      skillId: "s1",
    });
  });

  it("drops the skill when the method goes back to the group queue", () => {
    const onSubmit = vi.fn();
    withIntl(<RuleForm rule={rule("r1", "Net", 0, { method: "RoundRobin", skill: { id: "s1", name: skills[0]!.name } })} choices={choices} onSubmit={onSubmit} onCancel={() => {}} busy={false} />);

    fireEvent.change(screen.getByLabelText("How"), { target: { value: "GroupQueue" } });
    expect(screen.queryByLabelText("Only agents with the skill")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save rule" }));
    expect(onSubmit.mock.calls[0]![0]).toMatchObject({ method: "GroupQueue", skillId: null, groupId: "g1" });
  });
});

describe("AssignmentRulesPanel", () => {
  const rules = [rule("r1", "First", 0), rule("r2", "Second", 1, { method: "RoundRobin" })];

  it("reorders rules with the whole order and shows the answer", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok([{ ...rules[1], position: 0 }, { ...rules[0], position: 1 }]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AssignmentRulesPanel initialRules={rules} initialDefault={{ groupId: null, method: "GroupQueue" }} choices={choices} csrfToken="c" canManage />);

    expect(screen.getByRole("button", { name: "Move First up" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Move Second up" }));

    await waitFor(() => expect(within(screen.getByTestId("assignment-rules")).getAllByRole("listitem")[0]).toHaveTextContent("Second"));
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/assignment-rules/order");
    expect(methodOf(fetchMock.mock.calls[0])).toBe("PUT");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ ids: ["r2", "r1"] });
    expect(screen.getByText(/Service desk \(round-robin\)/)).toBeInTheDocument();
  });

  it("tries the rules without assigning anything", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ rule: { id: "r2", name: { en: "Second", ar: null } }, group: { id: "g1", name: groups[0]!.name }, method: "RoundRobin", assignee: { id: "u1", displayName: "Hamad" } }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<AssignmentRulesPanel initialRules={rules} initialDefault={{ groupId: "g1", method: "GroupQueue" }} choices={choices} csrfToken="c" canManage={false} />);

    expect(screen.queryByRole("button", { name: "Add a rule" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try" }));

    await waitFor(() => expect(screen.getByTestId("rule-test-result")).toHaveTextContent("Second sends it to Service desk: Hamad."));
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ categoryId: null, locationId: null, language: null });
  });
});

const group: Group = {
  ...groups[0]!,
  members: [
    { userId: "lead", displayName: "Lead Laila", isLead: true, availableForAssignment: true, languages: ["en", "ar"], skills: [], status: "Active", lastAssignedAt: null },
    { userId: "agent", displayName: "Agent Hamad", isLead: false, availableForAssignment: true, languages: ["ar"], skills: [{ id: "s1", name: skills[0]!.name }], status: "Active", lastAssignedAt: null },
  ],
};

describe("GroupDetailPanel", () => {
  it("lets a team lead manage members who are not leads, but not leads or the group", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(ok({})).mockResolvedValueOnce(ok({ ...group, members: [group.members[0], { ...group.members[1], availableForAssignment: false }] }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<GroupDetailPanel initial={group} csrfToken="c" canManage={false} meId="lead" />);

    expect(screen.queryByRole("heading", { name: "Details" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Lead Laila is a team lead")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove Lead Laila" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Agent Hamad" })).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText("Agent Hamad is available for assignment"));
    await waitFor(() => expect(screen.getByLabelText("Agent Hamad is available for assignment")).not.toBeChecked());
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/users/agent/availability");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ available: false });
  });

  it("is read-only for other members, and administrators change the lead flag", async () => {
    const { unmount } = withIntl(<GroupDetailPanel initial={group} csrfToken="c" canManage={false} meId="agent" />);
    expect(screen.queryByRole("button", { name: /Remove/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("search")).not.toBeInTheDocument();
    unmount();

    const fetchMock = vi.fn().mockImplementation(async () => ok(group));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<GroupDetailPanel initial={group} csrfToken="c" canManage meId="admin" />);
    fireEvent.click(screen.getByLabelText("Agent Hamad is a team lead"));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/groups/g1/members/agent");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ isLead: true });
  });
});

describe("UserEditForm", () => {
  const user = {
    id: "u1",
    displayName: "Hamad",
    email: "hamad@example.test",
    status: "Active" as const,
    roles: ["agent"],
    createdAt: "2026-11-20T08:00:00Z",
    skills: [],
    languages: ["ar" as const],
    availableForAssignment: true,
    groups: [{ id: "g1", name: groups[0]!.name, isLead: false }],
  };
  const roles: Role[] = [
    { id: "r1", key: "agent", name: { en: "Agent", ar: "موظف الدعم" }, isSystem: true, requiresMfa: true, permissions: [], fieldRules: [] },
    { id: "r2", key: "team-lead", name: { en: "Team lead", ar: "قائد الفريق" }, isSystem: true, requiresMfa: true, permissions: [], fieldRules: [] },
  ];

  it("saves only what changed, each through its endpoint", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => ok({}));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<UserEditForm initial={user} roles={roles} skills={skills} csrfToken="c" canManage />);

    expect(screen.queryByLabelText("Emirates ID")).not.toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("English"));
    fireEvent.click(screen.getByLabelText("Available for assignment"));
    fireEvent.click(screen.getByLabelText("Team lead"));
    fireEvent.click(screen.getByLabelText("Networking"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/users/u1");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ languages: ["ar", "en"], availableForAssignment: false });
    expect(bodyOf(fetchMock.mock.calls[1])).toEqual({ roles: ["agent", "team-lead"] });
    expect(bodyOf(fetchMock.mock.calls[2])).toEqual({ skillIds: ["s1"] });
    expect(screen.getByText("Service desk")).toBeInTheDocument();
  });

  it("shows the API's refusal and has no Save button for readers", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problem(409, "last-tenant-admin")));
    const { unmount } = withIntl(<UserEditForm initial={user} roles={roles} skills={skills} csrfToken="c" canManage />);
    fireEvent.change(screen.getByLabelText("Status"), { target: { value: "Disabled" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("at least one active tenant administrator"));
    unmount();

    withIntl(<UserEditForm initial={user} roles={null} skills={skills} csrfToken="c" canManage={false} />);
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveAttribute("readonly");
  });
});

describe("RoleEditForm", () => {
  const catalog: PermissionCatalog = {
    permissions: ["users.read", "users.manage", "incidents.read", "directory.read"],
    resources: [{ key: "users", readPermission: "users.read", editPermission: "users.manage", fields: [{ key: "email", sensitive: true, writable: false }, { key: "displayName", sensitive: false, writable: true }] }],
  };
  const role: Role = { id: "r9", key: "facilities", name: { en: "Facilities", ar: null }, isSystem: false, requiresMfa: false, permissions: ["users.read"], fieldRules: [] };

  it("groups permissions by area and saves permissions and field rules", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => ok(role));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<RoleEditForm initial={role} catalog={catalog} csrfToken="c" />);

    expect(screen.getByRole("group", { name: "Directory and assignment" })).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText(/See every incident/));
    const emailAccess = screen.getByLabelText("Access to users.email");
    expect(within(emailAccess).queryByRole("option", { name: "Can change" })).not.toBeInTheDocument();
    fireEvent.change(emailAccess, { target: { value: "Read" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ name: { en: "Facilities", ar: null }, requiresMfa: false });
    expect(bodyOf(fetchMock.mock.calls[1])).toEqual({ permissions: ["users.read", "incidents.read"] });
    expect(bodyOf(fetchMock.mock.calls[2])).toEqual({ rules: [{ resource: "users", field: "email", access: "Read" }] });
  });

  it("locks the second factor of staff roles and offers delete only for custom roles", () => {
    withIntl(<RoleEditForm initial={{ ...role, key: "agent", isSystem: true, requiresMfa: true }} catalog={catalog} csrfToken="c" />);
    expect(screen.getByLabelText("Needs a second factor")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Delete role" })).not.toBeInTheDocument();
  });
});

describe("AssignmentPicker", () => {
  it("lists only the group's members once a group is chosen and saves both", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok([{ id: "m1", displayName: "Member Mona" }]));
    vi.stubGlobal("fetch", fetchMock);
    const onSave = vi.fn();
    withIntl(
      <AssignmentPicker incident={{ ...incident, group: null }} groups={groups} allAssignees={[{ id: "a1", displayName: "Any Ali" }]} busy={false} onSave={onSave} />,
    );

    expect(screen.getByRole("option", { name: "Any Ali" })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Group"), { target: { value: "g2" } });
    await waitFor(() => expect(screen.getByRole("option", { name: "Member Mona" })).toBeInTheDocument());
    expect(screen.queryByRole("option", { name: "Any Ali" })).not.toBeInTheDocument();
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/incidents/assignees?groupId=g2");

    fireEvent.change(screen.getByLabelText("Assignee"), { target: { value: "m1" } });
    fireEvent.click(screen.getByRole("button", { name: "Assign" }));
    expect(onSave).toHaveBeenCalledWith("g2", "m1");
  });

  it("leaves the incident in the group queue without an assignee", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok([])));
    const onSave = vi.fn();
    withIntl(<AssignmentPicker incident={{ ...incident, assignee: null, group: { id: "g1", name: groups[0]!.name } }} groups={groups} allAssignees={[]} busy={false} onSave={onSave} />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Assign" })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: "Assign" }));
    expect(onSave).toHaveBeenCalledWith("g1", null);
  });
});
