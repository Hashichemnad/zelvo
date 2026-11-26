# UAE Information Assurance control mapping (GOV-6)

Evidence for customers' security reviews: how Zelvo supports the control families of the **UAE Information Assurance Regulation** (TDRA; formerly the UAE IA Standard). It maps families and the main areas within them; control numbers differ between editions, so reviewers should check each line against the edition their entity applies. Last reviewed with task F5 (2026-11-25).

**Responsibility**: **Zelvo** (the product provides the control), **Shared** (the product provides the means, the customer sets it up or runs it), **Customer** (the customer's organisation, hosting or processes; Zelvo is self-hosted, GOV-4).

## Management controls

| Family | Area | Responsibility | How Zelvo supports it | Evidence |
| --- | --- | --- | --- | --- |
| M1 Strategy and planning | Information security policy, roles | Customer | — | Customer's ISMS |
| M2 Risk management | Risk assessment of the service | Shared | Threat model and security baseline; ASVS L2 checklist with status per requirement | `docs/architecture/security-baseline.md`, `docs/security/asvs-l2-checklist.md` |
| M3 Awareness and training | Staff training | Customer | Bilingual UI and documentation | — |
| M4 Human resources security | Joiners, movers, leavers | Shared | Users disabled at once (API refuses disabled users even with a valid token); roles removed; bulk import/export of users; erasure of leavers' personal data (F5) | `docs/architecture/identity-access.md`, `docs/architecture/privacy.md` |
| M5 Compliance | Legal and regulatory (PDPL), audit | Shared | Privacy notice and consent, retention periods, data subject requests, export and erasure; tamper-evident audit log with verification | `docs/security/uae-pdpl-mapping.md`, `docs/architecture/data-tenancy-audit.md` |
| M6 Performance evaluation and improvement | Monitoring of controls, internal audit | Shared | Audit log search and chain verification (`GET /audit/verification`); Grafana dashboards and alert rules; reports | `docs/architecture/observability.md`, `docs/architecture/reporting.md` |

## Technical controls

| Family | Area | Responsibility | How Zelvo supports it | Evidence |
| --- | --- | --- | --- | --- |
| T1 Asset management | Information classification and handling | Shared | Personal data fields marked and masked in the audit log (`[SensitiveData]`, `[AuditLengthOnly]`); field-level access with sensitive fields hidden by default; personal answers kept apart; masking before AI | `docs/architecture/identity-access.md`, `docs/architecture/forms.md`, `docs/architecture/ai-gateway.md` |
| T1 Asset management | Retention and disposal | Zelvo | Retention periods per record type and for unused accounts, anonymised by the privacy jobs; form uploads, AI logs, delivery logs and exports cleared on schedule | `docs/architecture/privacy.md` |
| T2 Physical and environmental security | Data centre | Customer | Self-hosted on the customer's servers or UAE cloud account (GOV-4) | — |
| T3 Operations management | Change and capacity management, malware protection | Shared | Signed update packages (F4); ClamAV scanning of every uploaded file before download; container images scanned (Grype), SBOM, Dependabot | `docs/architecture/tickets.md`, CI workflows |
| T3 Operations management | Logging and monitoring | Zelvo | Append-only, hash-chained audit log written in the same transaction (database triggers); OpenTelemetry traces, metrics and logs without personal data; rate-limit and failure alerts | `docs/architecture/data-tenancy-audit.md`, `docs/architecture/observability.md` |
| T3 Operations management | Backup | Shared | PostgreSQL holds data and attachments, so one backup covers both; backup and restore with F6 (RPO 15 min, RTO 4 h) | `docs/phase1-build-plan.md` (F6) |
| T4 Communications | Network security, encryption in transit | Shared | TLS 1.3 to RabbitMQ and OpenSearch; Caddy with ACME or the customer's certificate; outgoing requests limited by policy (no internal or metadata addresses unless allowed) | `docs/architecture/security-baseline.md`, `docs/architecture/branding.md`, `docs/architecture/integrations.md` |
| T4 Communications | Information exchange | Zelvo | Signed webhooks (Standard Webhooks), integration log with masked bodies; email with SPF, DKIM and DMARC | `docs/architecture/integrations.md`, `docs/architecture/branding.md` |
| T5 Access control | User access management, privileges | Zelvo | Role- and field-level permissions as data, least privilege by default; lock-out guards; integrations never get user, role, identity, integration, privacy or other administrative permissions | `docs/architecture/identity-access.md`, `docs/architecture/integrations.md` |
| T5 Access control | Authentication | Zelvo | Keycloak (one realm per tenant), mandatory second factor for staff (`acr=mfa`), brute-force protection, UAE PASS with account levels | `docs/architecture/identity-access.md`, `docs/architecture/uae-pass.md` |
| T5 Access control | Session management | Zelvo | Server-side sessions in Valkey, http-only cookies, CSRF protection; the browser never holds tokens | `docs/architecture/identity-access.md` |
| T5 Access control | Segregation (multi-tenancy) | Zelvo | PostgreSQL row-level security on every tenant table (guard test), application role without BYPASSRLS, tenant filter in search | `docs/architecture/data-tenancy-audit.md`, `docs/architecture/search.md` |
| T6 Third-party security | Suppliers, cloud and AI providers | Shared | Customer chooses its AI provider and key; personal data masked before AI; customers' MCP servers only through the outgoing-request policy with approval for changes | `docs/architecture/ai-gateway.md`, `docs/architecture/mcp.md` |
| T7 Acquisition, development and maintenance | Secure development | Zelvo | OWASP ASVS L2; CodeQL, Gitleaks, dependency audit and image scanning in CI; parameterised queries only; tests against real PostgreSQL | `docs/security/asvs-l2-checklist.md`, `CLAUDE.md` |
| T7 Acquisition, development and maintenance | Cryptography and key management | Zelvo | Stored secrets encrypted with per-tenant AES-256-GCM keys wrapped by a master key file or Vault/OpenBao Transit; key rotation (`rewrap-keys`) | `docs/architecture/secrets-encryption.md` |
| T8 Incident management | Security incident handling | Shared | Audit log, telemetry and alerts give the evidence; the customer runs its incident process (and can run it in Zelvo as incidents) | `docs/architecture/observability.md` |
| T9 Continuity management | Availability, recovery | Shared | Stateless services scaled horizontally; Helm for medium and large installations; backup and restore with F6 | `docs/phase1-build-plan.md` (F6) |

## Open points

- Penetration test (release blocker, once after all four phases, decided 2026-11-26) and the ASVS L2 review with task F7 add their reports as evidence.
- Customers that must follow the Abu Dhabi or Dubai information security standards can reuse this mapping; their control numbers are not listed here.
