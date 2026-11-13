# Zelvo — Product Requirements

Nov 13, 2026 · @Hashichemnad

## Overview

Zelvo is a bilingual (English/Arabic) ITSM platform for the UAE market. It launches with UAE government and semi-government entities, and is designed so any sector or business can use it.

**Problem.** Enterprise ITSM tools such as ServiceNow are powerful but expensive, slow to implement and built English-first. Smaller tools are easier to use but lack governance, ITIL depth and UAE-specific needs: Arabic, UAE PASS, data residency and customer-happiness reporting.

**Vision.** An ITSM platform that meets ITIL and ISO/IEC 20000, is AI-native, Arabic-first in quality, and live in days instead of months.

**Positioning against ServiceNow.**

| Dimension | ServiceNow | Zelvo |
| --- | --- | --- |
| Time to go live | Months, partner-led | Days, guided setup wizard |
| Arabic | Supported as a translation | Designed in: RTL layout, Arabic AI, Gulf dialect |
| UAE government fit | Needs customization | UAE PASS, happiness meter, complaint/inquiry/suggestion built in |
| AI pricing | Premium add-on tier | Included in every plan |
| Data residency | Region dependent | UAE cloud region or on-premises |

**Target sectors after government:** banking, healthcare, education, real estate, retail, hospitality, telecom and managed service providers.

## Goals, non-goals and success metrics

The full system — all four phases — is in scope and is built in order; each phase is reviewed and approved by the product owner before the next one starts. Development is done in Claude Code.

**Goals**

1. Ship a production-ready core: incidents, service requests, complaints, inquiries, suggestions, portal, chat, email, knowledge base and SLAs.
2. Full English/Arabic experience, with a system default language and a personal default per user and agent.
3. Setup without consultants: a new tenant configured through a wizard.
4. AI triage and agent assistance included from day one.
5. Security and hosting that pass a UAE government security review.

**Non-goals**

- Languages other than English and Arabic.
- Hosting Zelvo for customers: every customer installs and runs its own copy.
- Remote desktop control of user devices (screen recording and co-browse of the portal are in scope).

**Success metrics** (targets to confirm through testing)

| Metric | Target |
| --- | --- |
| Tenant setup time | Under 1 working day |
| Time to raise a ticket in the portal | Under 60 seconds, 3 clicks or fewer |
| Tickets auto-categorised correctly by AI | 85% or more |
| First response SLA met | 95% or more |
| Customer happiness score | 4 out of 5 or higher |
| Portal self-service deflection | 20% of contacts resolved without an agent |
| API response time | Under 200 ms at the 95th percentile |

## Users and personas

Six roles use the platform; each sees only the screens and data its role allows.

| Persona | Who they are | What they need most |
| --- | --- | --- |
| End user / customer | Employee, citizen or external customer raising a ticket | Raise a request in under a minute, track status, get answers without waiting |
| Agent | Service desk or support staff handling tickets | One workspace with all context, fast replies, AI suggestions, clear SLA timers |
| Team lead / supervisor | Manages a group of agents | Queue and SLA visibility, reassignment, workload balance, quality review |
| Approver | Manager or budget owner approving requests | Approve or reject in one tap from email, Teams, WhatsApp or the portal |
| Tenant administrator | IT or operations admin configuring the platform | Setup wizard, forms, workflows, SLAs, users, branding, language defaults |
| Executive / auditor | Leadership, quality or compliance staff | Dashboards, happiness reporting, ISO 20000 evidence, audit trail |

## Language and localization

English and Arabic are both first-class: every screen, email, notification, report and AI reply works fully in either language.

**Language resolution order** — the system picks the display language in this order:

1. The user's or agent's personal default, set in their profile.
2. The tenant default, set by the administrator in Settings.
3. The browser language, for anonymous portal visitors.
4. English, as the platform fallback.

**Requirements**

- **LANG-1** Admin sets the tenant default language (English or Arabic) in Settings.
- **LANG-2** Each user and agent can choose a personal default language; it overrides the tenant default.
- **LANG-3** A language switch is visible on every page; switching keeps the user on the same screen.
- **LANG-4** Arabic uses a full right-to-left layout: navigation, tables, forms, charts, dates and icons mirror correctly.
- **LANG-5** Catalog items, categories, forms, knowledge articles, email templates and SLA names hold both an English and an Arabic value; admins see a warning when one is missing.
- **LANG-6** Notifications go out in the recipient's language, not the sender's.
- **LANG-7** An agent and a user can each work in their own language on the same ticket; the AI offers a one-click translation of messages.
- **LANG-8** Dates support Gregorian and Hijri display; numbers support Western and Arabic-Indic digits, set per user.
- **LANG-9** Search works across both languages, including Arabic word variants (with or without diacritics, different letter forms).
- **LANG-10** The AI understands Modern Standard Arabic and Gulf dialect in chat, email and WhatsApp messages.

