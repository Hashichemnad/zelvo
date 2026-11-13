# Phase 1 build plan (approved 2026-11-13)

Phase 1 delivers modules 1–15 and demo mode from [requirements.md](requirements.md). This plan breaks it into tasks of one branch and one pull request each, in build order. Each task follows CLAUDE.md: plan first, build with tests, run in Docker Compose, update Progress.

**How to read it:** tasks in the same milestone that do not depend on each other can run in parallel. Large modules are split so each pull request stays reviewable. Every UI task ships English and Arabic, RTL and WCAG 2.2 AA from the start; every API task ships OpenAPI documentation and audit logging. Demo data (task D) grows with each module rather than being built at the end.

## Milestone A: Platform foundation

Everything else builds on these. They cover the release-blocking non-functional requirements: tenant isolation, audit, identity and security.

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| A1 | `phase1/a01-data-tenancy-audit` | EF Core with migrations; tenants; PostgreSQL row-level security driven by a per-request tenant setting, with tests proving a query cannot cross tenants; tamper-evident (hash-chained) audit log for create, change, admin action and sensitive view; shared bilingual value type (English + Arabic) with missing-translation reporting; API conventions (versioning, problem details, paging, validation); OpenAPI explorer in the app | NFR tenant isolation, audit; LANG-5 (type); module 14 (OpenAPI) | setup |
| A2 | `phase1/a02-identity-access` | Keycloak OIDC for the web app (server-side session, http-only cookies, CSRF protection) and JWT for the API; the six personas as roles; role- and field-level permission model; MFA required for agents and admins; email + one-time code login; anonymous access for public forms | NFR authentication, authorization; module 4 (login) | A1 |
| A3 | `phase1/a03-settings-localization` | Tenant settings store and Settings shell; tenant default language (LANG-1); user profile with personal language, calendar (Gregorian/Hijri) and digits (LANG-2, LANG-8); full display-language resolution order; formal Arabic tone guidelines for templates | LANG-1, 2, 3, 8; GOV-8 (base) | A2 |
| A4 | `phase1/a04-search` | OpenSearch indexing pipeline through RabbitMQ; Arabic and English analysers with ICU normalisation (diacritics, letter forms); tenant-isolated indices; search API used by later modules | LANG-9 | A1 |
| A5 | `phase1/a05-observability-security-baseline` | OpenTelemetry traces, metrics and logs; Prometheus and Grafana in Compose; rate limiting; encryption-at-rest pattern for secrets stored in the database (API keys, gateway credentials); OpenSearch and RabbitMQ security on; ASVS L2 checklist started | NFR encryption, performance monitoring, secure delivery | A1 |
| D | `phase1/d01-demo-mode-foundation` | Demo installation profile with its own (stub) license kind; seeding framework; demo users per role with one-click switching; "Reset demo". Extended by every later module with its sample data | Demo mode | A2, A3 |

## Milestone B: Ticket core and the agent's day

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| B1 | `phase1/m01-incidents` | Shared ticket core (numbering, states, history, attachments, parent/child); incidents with the configurable impact × urgency matrix, states incl. on-hold reasons and reopen; major-incident flag and communication timeline; field-level audit history | Module 1 | A1–A3 |
| B2 | `phase1/m10-admin-directory` | Users, groups, roles, categories, locations, skills; assignment rules (category, location, language, skill) and round-robin | Modules 1, 10 | A2, B1 |
| B3 | `phase1/m08-sla` | SLA policies by priority, category, customer group and record type; business calendars with UAE weekend patterns and public holidays per tenant and team; response and resolution targets; pause on waiting; escalations at thresholds; timers in the worker | Module 8; GOV-7 | B1, B2 |
| B4 | `phase1/m05-agent-workspace` | One-screen workspace (list, detail, customer history, AI panel placeholder) without page reloads; saved views, filters, bulk actions, keyboard shortcuts; public replies, internal notes, @mentions, bilingual canned responses; live SLA timers with warnings over SignalR | Module 5; LANG-7 (side-by-side languages) | B1–B3 |
| B5 | `phase1/m10-notifications` | Notification engine: email and in-app first, in the recipient's language; bilingual templates with formal Arabic and entity letterhead; SMS, WhatsApp and Teams channels plug in with module 6 | Module 10; LANG-6; GOV-8 | A3, B1 |

