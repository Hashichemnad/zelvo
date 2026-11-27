# UAE PDPL mapping (GOV-5)

How Zelvo supports the obligations of **Federal Decree-Law No. 45 of 2021 on the Protection of Personal Data** (PDPL) for the organisations that use it as controller. Article references follow the published law; the Executive Regulations may add detail (for example deadlines), so the configurable values below should be checked against them. Government entities are largely outside the federal PDPL and follow their emirate's data law; the same features serve those rules. Last reviewed with task F5 (2026-11-25).

| PDPL topic | Article (indicative) | Zelvo support | Configured by | Evidence |
| --- | --- | --- | --- | --- |
| Lawful processing and consent | Art. 4–6 | Versioned bilingual privacy notice accepted at first sign-in and on each new version, and by visitors before chat or anonymous messages; optional purposes granted and withdrawn with an unchangeable history | Settings → Privacy | `docs/architecture/privacy.md` |
| Controller's obligations, data minimisation | Art. 5, 7 | Personal data kept apart and masked (audit log, AI, integration log, telemetry); fields hidden unless a role is given access; anonymous complaints without personal details | Roles, field rules | `docs/architecture/identity-access.md`, `docs/architecture/customer-voice.md` |
| Storage limitation | Art. 5 | Retention periods per record type and for unused accounts; records anonymised when they end; logs and temporary files cleared on schedule | Settings → Privacy | `docs/architecture/privacy.md` |
| Security of processing | Art. 20 | Row-level tenant isolation, encryption of stored secrets, TLS, mandatory second factor for staff, tamper-evident audit log | Operator, administrators | `docs/security/uae-ia-mapping.md` |
| Data breach notification | Art. 9 | Evidence from the audit log and telemetry; the notification to the UAE Data Office and to people is the organisation's process | Customer | — |
| Data protection officer | Art. 10–12 | The `privacy.requests` permission gives a DPO or privacy officer the requests queue | Roles | `docs/architecture/privacy.md` |
| Right to information and access | Art. 13 | Data subject requests of type Access; the person downloads a copy themselves or the officer sends it | Account → Privacy, privacy requests | `docs/architecture/privacy.md` |
| Right to data portability | Art. 14 | The copy is machine-readable JSON (`zelvo-personal-data/1`) with the person's files | — | `docs/architecture/privacy.md` |
| Right to correction and erasure | Art. 15 | Correction requests handled by the officer in the user and ticket screens; erasure requests carried out by anonymising tickets and emptying the account; the audit log kept as the legal record (decided 2026-11-25) | Privacy requests | `docs/architecture/privacy.md` |
| Right to restrict and to stop processing; automated processing | Art. 16–18 | Restriction and Objection request types, handled by the officer with a recorded resolution; AI suggestions are never applied without a person except when an administrator turns automatic triage on | Privacy requests, Settings → AI | `docs/architecture/ai-features.md` |
| Cross-border transfer | Art. 22–23 | Self-hosted in the UAE (GOV-4); AI calls go only to the provider the customer chooses, with personal data masked | Operator, Settings → AI | `docs/architecture/ai-gateway.md` |
| Records of processing | Art. 7 | Audit log of every create, change, admin action and view of personal data | — | `docs/architecture/data-tenancy-audit.md` |
| Employee monitoring and workload data (K4) | Art. 4–7, 13–15 | Agent wellbeing off by default; turned on only after the administrator confirms staff were told, every agent notified; daily figures from existing Zelvo activity only, kept 90 days; individuals only for their team leads, totals for executives; every view audited; in the person's copy and erased with them; not for ranking | Settings → Wellbeing | `docs/architecture/wellbeing.md`, `docs/security/wellbeing-dpia.md` |

Deadline for requests: 30 days by default (Settings → Privacy, 1–90 days).