**Example user stories**

- As an Arabic-speaking citizen, I want the portal to open in Arabic automatically, so that I can raise a complaint without switching language.
- As an English-speaking agent, I want to read an Arabic ticket translated inline and reply in English that the customer receives in Arabic, so that language never blocks resolution.
- As an administrator, I want to see which catalog items are missing an Arabic translation, so that the portal is complete before go-live.

## Phase 1 functional requirements

Phase 1 delivers fifteen modules; each is listed with its key requirements and one representative user story.

### 1. Incident management

- Create incidents from portal, email, chat, WhatsApp, phone (agent-logged) or API.
- Priority calculated from an impact × urgency matrix that the admin can configure.
- States: New, Assigned, In progress, On hold (with reason), Resolved, Closed, Reopened.
- Assignment to groups and agents by rules (category, location, language, skill) or round-robin.
- Parent/child linking, a major-incident flag with a dedicated communication timeline.
- Full audit history of every field change.
- *Story:* As an agent, I want related incidents grouped under one parent, so that I resolve and notify them together.

### 2. Service requests and catalog

Admins build and change the whole service catalog themselves, with a drag-and-drop form builder and no code.

**Catalog builder**

- Create catalogs per department or audience, organised by category and by a main service → sub-service hierarchy (any depth).
- Draft, review, publish, suspend and retire services; copy a service to make a similar one; ready-made templates for common services.
- Bulk import and export of service cards through Excel and the API.

**Service card** — every service carries these fields, each in English and Arabic where it holds text:

| Field | What it holds |
| --- | --- |
| Service name and code | Display name and a unique service code |
| Description | What the service is, who it is for, and the outcome |
| Status | Draft, Under review, Active, Suspended (with a message shown to users), Retired |
| Category | One or more categories for browsing and search |
| Main service / parent | Its place in the main service → sub-service hierarchy |
| Department and owner | Owning department and the named service owner |
| Service type | Internal (handled in Zelvo) or external; external services show a support link that opens the provider's site instead of a form |
| Required documents | List of documents, each marked mandatory or optional, with allowed file types, maximum size and downloadable templates |
| Service steps | Ordered steps shown to the requester as their journey; each maps to an internal stage so status tracking shows the current step |
| Service fee | Free or paid; amount in AED; fee notes (for example, urgent delivery costs extra) |
| Number of days | Delivery time in working days; used as the default completion SLA |
| Delivery channels | Portal, mobile, WhatsApp, chat, email, call centre, service centre (multi-select) |
| Target audience | Individuals, businesses, government entities, employees, visitors (multi-select) |
| Service partners | Other departments, entities or vendors involved in delivering it |
| Related services | Linked services shown as "You may also need", plus prerequisite services that must be completed first |
| Keywords and icon | Search keywords, icon or image |

**Access control per service**

- Choose who can see and request each service: everyone, public (no login), or only specific groups, roles, departments, locations or named users.
- Hidden services do not appear in search, the catalog, the API or the virtual agent for anyone outside the allowed groups.
- Option to request on behalf of someone else, limited to roles the admin allows.

**Bundle services**

- A bundle groups several services into one request (for example, "New employee onboarding" = laptop + email account + access card + system access).
- The requester fills one combined form; Zelvo creates a child request for each service, each with its own team, approvals and SLA.
- Admins choose which services in a bundle are mandatory or optional, and whether children run in sequence or in parallel.
- The bundle closes when every child request is complete; the requester tracks all parts on one screen.

**Proactive services**

- Services Zelvo starts without the user asking, triggered by a date (for example, 30 days before a permit or contract expires), a schedule, or an event from another system through the API (for example, a new joiner created in the HR system).
- For each proactive service, the admin chooses to notify the person and ask them to confirm, or to start the request automatically.
- Notifications go by email, SMS, WhatsApp or the portal in the person's language.

**Service FAQ**

- Each service has its own FAQ section in English and Arabic, shown on the service page.
- The AI virtual agent answers questions about a service from its FAQ, steps, fees and required documents.

**Form builder (per service)**

- Drag-and-drop fields: text, number, date, dropdown, checkbox, radio, file upload, user picker, asset picker, location, signature, table rows, rich text, and lookups from other systems through the API.
- Rules on any field: show, hide, require or make read-only based on other answers (for example, show "Monitor size" only when "Add a monitor" is ticked).
- Default values filled from the requester's profile (department, manager, location, employee ID).
- Validation: required fields, formats (Emirates ID, phone, email, IBAN), min/max values, file type and size.
- Reusable field sets (for example, "Requester details") shared across many forms; a change updates every form that uses it.
- Every field label and option in English and Arabic; live preview on desktop and mobile before publishing.
- The same form builder customizes incident, complaint, inquiry and suggestion forms, so each category can ask its own questions.

**Fulfillment per service**