## Milestone C: Catalog, forms and customer voice

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| C1 | `phase1/m02-form-builder` | Drag-and-drop form builder with all field types, rules (show, hide, require, read-only), profile defaults, validation (Emirates ID, phone, email, IBAN), reusable field sets, bilingual labels, desktop/mobile preview; used by incidents, requests, complaints, inquiries and suggestions | Module 2 (forms) | B1 |
| C2 | `phase1/m02-catalog-builder` | Catalogs, categories and the main service → sub-service hierarchy; full service card; lifecycle (draft → retired), copy and templates; access control per service (also hidden from search, API and virtual agent); request on behalf; service FAQ; Excel/API import and export | Module 2 (catalog) | C1, A4 |
| C3 | `phase1/m10-workflow-approvals` | Workflow engine integration (Elsa, see decisions) and the no-code builder for assignment, approval and notification rules; approvals: before assignment and after fulfilment, single, multi-level, parallel, by role, amount or answer; approve from email and portal (Teams and WhatsApp with module 6) | Modules 2, 10 | B2, B5, C2 |
| C4 | `phase1/m02-fulfilment` | Service requests: initial group with answer-based overrides, ownership and completion SLAs per service, fulfilment task templates in sequence or parallel, service steps shown as the requester's journey | Module 2 (fulfilment) | B3, C3 |
| C5 | `phase1/m02-bundles-proactive` | Bundles (combined form, child requests, mandatory/optional, sequence/parallel, one tracking screen); proactive services triggered by date, schedule or API event, notify-and-confirm or auto-start | Module 2 | C4 |
| C6 | `phase1/m03-customer-voice` | Complaints, inquiries and suggestions with their own forms, categories, SLAs and closure flows; anonymous submission per tenant; happiness meter on closure and in the portal; happiness and customer-voice reporting | Module 3; GOV-2, GOV-3 | B3, C1 |

## Milestone D: Self-service and knowledge

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| D1 | `phase1/m07-knowledge` | Bilingual articles with draft, review, published and retired states; internal and external visibility; feedback and view counts; "turn this ticket into an article" | Module 7 | A4, B1 |
| D2 | `phase1/m04-portal` | Search-first home across catalog, knowledge and open tickets; my tickets and my approvals with progress bar; responsive, raise a ticket in ≤ 3 clicks / 60 s | Module 4 | C2, C6, D1 |
| D3 | `phase1/m04-uae-pass` | UAE PASS login for portal users and staff through Keycloak (integration terms to confirm with the UAE PASS programme) | GOV-1 | A2 |

