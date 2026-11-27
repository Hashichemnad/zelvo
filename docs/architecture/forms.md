# Form builder (task C1, module 2)

Covers the **form builder** part of module 2 in `docs/requirements.md`, LANG-5 for forms, and the profile defaults the form builder needs. It covers:

- drag-and-drop fields;
- rules (show, hide, require, read-only);
- defaults from the requester's profile;
- validation (required, formats, min/max, file type and size);
- reusable field sets;
- labels and options in English and Arabic;
- a desktop and mobile preview before publishing.

The same builder customises incident forms now. Requests (C2) and complaints, inquiries and suggestions (C6) use it when those record types exist.

Product owner decisions (2026-11-24):

- **Deferred field types.** The asset picker comes with the CMDB (module 18, Phase 2; done in G1 as the configuration item picker, see `cmdb.md`). Lookups from other systems come with the REST connector builder (E7). The field model has room for both.
- **Profile fields are added to users.** Users now have a department, a manager, a location, an employee number and a phone.
  - Departments are a bilingual list in Settings → Departments. C2 reuses it for the department that owns a service.
  - Administrators set the profile fields on the user page and through bulk import. Identity connectors (E7) can fill them later.
- **Signatures** are drawn with a mouse, finger or stylus and saved as a PNG that is scanned like any upload. Typing your full name and ticking "I confirm" is the equal alternative for keyboard and screen-reader users.
- **Drag and drop uses dnd-kit** (`@dnd-kit/core`, `@dnd-kit/sortable`, `@dnd-kit/utilities`; MIT).
  - It gives keyboard and touch dragging, with announcements in the page's language.
  - Every move also has a button (add, move up, move down), as WCAG 2.2 2.5.7 asks.

## Code

| Piece | Where |
| --- | --- |
| Tables `forms`, `form_versions`, `field_sets`, `field_set_versions`, `form_bindings`, `ticket_form_responses`, `departments` (row-level security; migration `Forms`); profile columns on `users`; `ticket_attachments.form_field_key` | `src/api/Zelvo.Data/Forms`, `Directory`, `Identity` |
| Schema, rules, format checks, definition and answer checks, resolver, clean-up job | `src/api/Zelvo.Tickets/Forms` |
| API: forms, field sets, bindings, uploads, answers on incidents, missing translations | `src/api/Zelvo.Api/Forms`, `Incidents/IncidentEndpoints.cs` |
| Departments and profile fields | `src/api/Zelvo.Api/ServiceDesk/DepartmentEndpoints.cs`, `Identity/UserProfileFields.cs`, `Integrations/Bulk/UsersBulkType.cs` |
| Demo seeder `c1-forms` | `src/api/Zelvo.Api/Demo/DemoFormsSeeder.cs` |
| Browser rules, formats and checks (same as the server) | `src/web/src/lib/forms` |
| Builder, renderer, signature pad, answers view, Settings panels | `src/web/src/components/forms` |
| Pages: Settings → Forms, Field sets, Departments; the form in "Report an incident" and "Log an incident" | `src/web/src/app/[locale]/settings/{forms,field-sets,departments}`, `components/incidents/ReportIncidentForm.tsx` |
| Cases shared by the server and browser tests | `tests/shared/form-cases.json` |

## Forms, versions and field sets

A form has a key (`laptop-problem`), a bilingual name and description, a state and a **draft**.

- **States: Draft → Published → Retired.**
  - Administrators change the draft as often as they like. A draft may have problems; they are listed and stop publishing.
  - **Publish** freezes the draft as a new **version** (1, 2, …). New tickets use it at once. Each ticket keeps the version it was filled with.
  - **Discard** goes back to the published version. **Retire** stops new tickets from using the form; **reactivate** brings it back.
  - A form that was never published can be deleted. Published forms are kept for the tickets that used them.
  - **Copy** makes a new draft form from another form's draft.
- **Elements.** A form holds fields and references to field sets, in order.
- **Field sets** have the same life cycle. A form refers to a field set; it does not copy it.
  - Publishing a new version of a field set changes **every published form that uses it at once**, as module 2 asks.
  - Publishing or retiring is refused (`field-set-breaks-forms`) when it would break a published form: a field key used twice, or a form rule that looks at a field that would be gone. The refusal names each form.
  - A field set's rules can look only at its own fields.
- **Concurrency.** `If-Match` with the `etag` of the form or field set is checked when it is sent; the builder always sends it (412 `version-mismatch`).
- **Audit.** Every change is audited automatically. Schemas are long, so only their length is recorded (`[AuditLengthOnly]`); the versions keep the full text.
  - Publishing, retiring and reactivating also write named entries: `forms.form.published`, `forms.field-set.retired`, and so on.

## Which form a ticket uses

`form_bindings` maps a record type, and optionally a category, to a form.

- A form on a category also covers the categories below it, unless they have their own.
- A binding without a category is the record type's **default**.
- Bindings to forms that are not published (drafts, retired forms) are skipped.
- `GET /api/v1/forms/resolve?recordType=Incident&categoryId=…` returns the form for a new ticket, with:
  - its fields, field sets expanded as published now;
  - the requester's **defaults**;
  - the names of the users and locations those defaults refer to.