- **Initial assigned group:** each service names the team that receives new requests first; form answers can override it (for example, "Location = Abu Dhabi" goes to the Abu Dhabi team).
- **Service-based SLAs:** each service has its own targets, overriding the general SLA policy:
  - *Ownership SLA* — time for an agent to accept and take ownership of the request.
  - *Completion SLA* — time to complete the service, defaulting to the service's number of days.
  - Both use working calendars and pause while waiting for the requester or an approval; escalations fire at thresholds the admin sets.
- **Approvals at any stage:** each service can have approvals:
  - *Before assignment* — the request goes to agents only after approval (for example, manager or budget approval).
  - *After fulfilment* — the agent's completed work is checked and signed off before the request closes (for example, quality or supervisor sign-off).
  - Approvals can be single, multi-level, parallel, or chosen by role, amount or form answer; approve from email, Teams, WhatsApp or the portal; rejection returns the request with a reason.
- **Fulfilment tasks:** each service has its own task template, assigned to one or more teams in sequence or in parallel, each task with its own target time.
- *Story:* As an employee, I want to request a new laptop from a catalog with my manager's approval routed automatically, so that I don't chase anyone.
- *Story:* As an admin, I want to define a new service with its documents, fees, steps, SLAs and approvals in one place, so that each department can publish its own services without developers.

### 3. Customer voice: complaints, inquiries and suggestions

- Three dedicated record types with their own forms, categories, SLAs and closure flow.
- Complaints: severity, root-cause category, mandatory resolution summary, customer confirmation before closure.
- Inquiries: fast answer flow with knowledge suggestions; convert to a request or complaint when needed.
- Suggestions: review states (Under review, Accepted, Implemented, Declined) with feedback to the submitter.
- Anonymous submission option, configurable per tenant.
- *Story:* As a citizen, I want to track my complaint and be told how it was resolved, so that I trust the entity listened.

### 4. Self-service portal

- Search-first home page ("How can we help?") across catalog, knowledge and open tickets.
- My tickets, my approvals, status tracking with a visual progress bar.
- Login by UAE PASS, single sign-on, email + one-time code, or anonymous for public complaints.
- Tenant branding: logo, colors, domain; fully responsive on mobile.
- *Story:* As a user, I want to see exactly where my request is and who has it, so that I don't need to call.

### 5. Agent workspace

- One screen: ticket list, ticket detail, customer history and AI assistant side by side; no full page reloads.
- Saved views and filters, bulk actions, keyboard shortcuts.
- Public replies and internal notes, @mentions, canned responses in both languages.
- Live SLA timers with color warnings before breach.
- *Story:* As an agent, I want to see the customer's previous tickets beside the current one, so that I don't ask the same questions again.

### 6. Omnichannel intake

- Email-to-ticket with threading, multiple mailboxes per tenant.
- Live chat widget for the portal and external websites, with agent handover from the AI assistant.
- WhatsApp Business: create and update tickets, receive notifications, send voice notes (transcribed).
- Microsoft Teams: raise tickets and approve requests.
- REST API and webhooks for any other channel.
- *Story:* As a customer, I want to send a WhatsApp message and get a ticket number back, so that I can use the app I already have.

### 7. Knowledge base

- Articles in English and Arabic with draft, review, published and retired states.
- Internal (agents only) and external (portal) visibility.
- "Was this helpful?" feedback and view counts; AI drafts articles from resolved tickets for review.
- *Story:* As an agent, I want to turn a resolved ticket into a draft article in one click, so that the next person can self-serve.

### 8. SLA management

- SLA policies by priority, category, customer group and record type (incident, request, complaint and so on).
- Business calendars with UAE weekends and public holidays, per tenant and per team.
- Response and resolution targets, pause on "waiting for customer", escalations at configurable thresholds.
- *Story:* As a team lead, I want to be alerted when a ticket reaches 75% of its SLA, so that I can act before it breaches.

### 9. AI capabilities (included)

- Auto-categorize, prioritize and route new tickets; agents can accept or correct, and corrections improve accuracy.
- Summarize long ticket threads; suggest replies in the agent's chosen language.
- Virtual agent in portal and chat that answers from the knowledge base and hands over to a human.
- Detect duplicate tickets and sentiment; escalate frustrated customers.
- Every AI action is logged; the AI never closes or changes a ticket without a human or a rule the admin approved.
- *Story:* As an agent, I want a suggested reply drafted from the knowledge base, so that I answer faster and consistently.

**AI providers (Settings → AI)**

- AI features stay off until the customer's admin connects a provider; Zelvo ships no AI model of its own.
- The admin picks a provider and enters its API key: Anthropic Claude, OpenAI, Microsoft Azure OpenAI (the API behind Microsoft Copilot), Google Gemini, or any OpenAI-compatible endpoint — including a model the customer hosts on its own servers for offline networks.
- Model chosen per feature (for example, a fast model for triage and a stronger one for summaries), with a test button, a usage and cost dashboard and a monthly spending limit.
- Keys stored encrypted; personal data masked before anything is sent; the provider can be switched at any time without losing data.