## Milestone E: Channels, AI and integrations

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| E1 | `phase1/m14-api-access-webhooks` | API keys and OAuth client credentials with scopes, roles, revocation and per-key rate limits; outgoing signed webhooks with retries and delivery log; bulk import/export (CSV, Excel); integration log with masked personal data; webhook tester; separate test installation support | Module 14 | A1, A2 |
| E2 | `phase1/m06-email-chat` | Email-to-ticket with threading and multiple mailboxes (Exchange/M365, Gmail, IMAP/SMTP); live chat widget for the portal and external sites | Module 6 | B1, B5 |
| E3 | `phase1/m06-whatsapp-teams-sms` | WhatsApp Business (create/update tickets, notifications, voice notes transcribed once AI is on); Microsoft Teams (raise tickets, approve); SMS gateways | Module 6 | E2, C3 |
| E4 | `phase1/m09-ai-gateway` | Settings → AI: providers (Claude, OpenAI, Azure OpenAI, Gemini, OpenAI-compatible/self-hosted), encrypted keys, model per feature, test button, usage and cost dashboard, monthly limit; personal-data masking and prompt-injection guards; AI off until configured; every AI action logged | Module 9 | A5 |
| E5 | `phase1/m09-ai-features` | Auto-categorise, prioritise and route with accept/correct feedback; thread summaries; suggested replies in the agent's language; one-click translation; duplicate and sentiment detection; virtual agent in portal and chat with human handover; article drafts from tickets | Module 9; LANG-7, LANG-10 | E4, B4, D1, E2 |
| E6 | `phase1/m09-mcp-server` | Built-in MCP server (search knowledge, tickets, status, catalog requests, SLAs) with per-agent credentials, per-tool switches and human approval for changes; Zelvo AI using the customer's MCP servers | Module 9 | E1, E4 |
| E7 | `phase1/m14-connectors` | Identity (Entra ID, AD/LDAP), CRM (Dynamics 365, Salesforce, HubSpot, Zoho), monitoring alert webhook → incidents; no-code REST connector builder used from workflow steps | Module 14 | E1, C3 |
| E8 | `phase1/m15-payments` | Settings → Payments: generic gateway connector (e.g. AjmanPay) with mapping, test connection and sandbox; per-service fee rules and timing; pay flow with server-side status confirmation and idempotent processing; bilingual e-receipts, refunds, daily reconciliation and Excel export | Module 15 | C4, E1 |

## Milestone F: Product, branding and delivery

| # | Branch | Scope | Covers | Depends on |
| --- | --- | --- | --- | --- |
| F1 | `phase1/m11-branding` | Product name (EN/AR), logos, favicon, login image, colours and fonts with automatic contrast checks, live preview; custom domain with automatic TLS; email sender domain with SPF/DKIM/DMARC guided check; branded templates and PDF exports; "Powered by Zelvo" always shown | Module 11 | A3, B5 |
| F2 | `phase1/m10-reporting-setup-wizard` | Dashboards (volumes, backlog, SLA compliance, agent performance, happiness) and scheduled reports by email; setup wizard and department templates so a tenant is live in under a day | Module 10 | C6, B3 |
| F3 | `phase1/m12-licensing-integration` | Activation step in the setup wizard and license import screen, calling `ILicenseService`; module gating across the UI and API. The verification, fingerprinting, watermark and signing tool are built in the private `zelvo-licensing` repository in parallel | Module 12 | F2 |
| F4 | `phase1/m13-update-packages` | Settings → Updates: package import, checks through `ILicenseService` (signature, version, upgrade license), automatic backup, step-by-step install with automatic rollback, maintenance window and banner, history in the audit log | Module 13 | F3 |
| F5 | `phase1/gov-privacy-compliance` | UAE PDPL: consent, retention periods, data subject requests, export and deletion; UAE IA control mapping as evidence | GOV-5, GOV-6 | A1, B1 |
| F6 | `phase1/release-packaging` | Hardened Compose for small installs; Helm charts for medium and large; release pipeline with the private licensing project, signed images, obfuscation and minified builds; backup and restore (RPO 15 min, RTO 4 h) | NFR deployment, backup; module 12 (compiled delivery) | F4 |
| F7 | `phase1/release-readiness` | Load tests (API p95 < 200 ms, 1,000 concurrent agents), accessibility audit in both languages, ASVS L2 review, demo walkthrough; then the external penetration test | NFR performance, security, accessibility | all |

## Product owner decisions (2026-11-13)

1. **Plan approved** (order, splitting and scope per task).
2. **Workflow engine:** Elsa Workflows 3 approved. It is confirmed with a short spike at the start of C3, with Temporal as the fallback.
3. **Cache:** Valkey (BSD, Redis-protocol compatible) approved in place of Redis, because customers receive our Docker images. The client code does not change.
4. **Arabic product wording:** "زيلفو" is the Arabic product name and "بتقنية زيلفو" is "Powered by Zelvo".
