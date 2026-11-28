# Low-code app builder (task K3, module 20)

Covers the low-code app builder of module 20 of `docs/requirements.md`: customers build their own processes (for example HR or facilities) on the same platform, group them as **apps**, keep **simple data lists** for dropdowns, and move apps between installations as **JSON packages**. Product owner decision 20 of 2026-11-26: an imported app is **validated, previewed and approved by an administrator before it is active**, and audited; no custom code (scripts are module 28, Phase 3).

## Code

| Piece | Where |
| --- | --- |
| Tables `apps`, `data_lists`, `data_list_items`, `app_imports` (row-level security; migration `AppBuilder`); `record_types.app_id`, `roles.app_id` | `src/api/Zelvo.Data/Apps`, `ZelvoDbContext.Apps.cs` |
| Data lists as form option sources (`FormField.dataList`), filled in when a published form is put together | `src/api/Zelvo.Tickets/Forms/DataListOptions.cs`, `FormResolver.PublishedAsync`, `FormDefinitionCheck` |
| Package format `zelvo-app/1`, references, export, check and preview, installation | `src/api/Zelvo.Api/Apps/AppPackage.cs`, `AppPackages.cs` |
| API: `/apps`, `/data-lists`, `/app-imports` | `src/api/Zelvo.Api/Apps/AppBuilderEndpoints.cs` |
| Missing translations of apps, entries, data lists and items | `src/api/Zelvo.Api/Apps/AppSetup.cs` |
| Demo seeder `k3-app-builder` (the HR app) | `src/api/Zelvo.Api/Demo/DemoAppBuilderSeeder.cs` |
| Settings → Apps, an app, an import's review, Settings → Data lists, `/apps`, `/apps/[key]`, the "Options from" picker of the form builder | `src/web/src/app/[locale]/{apps,settings/apps,settings/data-lists}`, `src/web/src/components/apps` |

## An app

`apps` (Settings → Apps, `apps.manage`): key (never changes), bilingual name and description, one of Zelvo's icons (the service catalog's set) and:

- **What it groups:** custom record types (K2), data lists and custom roles, each in at most one app (`app_id`). The **forms** bound to its record types and the **workflow rules** on them come with them.
- **List views:** saved filters of a record type's list (state, open only, assigned to the viewer or to nobody). They open `/records/{type}` with those filters.
- **Menu entries:** links on the app's page (`/apps/{key}`) to a record type's records (optionally a list view) or to the form to raise one; shown to people with the type's read or edit permission.
- **Portal entries:** a bilingual label and description that start a record of a type; shown to people who may raise records of the type.
- **Switched on or off:** off hides the entries, makes its record types take no new records and stops its rules; on restores exactly what was switched off (`apps.suspended`). Both audited (`apps.activated`, `apps.deactivated`).

The header shows "Apps" once an active app has entries the person may use. Entries and states are checked against the app's own record types. At most 50 apps per tenant.

## Data lists

`data_lists` and `data_list_items` (Settings → Data lists, `apps.manage`; readable with `forms.manage` for the form builder): key, bilingual name and description, up to 10 extra columns (key, bilingual label) and up to 1,000 items (value, bilingual label, column values, offered or not) in order. Saving replaces the items: items given are added or updated, items left out are **no longer offered, never deleted**, so answers that chose them keep them. At most 200 lists per tenant.