**MCP server for agentic AI**

- Zelvo includes a built-in MCP (Model Context Protocol) server, so any agentic AI — Claude, ChatGPT, Microsoft Copilot Studio or the customer's own agents — can work with Zelvo: search knowledge, create and update tickets, check status, raise catalog requests and read SLAs.
- Each AI agent connects with its own credentials and gets only the tools and data its role allows; the admin turns each tool on or off.
- Actions that change data can require a person's approval; every AI action is audit-logged and marked as done by an AI agent.
- Zelvo's own AI can also use the customer's MCP servers (for example, an HR or asset system) as tools, set up in Settings.

### 10. Notifications, reporting and administration

- Notifications by email, SMS, WhatsApp, Teams and in-app, in the recipient's language.
- Dashboards: volumes, backlog, SLA compliance, agent performance, happiness scores; scheduled reports to email.
- Admin: setup wizard, users and groups, roles and permissions, forms, categories, SLA policies, business hours, branding, language defaults.
- No-code workflow builder for assignment, approval and notification rules.
- *Story:* As an administrator, I want to set up a new department's service desk from a template, so that it's live the same day.

### 11. White-label branding

Zelvo is the platform name; each customer runs it under its own brand, set by its admin in Settings with no code and no help from us.

- Product name in English and Arabic, shown in the portal, agent workspace, browser tab, emails and chat widget.
- Logos for light and dark mode, favicon and login page image.
- Brand colors and a choice of fonts, applied across all screens with accessible contrast checked automatically.
- Custom domain (for example, support.customer.ae) with an automatic TLS certificate.
- Email sender name and the customer's own email domain (SPF, DKIM, DMARC set up through a guided check).
- Branded email, WhatsApp, SMS and notification templates; branded PDF reports and exports.
- Live preview of every change before it is published.
- A "Powered by Zelvo" mark in the portal footer, which is always shown and cannot be removed or changed in Settings.
- *Story:* As a customer admin, I want to upload our logo and set our own product name, so that our employees and citizens only ever see our brand.

### 12. Licensing and copy protection

Each customer hosts Zelvo on its own servers, so every installation must be activated with a license that we issue and that only works on that customer's servers.

- **One-time, permanent license:** no expiry, no subscription and no renewals; once activated, the installation runs for good.
- **Fully offline:** activation and every license check work without internet access, so air-gapped government networks are supported as standard. Zelvo never contacts us.
- **Compiled delivery only:** customers receive signed Docker images, never source code; .NET code is obfuscated and the frontend ships as minified builds.
- **Signed license file:** signed with our private key (Ed25519 or RSA-4096) and verified by every service at startup. It holds the customer name, license ID, licensed modules and installation fingerprint. Any edit breaks the signature.
- **Activation during setup:** the installer collects a fingerprint of the customer's servers (cluster ID, machine IDs, database ID, licensed domain) and saves it as an activation request file. The customer sends us that file; we return a license file bound to that fingerprint, so the same license fails on any other server.
- **Offline signing tool for us:** a small tool kept on our own computer (no portal, no server) that turns an activation request into a signed license file. The private key never leaves that computer.
- **Server moves:** if a customer replaces its servers, it sends a new activation request and we issue a replacement license for the new fingerprint.
- **Tamper detection:** services verify each other's image signatures; a modified or unlicensed component refuses to start and the event is logged.
- **Hidden customer watermark:** every build and license carries a unique customer ID embedded in the code, database and generated files, so a leaked copy can be traced back to the customer it was issued to.
- **Legal protection:** the license agreement forbids copying, resale, reverse engineering and moving to unlicensed servers, with penalties and audit rights. Technical controls slow copying down; the contract and the watermark make it costly.
- *Story:* As the Zelvo vendor, I want each installation locked to the customer's own servers, so that a copy given to another company does not run.

### 13. Offline update packages

Customers update Zelvo by downloading a signed update package and importing it in Settings; security updates are free and feature updates are paid.

| Update type | Version change | Cost | Unlocked by |
| --- | --- | --- | --- |
| Security and bug fix | 1.4.0 → 1.4.1 | Free, for every licensed customer | The existing license |
| Feature release | 1.4 → 1.5, or 1 → 2 | Paid | An upgrade license file we issue after payment |

