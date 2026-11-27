# CMDB core (task G1, module 18)

Covers the first part of module 18 in `docs/requirements.md`:

- configuration item classes (hardware, software, business services, applications, network, cloud resources) with sub-classes and custom attributes per class;
- relationships between items (runs on, depends on, connects to, used by, and types of the tenant's own);
- the visual relationship map, with an equal list view;
- impact traversal ("what depends on this item"), used later by problems (H1) and changes (H3, H5);
- configuration items on incidents and service requests, and the CI picker field that C1 deferred.

Import, discovery and reconciliation come with G2. Assets, contracts, licences and labels come with G3.

Product owner decisions that shape it (2026-11-26, Phase 2 plan):

- **Decision 5. Relationship map with Cytoscape.js** (MIT). The list view is the accessible equal.
- **Decision 6. Who a change affects.** A "used by" relationship points at users, departments and customer groups. G3 adds assets assigned to users.
- **Decision 12. CMDB size.** 50,000 items per tenant on the Small server and 500,000 on Medium. L1's load test checks it.

## Code

| Piece | Where |
| --- | --- |
| Tables `ci_classes`, `configuration_items`, `ci_number_sequences`, `ci_relationship_types`, `ci_relationships`, `ticket_configuration_items` (row-level security; migration `Cmdb`) | `src/api/Zelvo.Data/Cmdb`, `ZelvoDbContext.Cmdb.cs` |
| Class index, attribute checks, built-in relationship types, numbers, search documents, missing translations | `src/api/Zelvo.Api/Cmdb/CmdbCore.cs` |
| Impact traversal and the map's graph | `src/api/Zelvo.Api/Cmdb/CmdbGraph.cs` |
| Settings API (classes, attributes, relationship types, number prefix, standard classes) | `CmdbSettingsEndpoints.cs`, `CmdbStandardClasses.cs` |
| Items API (items, relationships, impact, map, history, tickets, picker lookup) | `CmdbItemEndpoints.cs`, `CmdbResponses.cs` |
| CIs on incidents and service requests | `CmdbTicketEndpoints.cs` |
| CI picker field type | `src/api/Zelvo.Tickets/Forms` (`FieldType.ConfigurationItem`), `src/api/Zelvo.Api/Forms/FormSubmission.cs` |
| Demo seeder `g1-cmdb` | `src/api/Zelvo.Api/Demo/DemoCmdbSeeder.cs` |
| Pages: `/cmdb`, `/cmdb/new`, `/cmdb/[id]` (map and list view), Settings → CMDB, the CI panel on incident and request pages, the picker in forms | `src/web/src/app/[locale]/cmdb`, `settings/cmdb`, `src/web/src/components/cmdb` |

## Classes and attributes

- **Families.** Every class belongs to one of the six families module 18 names: Hardware, Software, BusinessService, Application, Network, CloudResource. A top-level class chooses its family. Sub-classes take their parent's.
- **Tree.** Classes form a tree of up to four levels, for example Hardware → Server → Database server.
  - A class has a stable key (`database-server`) and a bilingual name and description.
  - Classes are ordered among their siblings. A class can be made inactive: its items stay, but no new items are added to it.
  - A class can be deleted only while it has no items and no classes below it.
- **Attributes** are C1 form fields, stored as JSON on the class.
  - An item of a class has the attributes of its class and of every class above it, top first. A server has Hardware's serial number and manufacturer, plus its own host name and memory.
  - A key may not be reused up or down the tree (`attribute-inherited`, `attribute-below`).
  - Allowed types: text, number, date, dropdown, checkbox, radio, user and location, with C1's checks: required, lengths, patterns, number and date ranges, options.
  - Not allowed: rules, profile defaults, read-only fields, files, signatures, tables, rich text, and personal data (the personal-data flag and the Emirates ID and IBAN formats). An item is not a person.
  - At most 50 attributes per class.
- **Standard classes.** "Add the standard classes" (`POST /cmdb/classes/standard`) adds Zelvo's bilingual classes the tenant does not have yet, by key, and changes nothing that exists:
  - servers, computers (laptops, desktops), printers, mobile devices;
  - operating systems and installed software;
  - business services;
  - applications, web applications and databases;
  - routers, switches and firewalls;
  - virtual machines, cloud databases and storage.

  Their identifying attributes (`hostName`, `serialNumber`, `macAddress`, `resourceId`) are the keys G2's identification rules will match on.
- **Requesters.** "Requesters may pick items of this class" (`requesterVisible`) is off by default. The standard business services, applications and printers have it on. People without `cmdb.read` only ever find active items of such classes.
- **Concurrency and audit.** Class changes take If-Match with the class's `etag`. Every change is audited automatically. Attributes are recorded by length only (`[AuditLengthOnly]`). "Add the standard classes" writes `cmdb.classes.standard-added`.

## Configuration items

- **Fields.** An item has:
  - a number, `CI-000123`. The prefix is the tenant setting `cmdb.numberPrefix`. Numbers come from `ci_number_sequences` inside the creating transaction and are never reused.
  - a class, a name and a bilingual description;
  - a status: Planned, Active, Maintenance or Retired;
  - an owner group (B2) and a location (B2 tree);
  - its attribute values.

  The name is one value in any language: items are usually named by their technical name (`srv-app-01`).
- **Values are checked** against the class's attributes on every save, with C1's answer checks (`CmdbAttributes.CheckValues`). Users must be active and locations selectable.
  - A `PATCH` sends all values. Values left out are cleared.
  - Moving an item to another class checks its values again and drops those the new class does not have.
  - Values of attributes an administrator removed stay until the item is next changed; the API shows them as `stale`.
- **Retire, not delete.** An item that a ticket refers to can only be retired. Retired items are left out of lists (unless asked for), of impact traversal and of the map (unless asked for), and cannot be linked to new tickets.
- **Concurrency.** `GET` returns an ETag. A `PATCH` with a stale If-Match gets 412 `version-mismatch`.
- **History** (`GET /cmdb/items/{id}/history`) comes from the audit log, as for incidents. It shows:
  - field changes, with class, group and location names instead of ids;
  - relationships added and removed (`cmdb.relationship.added`, `.removed`, written on both items);
  - tickets linked and unlinked (`cmdb.ticket-link.added`, `.removed`).

## Relationships

- **Types.** A relationship type has a key and a bilingual name for each direction, for example "Runs on" / "Hosts". Four types are built in. Administrators can rename them but not delete them or change their impact:

  | Key | From the source | From the target | Target | Impact |
  | --- | --- | --- | --- | --- |
  | `runs-on` | Runs on | Hosts | Item | ToSource: a fault of the host affects what runs on it |
  | `depends-on` | Depends on | Needed by | Item | ToSource |
  | `connects-to` | Connects to | Connected from | Item | Both |
  | `used-by` | Used by | Uses | User, department or customer group | ToTarget: a fault of the service affects its users |

  The built-in types are added to a tenant the first time its relationship types are used (`CmdbRelationshipDefaults.EnsureAsync`). Administrators add their own item-to-item types, for example "Backs up" with impact None. A type in use can only be made inactive.
- **Relationship rules.** A relationship runs from an item (the source) to exactly one target: an item, a user, a department or a customer group. A database check enforces it (`num_nonnulls(...) = 1`).
  - The same two ends are related once per type; a unique index treats empty target columns as equal.
  - An item is never related to itself.
  - "Used by" never points at an item; item types never point at people.
  - Relationships across tenants are impossible: row-level security hides the other tenant's items.

## Impact traversal

`GET /cmdb/items/{id}/impact?depth=3` and `POST /cmdb/impact` (several items) answer "what does a fault of this affect".

- Each relationship type's impact direction is followed, level by level, up to the depth (1–10; default 3). There is one query per level, so it stays fast with hundreds of thousands of items.
- It returns:
  - the affected items, each with its depth, the item it was reached from and the relationship type;
  - the affected business services, separately;
  - the users, departments and customer groups that use the affected items or the starting items;
  - `userCount`: users named directly plus active members of the departments. Customer groups are listed but not counted; their membership is by email domain.
- Retired items are skipped. At most 5,000 items are returned (`truncated`).
- `CmdbGraph.ImpactAsync` is the entry point that problems (H1) and the change what-if (H5) reuse.

## Relationship map and list view

- **API.** `GET /cmdb/items/{id}/map` returns the nodes and edges around an item, following relationships both ways.
  - Depth 1–4 (default 2), at most 200 nodes (`truncated`).
  - Filters: relationship types, classes (with the classes below them; the item itself is always shown), people on or off, retired items on or off.
- **Map.** The item page draws it with **Cytoscape.js**:
  - zoom, pan and fit;
  - depth and filters;
  - shapes and colours per family, with a legend that does not rely on colour;
  - keyboard navigation: arrow keys move between connected nodes, Enter opens the item, +/− zooms, 0 fits. A live region announces the selected node in the page's language.
- **List view.** The same nodes and relationships with the same filters, as a table. It is the equal for keyboard and screen-reader users (WCAG 2.2 AA).

## Configuration items on tickets

- **Endpoints.** `GET`, `PUT /{itemId}` and `DELETE /{itemId}` under `/incidents/{id}/configuration-items` and `/service-requests/{id}/configuration-items`.
  - Listing needs the ticket's staff read permission. Adding and removing need its edit permission.
  - Requesters do not see the list.
  - Each link is audited on the ticket and on the item.
- **Ticket table.** `ticket_configuration_items` belongs to the ticket core, so problems (H1) and changes (H3) use it too.
- **Tickets on an item.** `GET /cmdb/items/{id}/tickets` lists the tickets an item is on, of the record types the caller may read.
- **CI picker field** (`FieldType.ConfigurationItem`), the type C1 left for the CMDB:
  - One item, or up to 20 with `multiple`. `configurationItemClasses` limits the classes (each with the classes below it).
  - On submission the server checks each picked item (`CmdbPicker.CheckAsync`): it exists, is not retired and is of the field's classes. For people without `cmdb.read` it must also be active or under maintenance and of a class marked for requesters. Otherwise the answer is refused with `configuration-item-unknown`.
  - Picked items are linked to the new ticket.
  - The answers view names them through `references.configurationItems`.
  - `GET /cmdb/lookup` serves the picker and the ticket panel under the same rule.

## Search

- Items are the `ci` document type. The document holds:
  - title: number and name;
  - body: the description in both languages;
  - text: the attribute values that are text or numbers;
  - keywords: number, name, and short values such as host names and serial numbers.
- Readers are the roles with `cmdb.read` or `cmdb.manage`. The type also needs `cmdb.read` to be searched at all.
- A rebuild reads every item through `CmdbSearchSource`.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `cmdb.read` | See items, relationships, the map, impact, history and the tickets an item is on | Agent, team lead, executive / auditor, tenant administrator |
| `cmdb.manage` | Add, change, retire and delete items and relationships; Settings → CMDB | Tenant administrator |

- **Integrations.** Both permissions are open to integrations; G2's imports and discovery need them.
- **Licensing.** The CMDB endpoints are not behind a licensed module yet. Licences list only the Phase 1 modules (`LicensedModules.Phase1`), and how Phase 2 modules are licensed is for the product owner to decide. Feature releases are already gated by the upgrade licence (F4).

## Demo

Seeder `g1-cmdb` (order 270) gives both sample organisations Zelvo's standard classes, the custom type "Backs up", and relationships that make the map and impact interesting.

- **Government entity:**
  - business services: e-services portal, building permits, staff email;
  - applications: the e-services website, the permits system and the transaction management system;
  - infrastructure: the permits database, web, application, database and backup servers, the mail VM, the core switch, the edge firewall and the customer service printer;
  - a retired legacy web server.
- **Company:**
  - business services: email, customer billing, VPN;
  - applications: the billing system and the mail gateway;
  - infrastructure: the billing cloud database, VMs, the VPN firewall, a floor switch, a laptop and backup storage.
- **Who uses the services.** The main service is "used by" the customer happiness or sales department and by the VIP customer group. Email or billing is used by finance.
- **Tickets.** Sample incidents are linked to the items they are about: the portal outage, the printer, the email delay, the invoices, the VPN and the docking station.

## Not in G1

- Import from Excel and CSV, the discovery API and the discovery sources, identification rules, reconciliation and duplicates (G2).
- Assets, lifecycle, contracts, warranties, licences, labels and scanning (G3).
- CIs on problems and changes (H1, H3; the table is ready).
- A separately licensed CMDB module (for the product owner).
- The load test with 50,000 and 500,000 items (L1).