**As dropdown sources:** a dropdown or radio field of a form (C1) chooses **Options from** a data list instead of its own options (`dataList` on the field). Publishing checks the list exists (`data-list-unknown`). When a published form is put together for filling in (`FormResolver.PublishedAsync`), the list's active items become the field's options, so the ticket keeps them with its copy of the fields and answers are checked against them like any options (C# and TypeScript unchanged). Rules on such fields are not checked against options at publish.

## Packages (`zelvo-app/1`)

`GET /apps/{key}/export` (audited `apps.exported` with the SHA-256) writes one JSON document:

| Part | What |
| --- | --- |
| `app` | key, names, icon, list views, menu entries, portal entries |
| `dataLists` | with columns and items |
| `recordTypes` | key, names, prefix, states with categories, portal entry, default priority, `grantRoles` (roles that hold its permissions, except tenant administrators, who always get them) |
| `roles` | the app's custom roles with **only the permissions of the package's record types** |
| `forms`, `formBindings` | the published forms bound to its record types, fields of field sets included, without the data lists' items and without people, places or items as defaults; bindings per category |
| `rules` | the workflow rules on its record types (drafts left out) |
| `references` | things the package does not carry: categories and locations by their **path of names**, groups, departments and customer groups by **name**, services by **code**, people by **email**, REST connectors by **name**, configuration item classes by **key**; inside the package they keep the exporting installation's ids |

Packages hold no code and no secrets.

## Importing: validated, previewed, approved (decided 2026-11-26)

1. **Import** (`POST /app-imports`, the file as it is, at most 5 MB): anything that is not a `zelvo-app/1` package is refused (`apps.import.refused`); otherwise it is **kept as it came** (`app_imports`, with its SHA-256) as *Pending* and audited (`apps.imported` with the number of problems and conflicts and the permissions it would make). Nothing of it is active.
2. **Check and preview** (`GET /app-imports/{id}`, again against the installation as it is now):
   - **Shape and limits** (20 record types, 50 data lists, 20 roles, 50 forms, 200 bindings, 100 rules, 500 references).
   - **Each item with the builders' own checks:** record types (K2's states, prefix not built-in or another type's), data lists, forms (C1's `FormDefinitionCheck`; data lists in the package or here), rules (C3's `WorkflowRuleEndpoints.CheckAsync` with this installation's ids), the app's entries.
   - **References** each found here, or the problem `reference-not-found` naming what to add first.
   - **Permissions it creates and grants:** a package role holds only `records.<key>.*` of the package's own types (`permission-not-allowed`); a type's permissions are **never** given to the requester role (`grant-requester`); grants to existing roles are listed as such.
   - **What already exists** (app, record type, data list or role with the same key): a **conflict** for the administrator to decide, **keep** (what is here stays as it is) or **replace** (the package's definition applies through the same checks; a replaced record type must keep its states in use with their category, its form bindings are replaced and its rules are switched off for the package's; a replaced data list is merged item by item; a replaced role gets the package's permissions and keeps its others). Nothing is ever deleted.
3. **Approve** (`POST /app-imports/{id}/approve` with a choice for every conflict, after the administrator confirms on screen): checked again; refused while it has problems (422 `package-invalid`) or a conflict has no choice (422 `choices-required`). Installed in **one transaction** (row locked, one approval at a time): data lists, roles, record types with their permissions, new published forms (keys made unique), bindings, rules at the end of the list, the app **active**. Audited: `apps.import.added` / `apps.import.replaced` per item, `apps.import.approved` (choices, counts, permissions made) and `apps.activated`.
4. Or **discard** (`apps.import.discarded`), changing nothing.

The approver may be the administrator who imported it (as for plain-language rules, decision 18); `apps.manage` is for tenant administrators only and **not for integrations** (for the product owner's approval), because an approved package makes record types, roles and rules and grants permissions.

## API

| Endpoint | What |
| --- | --- |
| `GET /apps`, `GET /apps/{key}` | Active apps with the entries the caller may use; holders of `apps.manage` see all with what they group |
| `POST /apps`, `PATCH /apps/{key}`, `POST /apps/{key}/activate`, `/deactivate`, `GET /apps/{key}/export` | Building, switching, exporting (`apps.manage`) |
| `GET /data-lists`, `GET /data-lists/{key}`, `POST /data-lists`, `PUT /data-lists/{key}` | Data lists |
| `POST /app-imports`, `GET /app-imports`, `GET /app-imports/{id}`, `POST /app-imports/{id}/approve`, `/discard` | Imports (`apps.manage`) |

## Demo

Seeder `k3-app-builder`, installed in each sample organisation through the same installer an approved import uses: the **HR app** (*Human resources* / *الموارد البشرية*) with the data list *Leave types* (annual, sick, emergency, Hajj, study, with days a year), the record type *Leave request* / *طلب إجازة* (`LVE`; submitted, checking the balance, waiting for documents, approved, closed) with its form (leave type from the data list, first and last day, notes), the role *HR officer*, a rule that tells the requester their request arrived (in the app), list views *Open leave requests* and *Assigned to me*, menu entries and the portal entry *Request leave*.

## Not in K3

- Custom code, scripts and calculated fields (module 28, Phase 3).
- Signing app packages: they are moved by administrators between their own installations and always reviewed before they are active; a package signature could follow if apps are shared between organisations.
- Data lists from other systems (lookups through the REST connector builder, left from C1 and E7).
- SLA policies, dashboards and notification wording in packages (set up in the installation that imports the app).