- **Package format:** one file (for example, zelvo-1.5.0.zpkg) holding the Docker images, database migration scripts, translations and a manifest with version, type and release notes. Signed with our private key; any change breaks the signature.
- **Import in Settings:** an admin uploads the package under Settings → Updates. Works fully offline: it can be downloaded on another computer and carried in on USB for air-gapped networks.
- **Checks before install:** signature valid; package newer than the installed version; for a feature release, a matching upgrade license is present. A failed check stops the import with a clear reason.
- **Safe install:** automatic database and configuration backup first, then a step-by-step progress screen; if any step fails, Zelvo rolls back to the previous version automatically.
- **Maintenance window:** admins can schedule the install for off-hours and show a notice banner to users beforehand.
- **Upgrade license:** for a paid feature release, the customer's admin sends us their current activation details; our offline signing tool issues an upgrade license that unlocks that version on that installation only.
- **History:** every import, install and rollback is recorded in the audit log, with who did it and when.
- *Story:* As a customer admin on an air-gapped network, I want to import a security update from a USB drive, so that we stay protected without connecting to the internet.

**Release distribution:** every customer contacts us at first setup to get its license file, so we hold each customer's contact details from day one. When a new release is ready, we email customers a notification with release notes, and they download the package from the customer download area on the Zelvo website. Customer records, licenses and installed versions will be tracked in a Zelvo customer CRM, planned for later.

### 14. Integrations and developer tools

Every feature in Zelvo is reachable through a documented API, so customers can connect their CRM, ERP, HR and monitoring systems without help from us.

**API and events**

- REST API covering every record type (incidents, requests, complaints, inquiries, suggestions, users, groups, knowledge, SLAs, catalog), with OpenAPI documentation built into Zelvo.
- Authentication with OAuth 2.0 client credentials or API keys; each key has its own scopes and role, can be revoked, and every call is audit-logged.
- Outgoing webhooks on any event (ticket created, status changed, SLA breached, approval decided), with signed payloads, retries and a delivery log.
- Rate limits per key, configurable by the admin, to protect performance.
- Bulk import and export through the API, CSV and Excel.

**Ready-made connectors (Phase 1)**

| System type | Connectors | What syncs |
| --- | --- | --- |
| CRM | Microsoft Dynamics 365, Salesforce, HubSpot, Zoho CRM | Customers and contacts into Zelvo; ticket status and history back to the CRM record |
| Identity | Microsoft Entra ID, Active Directory / LDAP, UAE PASS | Users, departments, managers, groups and login |
| Messaging | Email (Exchange, Microsoft 365, Gmail, SMTP/IMAP), WhatsApp Business, Microsoft Teams, SMS gateways | Messages in and out as ticket updates |
| Monitoring | Generic alert webhook (works with Zabbix, Prometheus, Datadog and others) | Alerts become incidents automatically |

**No-code connector builder**

- Admins connect any other system with a REST API: set the URL, authentication and field mapping on screen, then call it from a workflow step (for example, "when a complaint is closed, update the case in our CRM").
- Test button, sample responses and error messages shown on screen before saving.

**Developer tools**

- API explorer inside Zelvo to try any call with the admin's own test key.
- Webhook tester and full integration log: every call in and out, with status, timing and payload (personal data masked).
- Custom fields and forms through the admin screens, all exposed in the API automatically.
- A separate test installation allowed under every license, so integrations are built and tested away from live data.

**Later phases:** custom record types and a low-code app builder (Phase 2); sandboxed server-side scripts for advanced logic (Phase 3); SDKs for .NET, JavaScript and Python (Phase 3).

- *Story:* As a customer's IT team, I want complaints raised in Zelvo to appear on the customer's record in our CRM, so that our account managers see every complaint without logging in to Zelvo.

### 15. Online payments

Each customer can switch online payments on or off in Settings and connect its own payment gateway, such as AjmanPay for Ajman Government entities.

**Settings → Payments**

- Payments switch: off by default. When off, service fees are shown for information only and no payment step appears.
- Connect one or more gateways through a payment connector: gateway name, API address, merchant ID and credentials (stored encrypted), success, failure and callback addresses, signature method, and test or live mode.
- "Test connection" and a test payment in sandbox mode before going live.
- One generic payment connector, no gateway-specific templates: the customer's admin configures any gateway that offers a payment API (for example, AjmanPay) using the gateway's own documentation, including request and response field mapping.

**Payment flow**

1. The requester submits a paid service; Zelvo shows the fee and a "Pay now" button.
2. Zelvo sends the amount and a unique reference to the gateway and redirects the requester to the gateway's payment page.
3. The gateway returns the requester to Zelvo with a success or failure result and also notifies Zelvo directly.
4. Zelvo confirms the result with the gateway's status API before marking the request paid; the request then moves on to approval or assignment.
5. On failure or cancellation, the request stays in "Awaiting payment" and the requester can retry; unpaid requests expire after a time the admin sets.

**Per-service payment settings**

- Fee fixed, or calculated from form answers (for example, urgent delivery adds a fee).
- When to pay: on submission, after approval, or on completion.
- Which gateway to use, if the customer has more than one.

**Receipts, refunds and finance**

