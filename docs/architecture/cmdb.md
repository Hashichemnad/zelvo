# CMDB core (task G1, module 18)

Covers the first part of module 18 in `docs/requirements.md`:

- configuration item classes (hardware, software, business services, applications, network, cloud resources) with sub-classes and custom attributes per class;
- relationships between items (runs on, depends on, connects to, used by, and types of the tenant's own);
- the visual relationship map, with an equal list view;
- impact traversal ("what depends on this item"), used later by problems (H1) and changes (H3, H5);
- configuration items on incidents and service requests, and the CI picker field that C1 deferred.

Import, discovery and reconciliation came with G2 (below, "Import, discovery and reconciliation"). Assets, contracts, licences and labels come with G3.

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

## Import, discovery and reconciliation (task G2)

Covers module 18's "import and sync from Excel, the API and discovery tools; duplicate detection and reconciliation". Product owner decision 4 of 2026-11-26: the discovery sources are a **discovery API**, **Microsoft Intune**, **Azure**, **Active Directory and Entra ID computers** and **VMware vCenter**; **AWS is deferred** until a customer asks.

### Code

| Piece | Where |
| --- | --- |
| Tables `ci_discovery_sources`, `ci_source_records`, `ci_installed_software`, `ci_review_items`; columns `configuration_items.attribute_sources`, `missing_since`, `merged_into_id` and the database-kept `identifiers`; `ci_classes.identification_rules` (row-level security; migration `CmdbDiscovery`) | `src/api/Zelvo.Data/Cmdb/DiscoveryEntities.cs`, `ZelvoDbContext.Cmdb.cs` |
| Discovery clients (Intune, Entra ID devices, Azure Resource Graph, Active Directory computers, vCenter) and their settings | `src/api/Zelvo.Connectors/DiscoveryClients.cs` (`IDiscoveryClients`, part of `ConnectorClients`) |
| Identification rules, precedence, reconciliation, duplicates, merging | `src/api/Zelvo.Api/Cmdb/CmdbReconciliation.cs` |
| Sources, the discovery API, records, the review queue | `CmdbDiscoveryEndpoints.cs` |
| The scheduled sync | `CmdbDiscoveryJobs.cs` (`CmdbDiscoveryRunner`, `CmdbDiscoveryJobs`) |
| CSV and Excel | `ConfigurationItemsBulkType.cs` (bulk type `configuration-items`) |
| Demo seeder `g2-discovery` | `src/api/Zelvo.Api/Demo/DemoDiscoverySeeder.cs` |
| Pages: Settings → CMDB (Discovery, Import and export, identification rules on classes, precedence of people's values), `/cmdb/review`, the Discovery panel on `/cmdb/[id]`, "No longer reported" on `/cmdb` | `src/web/src/components/cmdb/DiscoverySources.tsx`, `CiImportExport.tsx`, `ReviewQueue.tsx`, `CiDiscoveryPanel.tsx` |

### Sources

| Kind | Reads | Uses | Classes it reports |
| --- | --- | --- | --- |
| Discovery API | Nothing: a tool pushes `POST /cmdb/discovery/sources/{id}/records` (up to 1,000 records a call) with an API key or OAuth client holding `cmdb.manage` | — | Whatever the tool names (class keys) |
| Microsoft Intune | Graph `deviceManagement/managedDevices` and, optionally, `detectedApps` with their devices (at most 2,000 apps per sync) | An Entra ID connector's app (`DeviceManagementManagedDevices.Read.All`) | `computer` (Windows, macOS; host name, serial, MAC, model, OS), `mobile-device` (iOS, iPadOS, Android; IMEI) |
| Entra ID devices | Graph `devices` (disabled ones left out) | An Entra ID connector's app (`Device.Read.All`) | `computer`, `mobile-device` |
| Azure | Resource Graph (`2022-10-01`), the chosen subscriptions (or all the app may read) and resource types | An Entra ID connector's app with the Reader role | `virtual-machine`, `cloud-database`, `storage`, `cloud-resource` (resource id, region, provider) |
| Active Directory computers | LDAP search with the source's base DN and filter (`(objectCategory=computer)`); disabled accounts left out | An LDAP connector's read-only connection | `server` (a server operating system), `computer` |
| VMware vCenter | `api/session`, `api/vcenter/cluster`, `host`, `vm` per host and each VM's `guest/identity` (VMware Tools; errors ignored) | Its own address, read-only account and password (encrypted with `Zelvo.Secrets`, needed again for a new address or account), optionally a SHA-256 certificate fingerprint for an untrusted certificate | `virtualisation-cluster` (new standard class), `server` (hosts), `virtual-machine` (CPU, memory, guest host name and system); a VM *runs on* its host, a cluster *depends on* its hosts |

- **Class keys** are Zelvo's standard keys; a source's `classMap` maps them to a tenant's own keys. Records of a class the tenant does not have are counted (`unknownClass`) and left out.
- **Schedule.** Pull sources sync every 15 minutes to 7 days (default daily) in the API (`CmdbDiscoveryJobs`, every minute, `FOR UPDATE SKIP LOCKED` lease per source, tenants from `zelvo_cmdb_discovery_tenants()`), because numbering, checks and search documents live there. "Sync now" makes a source due at once; "Test" reads it and shows the counts per class and the first records without changing anything.
- **Every call** of a sync or test goes to the integration log (`cmdb.discovery.<kind>.sync` or `.test`, at most 200 exchanges a sync, bodies masked; the vCenter session id is never kept). Each sync is audited (`cmdb.discovery.synced` with its counts), as are pushes (`cmdb.discovery.pushed`), completed snapshots and "Sync now".
- Every connection follows the connectors' outgoing-request policy (`Connectors:Network:AllowedPrivateNetworks`; `Connectors:ManagementUrl` for sovereign clouds).

### Reconciliation

- **Identification rules** per class: a list of rules, each 1–3 attribute keys (or `name`), tried in order, for example `serialNumber`, then `hostName + macAddress`. A class without rules takes the nearest class above's, and at the top Zelvo's defaults: serial number, cloud resource id, MAC address, host name, IMEI. Administrators set them on the class (Settings → CMDB → Classes).
- **Matching.** A record is found by its source's own id first (`ci_source_records`). Otherwise the rules are tried among items under the same top class (a "computer" from Active Directory matches a "laptop" entered by hand), not retired and not merged. Comparison ignores case and surrounding spaces; MAC addresses are normalised to `AA:BB:CC:DD:EE:FF` by the clients. The database keeps every item's identifying values (`identifiers`, `key=value` in lower case, by a trigger whoever writes the item) behind one GIN index, so matching stays fast with hundreds of thousands of items.
  - One match: the record is linked to it.
  - None: a new item is made (`CI-…` number, status Active).
  - Several: the record waits in the **review queue** (*Ambiguous*) and is not applied until a person links it or makes a new item.
- **Precedence.** Each value remembers who set it last (`attribute_sources`: a source or `manual` for people, imports and the API). A source overwrites a value only when its priority for that key (per attribute where set, else the source's own, 1 most trusted to 100) is the same or better than the last setter's. People's values have the tenant setting `cmdb.manualPriority` (default 10; sources default to 50), so corrections stay unless an administrator ranks a source above people for that value. The name works the same way (`name`). Sources never clear values; values that fail the class's C1 checks are left out and counted (`valuesRefused`); user and location values are never taken from sources.
- **Missing, never deleted.** A pull sync is a complete list: the source's records it no longer saw are marked (`missing_since`), and an item none of whose records is still reported is marked "No longer reported" (`configuration_items.missing_since`, list filter `missing=true`). Reported again, both marks go. A tool that pushes a full snapshot in several calls ends it with `POST …/snapshot-complete {startedAt}`.
- **Installed software** (Intune's detected apps, the discovery API's `software`) is kept per item and source with a normalised product key (`publisher|name`), replaced at each report, for G3's licence compliance.
- **Relationships** a source reports (vCenter, the discovery API's `relationships` by the other record's id) are added when missing, never removed.
- **Duplicates.** When a linked item's values change, other items under the same top class that share the values of one of its rules (names alone never count) are raised as a *possible duplicate*. A pair is raised once, whatever was decided.

### Review queue

`/cmdb/review` (`cmdb.manage`) shows each question with the values side by side (the keys matched on first), the items' status, tickets and sources.

- **Possible duplicate:** *Merge, keeping …* moves the other item's tickets, relationships (except ones the kept item already has or that would point at itself), source records and software to the one kept, fills the kept item's empty values (with who set them), retires the other and points it at the one kept (`merged_into_id`; the item page shows it); audited `cmdb.items.merged` on both. *They are different* is final for the pair.
- **Ambiguous record:** *It is …* links it to one of the items it matched and applies its values; *It is a new item* makes one.
- One decision at a time (row lock); decisions are audited (`cmdb.review.decided`). Questions about an item that was merged away are closed.

### Import and export

The E1 bulk framework's type `configuration-items` (CSV and Excel, Settings → CMDB → Import and export; export `cmdb.read`, import `cmdb.manage`): columns `number`, `class`, `name`, `descriptionEn`, `descriptionAr`, `status`, `ownerGroup` (name), `location` (path), `attributes` (JSON, `null` clears a value) and `attr.<key>` columns (typed by the attribute). A row with a number changes that item; one without is matched by the identification rules (one match: changed, merged over its values; none: new; several: a problem; two rows describing the same item: a problem). Every row is checked with the class's checks first; nothing changes while any row has a problem. Imported values count as set by people. Audited `bulk.configuration-items.exported`, `.imported`.

### API

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET`, `POST /cmdb/discovery/sources`, `PATCH`, `DELETE /cmdb/discovery/sources/{id}` | `cmdb.discovery.manage` | Sources (the password is never returned) |
| `POST …/{id}/test`, `POST …/{id}/sync`, `GET …/{id}/records` | `cmdb.discovery.manage` | Read without changing, sync now, what it reports |
| `POST …/{id}/records`, `POST …/{id}/snapshot-complete` | `cmdb.manage` | The discovery API |
| `GET /cmdb/items/{id}/discovery` | `cmdb.read` | Sources, who set each value, software, missing and merged |
| `GET /cmdb/review`, `POST /cmdb/review/{id}/merge`, `/keep-apart`, `/link`, `/create` | `cmdb.manage` | The review queue |
| `GET /bulk/configuration-items/export`, `/template`, `POST …/import` | `cmdb.read` / `cmdb.manage` | CSV and Excel |

`cmdb.discovery.manage` belongs to tenant administrators and is **not available to integrations** (for the product owner's approval, like `connectors.manage`: sources use connectors' stored credentials). Problem codes: `source-not-found`, `review-not-found`, `connector-required`, `connector-not-used`, `credential-required`, `credential-not-used`, `not-a-push-source`, `not-a-pull-source`, `source-inactive`, `name-taken`, the settings codes (`url-not-https`, `subscription-invalid`, `resource-type-invalid`, `filter-invalid`, `thumbprint-invalid`, `class-map-invalid`, `priority-invalid`) and the rule codes `rule-invalid`, `rule-key-unknown`, `too-many-rules`.

### Demo

Seeder `g2-discovery` (order 272): a discovery API source in each organisation (*Network scanner*, *Endpoint agent*) whose records were reconciled with the G1 items (servers linked by serial number and host name, new switches, desktops and laptops with Microsoft 365 and other software, a relationship), a record that matched an item and an older hand-made copy (*Ambiguous*), a hand-made item sharing a serial number (*possible duplicate*), an item no longer reported, and an Intune (government entity) or Active Directory (company) source on the E7 connector, turned off like the connector.

### Tests

- Integration against PostgreSQL (`CmdbDiscoveryTests`): the discovery API linking by serial number with people's values winning until a source is ranked above them per attribute, refused values, unknown classes, software and relationships, snapshots marking items missing and reports clearing it, API keys that may push but not manage sources; Intune synced by the job with the connector's credential, the review queue linking an ambiguous record, the next sync raising a possible duplicate and marking an item missing, merging (tickets and relationships move, retired, merged into), keeping apart once only; vCenter's password write-only and needed again for a new address, test and sync, removed sources leaving their items; imports checked first, rows matched by a class's own rules, the same item twice refused, export for `cmdb.read`.
- Unit (`DiscoveryClientTests`): Intune devices and apps with the app token, Entra devices without disabled ones, Azure across pages with subscriptions and types, vCenter's session, header, hosts and VMs without the session in the log, Active Directory computers, MAC normalisation and settings checks.
- Web (`discovery.test.tsx`): a source's last sync and test, adding an Intune source on an Entra connector with precedence, the write-only vCenter password in Arabic, the review queue's merge and Arabic link choices, the item's discovery panel.

### Not in G2

- AWS (decision 4), other discovery tools' native formats (they use the discovery API), agent software of Zelvo's own.
- Writing back to the sources; relationships removed when a source stops reporting them.
- Sources that change an item's class or status (people decide), and deleting items a source no longer reports (they are marked).
- Not tried against real Intune, Entra ID, Azure, Active Directory or vCenter systems in this session (no access); the clients follow the documented APIs and are tested against recorded requests. Not run in Docker Compose here (no image builds in this environment).

## Not in G1

- Import from Excel and CSV, the discovery API and the discovery sources, identification rules, reconciliation and duplicates (G2, above).
- Assets, lifecycle, contracts, warranties, licences, labels and scanning (G3).
- CIs on problems and changes (H1, H3; the table is ready).
- A separately licensed CMDB module (for the product owner).
- The load test with 50,000 and 500,000 items (L1).