## Fields

| Type | Answer | Notes |
| --- | --- | --- |
| Text | text | One line or several (`multiline`); formats Emirates ID, phone, email, IBAN; length limits; a regular expression (whole answer, 100 ms limit) |
| Number | number | Smallest and largest |
| Date | `YYYY-MM-DD` | Earliest and latest |
| Dropdown | option value, or a list with `multiple` | Options have a stable value and a bilingual label |
| Checkbox | true or false | Required means it must be ticked |
| Radio | option value | |
| File upload | list of upload ids | Types (a subset of the tenant's allowlist), largest file, most files (default 5, at most 20) |
| User picker | user id | Active users, found by name (`GET /forms/people`, names only) |
| Location | location id | The B2 location tree |
| Signature | `{"attachmentId"}` or `{"typedName", "confirmed": true}` | Drawn PNG or typed name |
| Table rows | list of rows | Columns are text, number, date, dropdown or checkbox; fewest and most rows (at most 100) |
| Rich text | Markdown | Shown with the knowledge base's Markdown viewer, never as HTML, without images |
| Configuration item (task G1) | item id, or a list with `multiple` (at most 20) | Items of the field's classes (`configurationItemClasses`, with the classes below them); requesters only find active items of classes marked for them; picked items are linked to the ticket (`cmdb.md`) |

- Every field has a bilingual label and can have help text and a placeholder. Keys are camelCase and unique in the form, field sets included.
- **Personal data.** Fields can be marked as personal data. Their answers are stored apart (`personal_answers`, masked in the audit log) and are replaced with placeholders before any text goes to AI (the gateway masks them; E5 sends title, description and comments, not form answers).
  - The flag is always on for the Emirates ID, phone, email and IBAN formats, for profile email, phone and Emirates ID, and for signatures.
- **Format checks.**
  - **Emirates ID:** 15 digits starting with 784, with a valid Luhn check digit. Dashes, spaces and Arabic-Indic digits are allowed.
  - **Phone:** E.164. UAE numbers may be written locally (`050 123 4567`, `04 123 4567`). UAE mobiles have 9 digits after 971 and landlines 8.
  - **Email:** a valid address; the domain is lower-cased.
  - **IBAN:** the ISO 13616 mod-97 check, and the country's length for 20 common countries (AE: 23).
  - Answers are stored in their normal form, for example `+971501234567` and `AE070331234567890123456`.
- **Defaults.** A profile default (name, email, phone, Emirates ID, employee number, department, manager, location) wins over a fixed default when the profile has a value.
  - Staff logging an incident for someone else get that person's email, phone and Emirates ID only when their roles may see those user fields, and that view is audited.

## Rules

Each field can have rules: **show**, **hide**, **require** or **make read-only**, when **all** or **any** of their conditions hold. Conditions look at other answers:

- is, is not;
- contains;
- is empty, is answered;
- more than / after, less than / before;
- ticked, not ticked.

Rules are evaluated in an order where each field comes after the fields it looks at. Publishing refuses rules that form a circle, look at their own field, look at unknown fields, or compare in a way that does not fit the other field.

- A field with show rules is visible only when one matches. A matching hide rule hides it.
- A **hidden field counts as not answered**, so its answer is dropped and rules that look at it see nothing.
- A visible field is required when it is marked required or a require rule matches.
- A **read-only** field keeps its default; whatever was sent is ignored.

The server (`FormRules`) and the browser (`rules.ts`) evaluate the same rules. The browser does so as people type; the server always does so again. `tests/shared/form-cases.json` holds format and rule cases that both test suites run, so the two cannot drift apart.

## Answers on tickets

`POST /api/v1/incidents` takes `formVersionId` and `formAnswers`.

1. The server finds the form for the category.
   - If the sent version is no longer the published one, the answer is **409 `form-changed`**. The web app reloads the form, keeping answers to fields that are still there.
2. It applies the defaults and rules, and checks every answer. Required fields must be answered whenever a form applies, also for integrations.
3. It checks what the answers refer to:
   - users must be active;
   - locations must be selectable;
   - files must be the caller's own unsent uploads for that field, not infected, of the field's types and size.
4. Refusals are **400 `validation-failed`** with `formErrors`: a list of the field (`table.3.column` inside tables), a code (`required`, `format-iban`, …) and an English message. The web app shows its own translated text for each code under the field and moves the focus to the first one.

`ticket_form_responses` keeps:

- the answers, split into ordinary and personal;
- the version;
- a **copy of the fields as they were**, so a later change to the form or a field set never changes what the answers meant.

`GET /api/v1/incidents/{id}/form` returns them to the requester and to holders of `incidents.read`. Staff views of personal answers are audited (`forms.personal-data.viewed`). Incident pages show the answers under the facts.

## Uploads for forms

Files and drawn signatures are uploaded while the form is filled, before the ticket exists.

- `POST /api/v1/form-uploads` takes the multipart fields `file` and `field`, and follows the tenant's attachment rules:
  - size limit;
  - allowlist (PNG is always accepted so that drawn signatures work);
  - content matching the type.
- Uploads are stored in `ticket_attachments` with `form_field_key` and no ticket, and are scanned by ClamAV like any attachment.
- Until the form is sent they belong only to their uploader, who can see and remove them (`GET`/`DELETE /form-uploads/{id}`). One person holds at most 50 unsent uploads.
- When the form is sent they become the ticket's attachments: they show in the attachment list and download once scanned clean.
- The worker's `FormUploadCleanupJob` deletes unsent uploads after a day. It finds tenants with work through `zelvo_tenants_with_stale_form_uploads`.

## Profile fields and departments

- **Users** now have `department`, `manager`, `location`, `employeeId` and `phone`. They are field-rule fields of the `users` resource; `phone` is personal data, editable by tenant administrators and hidden from others by default.
  - `PATCH /users/{id}` takes `departmentId`, `managerId` and `locationId` (an empty string removes them), `employeeId` and `phone` (normalised like the phone format).
  - A person cannot be their own manager, and managers cannot form a circle.
- **Bulk import and export** of users have the columns `department` (name in English or Arabic), `manager` (email), `location` (path, levels separated by `›` or `>`), `employeeId` and `phone`. `-` removes a value.
- **Departments** (`/api/v1/departments`):
  - every signed-in user sees the active ones;
  - administrators (`directory.manage`) add, rename, deactivate and delete them;
  - departments in use cannot be deleted.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `forms.manage` | Build, publish and retire forms and field sets; choose which form each category uses | Tenant administrator |

Filling a form needs nothing more than reporting the ticket. Integrations can be given `forms.manage`.

## Screens

- **Settings → Forms:** the forms with their state, version and where they are used; "Create a form"; and which form each category uses.
- **The builder (`/settings/forms/{id}`):**
  - three panes: field types and field sets, the form's fields, and the selected field's properties;
  - in the properties: key, bilingual label, help and placeholder, behaviour, options, checks, defaults, table columns and rules;
  - a toolbar to save the draft, publish and discard;
  - the list of problems that stop publishing.
- **Preview:** shows the form as a requester fills it, on desktop or mobile (375 px), in English or Arabic, with rules working and a "Check the answers" button. Nothing is sent or uploaded.
- **Settings → Field sets** and the field set builder, and **Settings → Departments**.
- **"Report an incident" and "Log an incident"** show the category's form under the title and description, and load it again when the category or the requester changes.
- **Incident pages** show the answers.

Everything is in English and Arabic, right to left, and WCAG 2.2 AA. Axe runs in `tests/e2e/specs/forms.spec.ts`.

## Demo data

Seeder `c1-forms` (order 140, after B5) adds three departments with the demo users in them, plus the requester's manager, location, employee number and a fictional phone number. It also adds:

- a published field set "Requester details": department (read-only), employee number and mobile, all from the profile;
- a published default incident form, "General incident";
- a "Laptop problem" form on the Laptops category, with the module 2 example: "Monitor size" is shown and required only when "Add a monitor" is ticked.

## Tests

- **Unit** (`FormEngineTests`): the shared format and rule cases, answer checks, hidden and read-only fields, files, users, signatures and tables, patterns, definition problems, rule cycles, and JSON round trips.
- **Integration against PostgreSQL** (`FormApiTests`):
  - fill a category form;
  - server checks and normalised answers, with personal data kept apart and out of the audit log;
  - staff views audited;
  - versions, including 409 `form-changed`;
  - only `forms.manage` builds forms, and problems stop publishing;
  - missing translations;
  - delete, retire, reactivate and copy;
  - a field set change reaching every form, and refused when it would break one;
  - profile defaults and read-only fields, including what staff may see for someone else;
  - uploads (ownership, type, reuse, typed signature, clean-up);
  - bulk import of profile columns;
  - tenant isolation.
- **Demo** (`DemoModeTests`): the seeder runs under `migrate`, its forms have no problems, and reset still covers every table.
- **Web:**
  - `lib/forms/forms.test.ts`: the shared cases and answer checks;
  - `components/forms/forms.test.tsx`: builder palette, properties, rules, keyboard moves, save and publish with If-Match, mobile and Arabic preview, problems; renderer tables, signatures, choices, uploads and people search; the answers view;
  - `incidents.test.tsx`: the form in "Report an incident", with browser and server errors.
- **End to end** (`forms.spec.ts`): build, preview, publish and bind a form, then fill it as a requester, in English and Arabic with axe checks.

## Not in C1

- The asset picker (module 18) and lookups from other systems (E7), as decided.
- Service catalog forms (C2) and complaint, inquiry and suggestion forms (C6), which reuse this builder.
- Routing, approvals and fees by form answer (C3, C4, E8).
- Changing answers after the ticket is sent.
- Answers in search.
- A Hijri date picker: date fields use the browser's date input.
- Anonymous forms: visitors who are not signed in come with C6.
- Sections and page breaks within a form.
- Keyboard dragging from the palette into a chosen place. Palette items are added at the end with their button and then moved up or down.