- Bilingual e-receipt with transaction ID, emailed and available in the portal.
- Refunds recorded with a reason, sent through the gateway's refund API where it offers one, otherwise marked as a manual refund.
- Daily reconciliation report matching Zelvo payments to gateway transactions; export to Excel for finance.

**Security**

- Card details are entered only on the gateway's own page; Zelvo never sees or stores card data.
- Every gateway response is verified (signature and status check) and processed once only, so a payment can never be counted twice.
- Every payment event is written to the audit log.
- Network access between the gateway and the customer's Zelvo server (firewall rules, public address, certificates) is the customer's responsibility as the system owner.
- *Story:* As a citizen, I want to pay the service fee online through AjmanPay when I submit my request, so that my request starts without visiting a service centre.

### Demo mode (built alongside Phase 1, extended in each phase)

A demo installation with realistic sample data lets the product owner show Zelvo to prospective customers as a working product.

- Separate demo installation with its own demo license; never mixed with real customer data.
- Sample data in English and Arabic: a sample government entity and a sample private company, each with branding, departments, users, agents, service catalog (including bundles, proactive services, fees and FAQs), SLAs, knowledge articles, and several months of tickets, complaints, inquiries and suggestions.
- Dashboards and reports filled with realistic trends, including SLA results and happiness scores.
- Ready-made demo users for each role (requester, agent, team lead, approver, admin, executive) with one-click switching between them.
- Payment gateway in sandbox mode, so the payment flow can be shown without real money.
- "Reset demo" button that restores the clean sample state in a few minutes before each meeting.
- Sample data grows with each phase (problems, changes, CMDB, status page and so on).
- *Story:* As the product owner, I want to reset the demo before a meeting and switch between citizen, agent and manager views, so that prospects see the full journey in a few minutes.

## Phase 2 functional requirements — Control

Phase 2 adds the ITIL control practices: problem, change, configuration and assets, plus mobile apps and smarter automation.

### 16. Problem management

- Create problems from one or many incidents, manually or from AI suggestions when similar incidents repeat.
- States: New, Assessment, Root-cause analysis, Known error, Resolved, Closed.
- Root-cause tools: 5 Whys and fishbone templates, timeline of linked incidents and changes.
- Known error database: each known error has a workaround that agents see on matching incidents and that the portal can show to users.
- Proactive problems from trend reports (for example, the same category spiking each month).
- Link problems to incidents, changes, configuration items and knowledge articles.
- *Story:* As a problem manager, I want Zelvo to flag that 40 incidents share the same cause, so that we fix the cause instead of each ticket.

### 17. Change management

- Change types: standard (pre-approved, from a catalog of standard changes), normal and emergency, each with its own workflow.
- Risk and impact questionnaire that calculates a risk score; risk decides the approval path.
- Change advisory board: meetings, agenda built automatically from pending changes, votes and minutes; emergency board for urgent changes.
- Change calendar with conflict detection (same configuration item, same window) and blackout periods.
- Implementation, test and back-out plans; change tasks for each team; post-implementation review.
- **What-if simulation:** before approval, show the blast radius from the CMDB (services and users affected), similar past changes and their outcomes, and a predicted risk.
- *Story:* As a change manager, I want to see every service a change will touch before the board meets, so that we approve it with full knowledge.

### 18. CMDB and asset management

- Configuration item classes (hardware, software, business services, applications, network, cloud resources) with custom attributes per class.
- Relationships between items (runs on, depends on, connects to) and a visual relationship map.
- Import and sync from Excel, the API and discovery tools; duplicate detection and reconciliation.
- Asset lifecycle: requested, ordered, received, in stock, assigned, in repair, retired, disposed.
- Contracts, warranties and software licenses with expiry alerts; software license compliance (installed vs owned).
- Barcode and QR labels, scanned from the mobile app.
- Assets assigned to users and shown on their tickets.
- *Story:* As an agent, I want to see the user's laptop, its warranty and its past incidents on the ticket, so that I decide repair or replace quickly.

### 19. Mobile apps and Microsoft Teams

- Native iOS and Android apps for requesters and agents: raise and track requests, approve, chat, push notifications, camera attachments, barcode scanning, biometric login.
- The app connects to the customer's own Zelvo server by entering its address or scanning a QR code; it shows the customer's branding after connecting.
- Microsoft Teams app: raise and track tickets, approve requests and receive notifications inside Teams.
- *Story:* As a manager, I want to approve requests from my phone, so that nothing waits for me to reach a computer.

### 20. Workflow automation

- **Plain-language rules:** an admin describes a rule in English or Arabic; Zelvo's AI builds the workflow and shows it visually for review; it only goes live after the admin approves it.
- Custom record types and a low-code app builder, so customers can build their own processes (for example, facilities or HR requests) on the same platform.
- *Story:* As an admin, I want to type "when a VIP raises a critical incident, notify the IT manager on WhatsApp" and get a working rule, so that I don't build it step by step.

### 21. Agent wellbeing

- Dashboard for team leads: workload per agent, overtime and after-hours work, share of angry or escalated tickets handled.
- Alerts and suggested reassignment when an agent is overloaded.
- Visible to team leads and above only.
- *Story:* As a team lead, I want to see who is overloaded this week, so that I rebalance work before people burn out.

## Phase 3 functional requirements — Scale

Phase 3 adds event handling, a full virtual agent, AI-driven improvement and an integration library.

### 22. Event management

- Receive alerts from monitoring tools through webhooks, email or the API.
- Remove duplicates and group related alerts; create an incident automatically when an alert meets the admin's rules.
- Close the incident automatically when the alert clears, if the admin allows it.
- Maintenance windows suppress alerts for items under planned change.

### 23. Virtual agent (full)

- Conversational assistant in the portal, chat widget, WhatsApp, Teams and mobile app, in English and Arabic.
- Completes catalog requests by conversation, checks status, answers from knowledge and service FAQs, and performs approved actions (for example, a password reset through an integration).
- Hands over to a human agent with the full conversation and collected details.

### 24. AI post-incident reviews

- For major incidents, Zelvo drafts the review: timeline from ticket, chat and alert history, impact, root-cause draft and action items.
- The incident manager edits and approves it; action items become tracked tasks.

### 25. Continual improvement and customer voice insights

- AI groups complaints and suggestions into themes and shows trends by service, channel and department.
- Themes become items in a continual improvement register (as ISO/IEC 20000 requires), with owner, target date and status.
- Submitters are told when their suggestion leads to a change.
- Experience surveys and XLA (experience-level agreement) reporting alongside SLAs.

### 26. Public status page

- A page per customer showing each service's status (operational, degraded, outage) and current incidents, in the customer's branding and both languages.
- Users subscribe to updates by email, SMS or WhatsApp; the portal shows active outages before a user raises a duplicate ticket.

### 27. Integration library

- Extra connectors delivered as signed packages that the admin imports in Settings, the same way as update packages, so they work offline.
- Each package declares the data it reads and writes; the admin approves it before installing.

### 28. Developer extensions

- Sandboxed server-side scripts for advanced logic, with limits on time and resources and no access outside approved APIs.
- SDKs for .NET, JavaScript and Python.

## Phase 4 functional requirements — Expand

Phase 4 adds automated fixing, multi-client operation, richer user diagnostics and certification.

### 29. Self-healing runbooks

- Admins define runbooks (scripts or API calls, for example restart a service or clear disk space) and approve them before use.
- Rules or the AI can run an approved runbook when a matching incident or alert arrives; risky runbooks need a person's approval each time.
- Dry-run mode, step-by-step output on the ticket, automatic rollback where defined, and full audit trail.

### 30. Multi-client (MSP) mode

- One Zelvo installation serves several client organisations: separate portals, branding, catalogs, SLAs and reports per client.
- Agents see only the clients they are assigned to.
- Per-client billing reports (tickets, time spent, SLA results).

### 31. Screen recording and co-browse

- Users record their screen and voice from the portal in one click; the recording is attached to the ticket.
- Agents co-browse the user's portal session with the user's consent: view and highlight, with control only when the user allows it.

### 32. Certification readiness

- Product prepared for PinkVERIFY and PeopleCert ITIL tool assessments.
- Built-in ISO/IEC 20000 evidence reports (process records, reviews, improvement register).
- Company-side: ISO 27001 and SOC 2 for the Zelvo business.

## UAE and government requirements

These requirements make the platform ready for UAE government tenders; they are configurable, so private-sector tenants can switch them off.

| ID | Requirement | Notes |
| --- | --- | --- |
| GOV-1 | Login with UAE PASS for portal users and staff | Integration terms to confirm with the UAE PASS programme |
| GOV-2 | Happiness meter on ticket closure and in the portal | Rating scale and wording configurable to the entity's customer-happiness charter |
| GOV-3 | Happiness and customer-voice reporting | Complaints, inquiries, suggestions and happiness trends per service and channel |
| GOV-4 | Data hosted in the UAE | Each customer hosts Zelvo on its own servers or its own UAE cloud account |
| GOV-5 | Compliance with the UAE Personal Data Protection Law and sector rules | Consent, retention periods, data subject requests, export and deletion |
| GOV-6 | Alignment with the UAE Information Assurance standard | Control mapping delivered as evidence for security reviews |
| GOV-7 | UAE business calendar | Friday half-day / Saturday–Sunday weekend patterns and public holidays per tenant |
| GOV-8 | Arabic official correspondence | Arabic email and notification templates with formal tone, entity letterhead |
| GOV-9 | Accessibility | WCAG 2.2 AA in both languages |
| GOV-10 | Integration with government channels | Entity websites, call centres and existing case systems via API |

Open point: confirm with prospective government customers which reporting formats they submit to their regulator, so that GOV-3 produces them directly.

## Non-functional requirements

Security and tenant isolation are release blockers: Phase 1 does not go live until they pass an external penetration test.

| Area | Requirement |
| --- | --- |
| Security standard | OWASP ASVS Level 2 for all tenants; Level 3 controls available for government tenants |
| Tenant isolation | Enforced in the database (PostgreSQL row-level security), not only in application code |
| Encryption | TLS 1.3 in transit; AES-256 at rest with keys in a managed key vault |
| Authentication | SSO (SAML/OIDC), UAE PASS, mandatory MFA for agents and admins |
| Authorization | Role-based access down to field level; least privilege by default |
| Audit | Tamper-proof audit log of every create, change, view of sensitive data and admin action |
| AI safety | Personal data masked before reaching the model; prompt-injection guards; human approval for actions |
| Secure delivery | Code, dependency and container scanning in every build; software bill of materials; yearly penetration test |
| Performance | Page load under 1 s; API under 200 ms at the 95th percentile; 1,000 concurrent agents per tenant |
| Availability | 99.9% monthly uptime target; zero-downtime deployments |
| Backup and recovery | Daily backups kept 35 days; recovery point under 15 minutes, recovery time under 4 hours |
| Scalability | Stateless services scaled horizontally on Kubernetes; read replicas for reporting |
| Deployment | Docker images for cloud (UAE region), dedicated tenant, and on-premises installs |
| Accessibility | WCAG 2.2 AA, keyboard-only use, screen readers in English and Arabic |
| Compliance targets | ISO 27001 and SOC 2 for the company; PinkVERIFY / PeopleCert ATV for the product |

**Reference stack:** ASP.NET Core (.NET 10 LTS), Next.js + TypeScript, PostgreSQL with pgvector, OpenSearch, Redis, RabbitMQ, SignalR, Keycloak, an AI gateway in front of the LLM provider, Docker and Kubernetes, OpenTelemetry with Grafana.

**Server sizing** — published to customers as the minimum specification; estimates to be confirmed by load testing before release. AI runs at the customer's chosen provider, so no GPU is needed.

| Size | Agents | Servers | Each server |
| --- | --- | --- | --- |
| Small | Up to 50 | 1 server, Docker Compose | 8 vCPU, 32 GB RAM, 500 GB SSD |
| Medium | Up to 300 | 3 application servers + 1 database server, Kubernetes | Application: 8 vCPU, 16 GB RAM; database: 8 vCPU, 64 GB RAM, 1 TB SSD |
| Large | More than 300 | Kubernetes cluster: 5 or more application nodes, database primary + replica, separate search nodes | Sized per customer |

Operating system: Linux (Ubuntu Server LTS or Red Hat Enterprise Linux). Attachment storage grows with use and is planned separately. A customer that hosts its own AI model needs a separate GPU server sized for that model.

## Differentiators and roadmap

All four phases are in scope; the phases set the build order, with Phase 1 carrying the features that win UAE government deals.

&#91;embedded content: product roadmap · 4 phases, 1 gate\]

Each phase ends with a review and approval by the product owner; Phase 1 must also pass an external penetration test before Phase 2 starts.

| Differentiator | Why it wins | Phase |
| --- | --- | --- |
| Arabic-first AI and RTL design | Understands Gulf dialect and Arabic voice notes; layout designed, not mirrored | 1 |
| UAE government readiness | UAE PASS, happiness meter and customer voice built in | 1 |
| Live in a day, AI included in price | Answers the cost and implementation complaints about ServiceNow | 1 |
| Plain-language workflow builder | Admin describes a rule in words; the system builds it | 2 |
| Change what-if simulation | Shows blast radius and predicted risk before approval | 2 |
| Agent wellbeing dashboard | Rebalances workload before agents burn out | 2 |
| AI post-incident reviews | Timeline, root-cause draft and actions generated automatically | 3 |
| Suggestions into improvement backlog | AI clusters customer voice into ISO 20000 continual improvement | 3 |
| Public status page | Users see outages before raising duplicate tickets | 3 |
| Screen recording and co-browse | Faster diagnosis, with the user's consent | 4 |
| Self-healing runbooks | AI runs admin-approved fixes, fully audited | 4 |
| MSP multi-client mode | One console for many client tenants with per-client SLAs | 4 |

## Open questions and next steps

All decisions are settled; development can start. Zelvo is built first and shown to prospective customers once ready.

**Open questions**

None remaining.

Decided: customer records, licenses and installed versions are kept in an Excel sheet for now; a CRM comes later.

**Next steps**

- [ ] Product owner approves the full requirements (all four phases).
- [ ] Start development in Claude Code, phase by phase, with a review and approval after each phase.
- [ ] Book an external penetration test for the end of Phase 1.
