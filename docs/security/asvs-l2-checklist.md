# OWASP ASVS 5.0 Level 2 checklist

Zelvo targets OWASP ASVS **Level 2** for every tenant (non-functional requirement "Security standard"). This checklist was started in task A5 and is completed in task F7 (ASVS L2 review before the external penetration test). Every task that touches a listed area updates its rows.

- Standard: [OWASP ASVS 5.0.0](https://github.com/OWASP/ASVS/tree/v5.0.0/5.0) (May 2025), levels 1 and 2: 253 requirements. Requirement texts are in the standard (CC BY-SA 4.0) and are not copied here; look them up by ID.
- Status: **Met** (with evidence), **Partly met**, **Open** (with the task expected to close it), **Not applicable**, **Not assessed** (not reviewed yet).
- Evidence points to code, tests, architecture notes and build plan tasks (A1-F7 in `docs/phase1-build-plan.md`).

**Summary (2026-11-25):** Met: 101 · Partly met: 21 · Open: 3 · Not applicable: 18 · Not assessed: 110.

Main gaps found so far: web app without Content-Security-Policy and HSTS (V3.4); TLS still to add for PostgreSQL, Valkey and internal HTTP (V12.3, F6); service passwords as environment variables rather than a secrets manager (V13.3.1, F6); password policy, breached-password checks and session management settings in Keycloak not yet reviewed (V6.2, V7). File handling (V5) was added with attachments in B1.

## V1 Encoding and Sanitization

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V1.1.1 | 2 | V1.1 Encoding and Sanitization Architecture | Not assessed |  |
| V1.1.2 | 2 | V1.1 Encoding and Sanitization Architecture | Not assessed |  |
| V1.2.1 | 1 | V1.2 Injection Prevention | Partly met | B5: notification emails are built by `NotificationRenderer`, which HTML-encodes every template text and placeholder value for the HTML body (values also isolated in `<bdi>`), writes subjects and in-app texts as single lines without control characters, and uses only absolute `http(s)` links built from the operator's `Notifications:WebUrl` and paths checked by a database constraint; previews and the delivery log show the HTML in a sandboxed `iframe` (no scripts, no access to the page). The web app renders through React, which encodes text. Tests: `NotificationRendererTests.Values_are_encoded_isolated_and_laid_out_right_to_left_with_the_letterhead_and_the_Zelvo_footer`, `NotificationApiTests.Administrators_switch_events_off_and_word_them_in_both_languages_with_only_their_placeholders`. Other output contexts are reviewed in F7. E2: incoming emails are read as text only: HTML bodies are turned into text (scripts, styles and markup dropped, `EmailParsing.HtmlToText`) and conversation messages and chats are shown as text by React; replies to email requesters use the B5 renderer. |
| V1.2.2 | 1 | V1.2 Injection Prevention | Not assessed |  |
| V1.2.3 | 1 | V1.2 Injection Prevention | Not assessed |  |
| V1.2.4 | 1 | V1.2 Injection Prevention | Met | EF Core and parameterised SQL only (`ExecuteSqlInterpolated` binds parameters); no SQL built from input. The one statement with a variable identifier, "Reset demo"'s `DELETE` (`DemoReset.DeleteStatement`), takes table names from the EF model only, checks them against `^[a-z_][a-z0-9_]*$` and binds the tenant id. A1, D. |
| V1.2.5 | 1 | V1.2 Injection Prevention | Not applicable | No operating system commands are run with user input. |
| V1.2.6 | 2 | V1.2 Injection Prevention | Open | LDAP: none yet; AD/LDAP connector in E7. |
| V1.2.7 | 2 | V1.2 Injection Prevention | Not applicable | No XPath or LaTeX processing. |
| V1.2.8 | 2 | V1.2 Injection Prevention | Not applicable | No XPath or LaTeX processing. |
| V1.2.9 | 2 | V1.2 Injection Prevention | Not assessed |  |
| V1.3.1 | 1 | V1.3 Sanitization | Met | D1: knowledge articles are Markdown; raw HTML in them is never rendered (react-markdown with `skipHtml`), link targets keep only safe protocols and images only the article's own scanned uploads (`MarkdownView`; test `Knowledge.test.tsx` "renders Markdown but never raw HTML…"). No other screen accepts rich text yet. C1: rich text answers of forms are Markdown shown with the same `MarkdownView`, without images. C2: service descriptions are Markdown shown with the same `MarkdownView`; FAQ answers, steps and other card texts are plain text. |
| V1.3.2 | 1 | V1.3 Sanitization | Met | No `eval` or dynamic code execution in the API or web app. |
| V1.3.3 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.4 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.5 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.6 | 2 | V1.3 Sanitization | Met | E1: outgoing webhooks only to `https://` receivers on allowed addresses (`OutboundAddressPolicy`): loopback, link-local and cloud metadata, unspecified, multicast and reserved ranges always refused; private networks only when the operator lists them; checked on the address actually connected to after DNS; no redirects followed; no system proxy. Tests in `IntegrationBuildingBlockTests`, `WebhookApiTests`. Connectors (E7) will use the same policy. E4: AI provider endpoints use the same policy with their own operator settings (`Ai:AllowedPrivateNetworks`, `Ai:AllowInsecureHttp`, both off by default), checked when saved and on the address connected to after DNS; redirects are not followed; a new endpoint needs the API key again, so a stored key is never sent to an address it was not given for. Tests: `AiApiTests.Keys_are_stored_encrypted_never_returned_and_needed_again_for_a_new_endpoint`, `AiBuildingBlockTests.Internal_addresses_are_refused_after_dns`. E6: customers' MCP servers use the same policy and AI operator settings, checked when saved and after DNS (`McpServerConnector`); a new address needs the credential again. Test: `McpClientTests.Servers_are_added_with_an_encrypted_credential_and_their_tools_start_off`. E2: mail connections (IMAP and SMTP through MailKit, Microsoft Graph, the Gmail API and the Microsoft and Google token endpoints) use the same policy with their own operator setting (`Channels:Network:AllowedPrivateNetworks`), checked when a mailbox is saved (host names; literal addresses and `localhost` refused) and on the address actually connected to after DNS (`ChannelHttp`, `MailboxClients.ConnectAsync`); redirects are not followed; a Google service-account key never chooses the token address (the configured one is the assertion's audience); a new server, provider, sign-in method, app or address needs the credential again. Tests: `EmailChannelTests.Mailbox_settings_keep_credentials_safe_and_are_for_administrators_only`, `MailProviderTests.Gmail_signs_in_as_the_mailbox_with_the_service_account_and_never_uses_the_key_files_token_address`. E8: payment gateways only on `https://` addresses the installation allows (`Payments:AllowedPrivateNetworks`), checked after DNS, no redirects followed. |
| V1.3.7 | 2 | V1.3 Sanitization | Met | B5: notification templates are the only templates administrators edit. They have no template language: `{{placeholder}}` names are replaced with values, nothing is evaluated, and only the event's own placeholders are accepted when saved (`TemplateSyntax.Problems`; unknown names and stray braces refused with 400). Values come from the change that caused the notification, never from the template. Tests: `NotificationRendererTests.Templates_may_use_only_the_events_placeholders`, `NotificationApiTests.Administrators_switch_events_off_and_word_them_in_both_languages_with_only_their_placeholders`. B4: canned responses use the same placeholder syntax and checks (`TemplateSyntax.Problems`) with a fixed list of five placeholders; they are filled in on the server for the incident as plain text (`CannedResponseEndpoints.RenderAsync`; test `WorkspaceApiTests.Canned_responses_are_personal_for_the_group_or_for_everyone_and_are_filled_in_per_ticket`). |
| V1.3.8 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.9 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.10 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.11 | 2 | V1.3 Sanitization | Met | B5: email is built with MimeKit (`SmtpEmailSender.Build`): addresses are parsed as mailboxes (an address that does not parse fails for good), the subject and sender name are encoded header values with control characters and line breaks removed by the renderer first, and recipients come only from user accounts, never from input. No other part of Zelvo talks SMTP or IMAP yet (email-to-ticket in E2). |
| V1.4.1 | 2 | V1.4 Memory, String, and Unmanaged Code | Met | Managed languages (C#, TypeScript, Java). |
| V1.4.2 | 2 | V1.4 Memory, String, and Unmanaged Code | Not assessed |  |
| V1.4.3 | 2 | V1.4 Memory, String, and Unmanaged Code | Met | Managed languages (C#, TypeScript, Java). |
| V1.5.1 | 1 | V1.5 Safe Deserialization | Not applicable | No XML parsing yet (SAML is handled by Keycloak). |
| V1.5.2 | 2 | V1.5 Safe Deserialization | Met | System.Text.Json with typed records; no polymorphic or type-name deserialisation. |

## V2 Validation and Business Logic

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V2.1.1 | 1 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.1.2 | 2 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.1.3 | 2 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.2.1 | 1 | V2.2 Input Validation | Met | C1: form answers are checked on the server against the published form: only known fields, the expected kind per field type, options from the list, lengths, number and date ranges, formats (Emirates ID with its check digit, phone, email, IBAN mod 97) and admin patterns (run with a 100 ms time limit); hidden fields' answers are dropped and read-only fields keep their default (`FormAnswers`, `FormRules`; tests `FormEngineTests`, `FormApiTests`). C2: service cards are checked on the server before they are saved and again before review and publishing: lengths, codes, keys, HTTPS-only external links, fees in AED with two decimals, file types within the tenant's allowlist, and every referenced catalog, category, department, user, group, role, location, form, file and service (`ServiceCards.CheckShape`, `ServiceCardCheck`); bulk imports run the same checks per row (`ServicesBulkType`; tests `CatalogApiTests`). C3: workflow rules and approval levels are checked on the server: known fields with fitting operators and values, ids that exist, placeholders the wording may use, approvals only on service requests at submission (`RuleDocuments.CheckConditions`, `CheckLevels`, `CheckNotify`, `WorkflowReferences`; `WorkflowApiTests.Rules_assign_notify_and_ask_for_approval_by_amount_and_answer`). C4: the card's fulfilment (groups, overrides, SLA targets, escalations, task keys, steps naming tasks) is checked on the server before saving and publishing (`ServiceCards.CheckShape`, `ServiceCardCheck`; `FulfilmentApiTests`). C5: bundle items (single internal services that exist, no duplicates), proactive triggers (keys, offsets, schedules, people that exist, a date field of the source service's published form) and proactive events (service, trigger, person, reference length) are checked on the server; a bundle's combined answers are refused when no form of the bundle has the field (`ServiceCards.CheckShape`, `ServiceCardCheck`, `ProactiveEndpoints`; `BundleProactiveApiTests`). E8: gateway connectors (methods, HTTPS addresses, placeholders, credentials, answer fields, signatures), fees and fee rules, refunds (at most what is left, two decimals) and settlement files (CSV or Excel, size, columns) are checked on the server (`GatewayConnector.Check`, `PaymentEndpoints`; `PaymentApiTests`). |
| V2.2.2 | 1 | V2.2 Input Validation | Met | All validation in the API (`AddValidation`, endpoint checks); the web app only adds usability checks. C1: the web app checks form answers with the same rules for usability only (shared cases in `tests/shared/form-cases.json`); the server checks everything again. |
| V2.2.3 | 2 | V2.2 Input Validation | Not assessed |  |
| V2.3.1 | 1 | V2.3 Business Logic Security | Partly met | C3: approval levels run in order on the workflow engine; a decision is accepted only from the person asked, while their level is current and the process waits, and a request waiting for approval cannot be assigned or moved by staff (409 `awaiting-approval`) (`ApprovalService.DecideAsync`, `WorkflowApiTests`). Other flows are reviewed in F7. |
| V2.3.2 | 2 | V2.3 Business Logic Security | Partly met | C3: concurrent decisions on one approval process are taken one after the other (the process row is locked `FOR UPDATE` while the engine moves on), so a level is decided once (`ApprovalService`). Other limits are reviewed in F7. C4: completing a fulfilment task locks the request's row, so parallel tasks finishing together advance the stages once (`RequestTaskEndpoints`). E8: a payment is confirmed with the payment row locked `FOR UPDATE`, so returns, notifications and the job counting the same payment at the same time apply it once. |
| V2.3.3 | 2 | V2.3 Business Logic Security | Met | Changes and their audit entries and search outbox entries are saved in one transaction. A1, A4. |
| V2.3.4 | 2 | V2.3 Business Logic Security | Partly met | E8: a paid request reaches the service desk only after the gateway's status API confirms the payment, with the amount and reference checked; the browser's return and the gateway's notification only trigger that check (`PaymentApiTests`). |
| V2.4.1 | 2 | V2.4 Anti-automation | Met | A5: API rate limits per signed-in user and per client address (Valkey, shared by instances); Keycloak brute-force protection for sign-in. E1: a limit per API key and OAuth client, set by the administrator (`ApiClientApiTests`). E2: live chat for visitors has its own limits per client address (5 new chats per 10 minutes, 5 left messages per hour) and per chat (20 messages a minute), shared through Valkey (`ChatEndpoints.LimitedAsync`); no captcha, so installations without internet keep working (product owner decision 2026-11-24). C6: anonymous complaints, inquiries and suggestions have their own limits on top of the API's (5 submissions per client address and hour; 30 tracking calls in ten minutes per address and per record number, test `Anonymous_submission_and_tracking_have_their_own_rate_limits`); tracking codes have about 59 random bits, so guessing one within the limits is not practical. |

## V3 Web Frontend Security

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V3.2.1 | 1 | V3.2 Unintended Content Interpretation | Not assessed |  |
| V3.2.2 | 1 | V3.2 Unintended Content Interpretation | Not assessed |  |
| V3.3.1 | 1 | V3.3 Cookie Setup | Met | `__Host-` session and sign-in cookies, Secure outside local development (`src/web/src/lib/auth/session.ts`). A2. |
| V3.3.2 | 2 | V3.3 Cookie Setup | Met | Session cookies `SameSite=Lax` (needed for the sign-in redirect); changes also need the CSRF token. A2. |
| V3.3.3 | 2 | V3.3 Cookie Setup | Met | `__Host-` prefix on the session and sign-in cookies. A2. |
| V3.3.4 | 2 | V3.3 Cookie Setup | Met | Session cookie is HttpOnly; tokens stay on the server (backend for frontend). The demo organisation cookie (`zelvo-tenant`) is HttpOnly too and only selects tenants listed in `ZELVO_DEMO_TENANTS`. A2, D. |
| V3.4.1 | 1 | V3.4 Browser Security Mechanism Headers | Open | No HSTS yet: TLS is terminated by the proxy of the hardened install (F6), which adds it; the web app should also send it. |
| V3.4.2 | 1 | V3.4 Browser Security Mechanism Headers | Not assessed |  |
| V3.4.3 | 2 | V3.4 Browser Security Mechanism Headers | Partly met | The API sends a restrictive CSP (`SecurityHeadersMiddleware`); the web app has none yet (follow-up with F1 branding, which sets allowed sources). |
| V3.4.4 | 2 | V3.4 Browser Security Mechanism Headers | Met | `X-Content-Type-Options: nosniff` on API and web responses. |
| V3.4.5 | 2 | V3.4 Browser Security Mechanism Headers | Met | `Referrer-Policy: no-referrer` (API) and `strict-origin-when-cross-origin` (web). |
| V3.4.6 | 2 | V3.4 Browser Security Mechanism Headers | Partly met | API: `frame-ancestors 'none'`; web: `X-Frame-Options: DENY` but no CSP yet (see V3.4.3). E2: the chat widget page is the only page that may be framed, and only by the websites listed for the widget (`frame-ancestors 'self' <origins>` set by the web proxy; every other page keeps `X-Frame-Options: DENY`). The embedding site never sees the chat: it runs in the frame from Zelvo's origin, and the frame talks to the page only to ask it to close (`postMessage` to the listed origin). |
| V3.5.1 | 1 | V3.5 Browser Origin Separation | Met | Changes through the web proxy need same origin and the session's CSRF token (e2e test). The demo switch (`/auth/demo`, demo installations only) needs same origin and, when signed in, the CSRF token. A2, D. |
| V3.5.2 | 1 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.5.3 | 1 | V3.5 Browser Origin Separation | Met | Changes use PUT/POST/DELETE; GET has no side effects. |
| V3.5.4 | 2 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.5.5 | 2 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.7.1 | 2 | V3.7 Other Browser Security Considerations | Not assessed |  |
| V3.7.2 | 2 | V3.7 Other Browser Security Considerations | Not assessed |  |

## V4 API and Web Service

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V4.1.1 | 1 | V4.1 Generic Web Service Security | Met | JSON and problem details with explicit content types. |
| V4.1.2 | 2 | V4.1 Generic Web Service Security | Not assessed |  |
| V4.1.3 | 2 | V4.1 Generic Web Service Security | Met | A5: `X-Forwarded-For`/`-Proto` used only from `Network:TrustedProxies`; the web app sends the browser's address it determined itself (`TRUSTED_PROXY_HOPS`). Tests: `RateLimitingTests`. |
| V4.2.1 | 2 | V4.2 HTTP Message Structure Validation | Not assessed |  |
| V4.3.1 | 2 | V4.3 GraphQL | Not applicable | No GraphQL. |
| V4.3.2 | 2 | V4.3 GraphQL | Not applicable | No GraphQL. |
| V4.4.1 | 1 | V4.4 WebSocket | Not applicable | B4: Zelvo offers no WebSockets. Live updates (SignalR hub `/api/v1/live`) allow only Server-Sent Events and long polling (`LiveUpdatesSetup`; test `WorkspaceApiTests.The_live_hub_accepts_signed_in_people_over_server_sent_events_and_long_polling_only`), through the web app's proxy with the server-side session's token and same-origin plus CSRF checks on its POST requests, like every other API call. Review again if WebSockets are ever enabled. |
| V4.4.2 | 2 | V4.4 WebSocket | Not applicable | B4: Zelvo offers no WebSockets. Live updates (SignalR hub `/api/v1/live`) allow only Server-Sent Events and long polling (`LiveUpdatesSetup`; test `WorkspaceApiTests.The_live_hub_accepts_signed_in_people_over_server_sent_events_and_long_polling_only`), through the web app's proxy with the server-side session's token and same-origin plus CSRF checks on its POST requests, like every other API call. Review again if WebSockets are ever enabled. |
| V4.4.3 | 2 | V4.4 WebSocket | Not applicable | B4: Zelvo offers no WebSockets. Live updates (SignalR hub `/api/v1/live`) allow only Server-Sent Events and long polling (`LiveUpdatesSetup`; test `WorkspaceApiTests.The_live_hub_accepts_signed_in_people_over_server_sent_events_and_long_polling_only`), through the web app's proxy with the server-side session's token and same-origin plus CSRF checks on its POST requests, like every other API call. Review again if WebSockets are ever enabled. |
| V4.4.4 | 2 | V4.4 WebSocket | Not applicable | B4: Zelvo offers no WebSockets. Live updates (SignalR hub `/api/v1/live`) allow only Server-Sent Events and long polling (`LiveUpdatesSetup`; test `WorkspaceApiTests.The_live_hub_accepts_signed_in_people_over_server_sent_events_and_long_polling_only`), through the web app's proxy with the server-side session's token and same-origin plus CSRF checks on its POST requests, like every other API call. Review again if WebSockets are ever enabled. |

## V5 File Handling

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V5.1.1 | 2 | V5.1 File Handling Documentation | Met | Permitted file types (an allowlist of types Zelvo can check, chosen per tenant), maximum size (tenant setting, 25 MB default, 100 MB installation maximum) and the handling of malicious files (scanned by ClamAV before download; infected files rejected, content deleted, audited) are documented in `docs/architecture/tickets.md` (B1). Zelvo never unpacks archives; ClamAV scans inside them within `MaxScanSize`. |
| V5.2.1 | 1 | V5.2 File Upload and Content | Met | Tenant size limit checked per file and an endpoint request size limit of 101 MB (`IncidentAttachmentEndpoints`); clamd `StreamMaxLength` matches. Tests: `IncidentAttachmentTests.The_tenant_sets_the_largest_file_and_the_accepted_types`. E1: bulk imports accept at most 5 MB and 5,000 rows (request size limit on the endpoint). C1: form uploads (file and signature fields) follow the tenant's size limit and each field's own smaller limit (`FormUploadEndpoints`, `FormSubmission`). C2: service files (image, document templates) and required documents of requests follow the tenant's size limit and each document's own smaller limit (`ServiceEndpoints.UploadFileAsync`, `ServiceRequestEndpoints.CheckDocumentsAsync`). F1: branding images up to 2 MB (favicon 256 KB) (`BrandingEndpoints.UploadImageAsync`). |
| V5.2.2 | 1 | V5.2 File Upload and Content | Met | Extension must be on the tenant's allowlist and the content must match the type (magic bytes; text must be UTF-8 without NUL) (`FileTypes`); the stored media type comes from the type, never the uploader. Tests: `Files_of_other_types_or_with_content_that_does_not_match_are_refused`, `TicketWorkflowTests.Content_must_match_the_file_type`. E1: bulk imports accept `.csv` and `.xlsx` only, and the content must parse as that format (`TabularFile`), otherwise 400. D1: knowledge article images accept PNG, JPEG, GIF and WebP only (no SVG), with the content checked against the type (`KnowledgePortalEndpoints.UploadImageAsync`; test `KnowledgeApiTests.Images_are_checked_and_shown_to_whoever_may_read_the_article`). C1: form uploads use the same allowlist and content checks; a field can narrow the types; drawn signatures must be PNG (`FormApiTests.Files_and_signatures_are_uploaded_first_and_belong_to_the_ticket_once_sent`). C2: service files use the same allowlist and content checks (images: PNG, JPEG, GIF, WebP); a required document can narrow the types (`CatalogApiTests.A_request_carries_the_form_answers_and_required_documents_and_others_request_on_behalf_only_with_an_allowed_role`). F1: branding images only PNG, JPEG, WebP (favicon also ICO), content checked, SVG refused, scanned by ClamAV before publishing, served with `nosniff` and `default-src 'none'` (`BrandingApiTests.Images_are_checked_scanned_and_served_only_once_published`). |
| V5.2.3 | 2 | V5.2 File Upload and Content | Partly met | Attachments: Zelvo never decompresses uploaded archives; ClamAV scans inside them with its own limits (`MaxScanSize` 400 MB, default file and recursion limits). E1: bulk import does open `.xlsx` (zip) files, read with a limit on the characters per part (`MaxCharactersInPart`) and from a 5 MB upload; a limit on the number of files in the archive is still to add. |
| V5.3.1 | 1 | V5.3 File Storage | Met | Attachments are stored in PostgreSQL (`ticket_attachment_contents`, row-level security), never in a folder the web server serves or executes (B1, decision 2026-11-22). D1: article images use the same store (`ticket_attachments` with `article_id`). C1: form uploads use the same store; unsent uploads belong only to their uploader and are deleted by the worker after a day (`FormUploadCleanupJob`). C2: service files use the same store (`ticket_attachments` with `service_id`); the form-upload clean-up skips them. |
| V5.3.2 | 1 | V5.3 File Storage | Met | No file system paths are built from user input; file names are cleaned of folders, control and bidirectional-override characters (`FileTypes.CleanFileName`, tested). |
| V5.4.1 | 2 | V5.4 File Download | Met | Downloads use the stored, cleaned file name in `Content-Disposition: attachment` with RFC 6266 `filename*` encoding (ASP.NET Core `TypedResults.File`), plus `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`. |
| V5.4.2 | 2 | V5.4 File Download | Met | As V5.4.1: names are cleaned on upload and encoded per RFC 6266 on download. |
| V5.4.3 | 2 | V5.4 File Download | Met | Every upload is scanned by ClamAV (on by default) before it can be downloaded; while the scanner is unavailable files stay unavailable (`AttachmentScanJob`). Tests: `IncidentAttachmentTests`, `RealClamAvTests` (against a real ClamAV), e2e `incidents.spec.ts` (EICAR rejected). D1: article images too, before anyone can see them. C1: form uploads are scanned like attachments; infected ones are refused when the form is sent. C2: service files and required documents are scanned like attachments; infected service files are audited (`catalog.file.infected`) and infected documents are refused when the request is sent. |

## V6 Authentication

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V6.1.1 | 1 | V6.1 Authentication Documentation | Not assessed |  |
| V6.1.2 | 2 | V6.1 Authentication Documentation | Not assessed |  |
| V6.1.3 | 2 | V6.1 Authentication Documentation | Met | Sign-in pathways (password, email code, UAE PASS; second factor) documented in `docs/architecture/identity-access.md` and `uae-pass.md`; the demo-only one-click sign-in in `demo-mode.md`. A2, D3, D. |
| V6.2.1 | 1 | V6.2 Password Security | Met | Keycloak password policy on every tenant realm: at least 12 characters, not the username or email, not the last 3. A2. |
| V6.2.2 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.3 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.4 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.5 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.6 | 1 | V6.2 Password Security | Met | Keycloak sign-in page (zelvo theme): password field masked, paste and password managers allowed. |
| V6.2.7 | 1 | V6.2 Password Security | Met | Keycloak sign-in page (zelvo theme): password field masked, paste and password managers allowed. |
| V6.2.8 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.9 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.10 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.11 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.12 | 2 | V6.2 Password Security | Not assessed |  |
| V6.3.1 | 1 | V6.3 General Authentication Security | Met | Keycloak brute-force protection on every tenant realm; A5 API rate limits. A2. |
| V6.3.2 | 1 | V6.3 General Authentication Security | Partly met | No default accounts in Zelvo or tenant realms; Keycloak's bootstrap administrator in the master realm is temporary and must be replaced at install (F6). |
| V6.3.3 | 2 | V6.3 General Authentication Security | Met | Second factor (authenticator app, or UAE PASS SOP2+ when the tenant allows) mandatory for staff roles, checked by the API from `acr`. Exception by design: the demo sign-in step (`zelvo-demo-sign-in`) signs listed demo users in at `acr=mfa` without credentials; `migrate` adds it only to the realms of a demo installation (demo license, own database, sample organisations only), never to other installations. A2, D3, D. |
| V6.3.4 | 2 | V6.3 General Authentication Security | Met | Sign-in pathways (password, email code, UAE PASS; second factor) documented in `docs/architecture/identity-access.md` and `uae-pass.md`. A2, D3. |
| V6.4.1 | 1 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.4.2 | 1 | V6.4 Authentication Factor Lifecycle and Recovery | Met | No password hints or secret questions. |
| V6.4.3 | 2 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.4.4 | 2 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.5.1 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.2 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.3 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.4 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.5 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.6.1 | 2 | V6.6 Out-of-Band authentication mechanisms | Not applicable | No SMS or phone one-time passwords for sign-in. |
| V6.6.2 | 2 | V6.6 Out-of-Band authentication mechanisms | Not assessed |  |
| V6.6.3 | 2 | V6.6 Out-of-Band authentication mechanisms | Met | Email codes: limited attempts and lifetime in the Zelvo Keycloak extension, plus brute-force protection. A2. |
| V6.8.1 | 2 | V6.8 Authentication with an Identity Provider | Met | UAE PASS accounts linked by UAE PASS uuid; linking by email needs proof; Emirates ID match only for SOP2+. D3. |
| V6.8.2 | 2 | V6.8 Authentication with an Identity Provider | Met | Tokens verified with the tenant realm's signing keys; required strength from `acr` (RFC 9470 step-up). A2. |
| V6.8.3 | 2 | V6.8 Authentication with an Identity Provider | Not assessed |  |
| V6.8.4 | 2 | V6.8 Authentication with an Identity Provider | Met | Tokens verified with the tenant realm's signing keys; required strength from `acr` (RFC 9470 step-up). A2. |

## V7 Session Management

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V7.1.1 | 2 | V7.1 Session Management Documentation | Met | Idle 30 min and absolute 12 h time-outs, documented in `identity-access.md`. A2. |
| V7.1.2 | 2 | V7.1 Session Management Documentation | Not assessed |  |
| V7.1.3 | 2 | V7.1 Session Management Documentation | Not assessed |  |
| V7.2.1 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.2 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.3 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.4 | 1 | V7.2 Fundamental Session Management Security | Met | A new session id at every sign-in; sign-out deletes the session. A2. |
| V7.3.1 | 2 | V7.3 Session Timeout | Met | Idle and absolute time-outs enforced by the web app's session store. A2. |
| V7.3.2 | 2 | V7.3 Session Timeout | Met | Idle and absolute time-outs enforced by the web app's session store. A2. |
| V7.4.1 | 1 | V7.4 Session Termination | Met | Sign-out deletes the server session and ends the Keycloak (and UAE PASS) session. A2, D3. |
| V7.4.2 | 1 | V7.4 Session Termination | Not assessed |  |
| V7.4.3 | 2 | V7.4 Session Termination | Not assessed |  |
| V7.4.4 | 2 | V7.4 Session Termination | Met | Sign-out in the header of every signed-in page. |
| V7.4.5 | 2 | V7.4 Session Termination | Not assessed |  |
| V7.5.1 | 2 | V7.5 Defenses Against Session Abuse | Not assessed |  |
| V7.5.2 | 2 | V7.5 Defenses Against Session Abuse | Not assessed |  |
| V7.6.1 | 2 | V7.6 Federated Re-authentication | Not assessed |  |
| V7.6.2 | 2 | V7.6 Federated Re-authentication | Not assessed |  |

## V8 Authorization

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V8.1.1 | 1 | V8.1 Authorization Documentation | Met | Permissions and field rules per role are data, documented in `identity-access.md`. A2. B2: directory permissions and group-lead rights in `directory-assignment.md`; custom roles start with no permissions. B3: `sla.manage` and SLA read access in `sla.md`. |
| V8.1.2 | 2 | V8.1 Authorization Documentation | Met | Permissions and field rules per role are data, documented in `identity-access.md`. A2. |
| V8.2.1 | 1 | V8.2 General Authorization Design | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. E1: integrations act with one role narrowed by their scopes and never hold `users.manage`, `roles.manage`, `identity.manage` or `integrations.manage` (`CurrentUser.SetClient`, `ApiClientApiTests`). E4: Settings → AI needs `ai.manage`, which integrations can never hold (`AiApiTests.Only_ai_managers_reach_settings_and_integrations_can_never_be_given_ai_manage`). D1: knowledge permissions `knowledge.read`, `knowledge.write`, `knowledge.publish`; internal articles only for staff; four-eyes rule for publishing (`KnowledgeAccess.MayReview`; tests in `KnowledgeApiTests`). E6: AI agents of the MCP server are integrations set up by an `ai.manage` holder; they get only tools turned on for them whose permission their role and scopes hold, and changes need the approval of a person who holds the change's permission (`McpRunner`, `McpServerTests`). E5: AI features use the caller's permissions (summary `incidents.read`; replies, triage decisions and duplicates `incidents.edit`, a group change also `incidents.assign`; field rules apply to AI values); requesters translate only their own incident's public messages; the virtual agent is grounded only in articles the person may read (external ones for visitors) and never runs a customer's tool that changes data (`AiFeatureTests`). C1: forms, field sets and form bindings need `forms.manage` (tenant administrators); filling a form needs only the right to report the ticket. C2: the catalog needs `catalog.manage` (tenant administrators) or being an editor named on a catalog (only that catalog's services); publishing needs `catalog.publish` and the four-eyes rule (`CatalogAccess.MayReview`); service requests need `requests.read`, `requests.edit`, `requests.assign` for staff; requesting on behalf only with a role the service allows (`CatalogApiTests`). C3: workflow rules need `workflows.manage` (tenant administrators; never for integrations); deciding an approval needs being its approver (no permission), `approvals.read` shows all approvals (`WorkflowApiTests`). C4: fulfilment tasks are worked on by their assignee and their group's members only, and given to people by holders of `requests.assign` or the group's team leads; the MCP catalog tools need `requests.read` or `requests.edit` in the agent's role and scopes (`FulfilmentApiTests`). C5: proactive events need `proactive.trigger`; proactive runs need `catalog.manage`; held parts of bundles and bundles themselves are not changed by hand (`BundleProactiveApiTests`). E8: gateways need `payments.manage` (never for integrations), refunds `payments.refund`, finance `payments.read` (`PaymentApiTests`). |
| V8.2.2 | 1 | V8.2 General Authorization Design | Met | Data-specific access: requesters see only their own incidents and get 404 for others (B1, `IncidentApiTests`); B2: rights of a team lead apply only to members of the groups they lead and only while their staff permissions (second factor) apply, users outside the directory get 404 for groups, and searches by email run only for callers who may see emails (`GroupEndpoints`, `UserEndpoints`, `DirectoryApiTests`). B3: SLA clocks only for staff with `incidents.read` (field `sla`), never in the requester's view (`SlaApiTests`). B5: in-app notifications are read and marked read only by their recipient (others get 404), and a notification goes only to people the change concerns (requester, assignee, group members, SLA recipients, publishers other than the author); `NotificationApiTests`. C1: uploads for forms are visible and usable only by their uploader until sent; form answers are shown only to those who may see the ticket, and staff views of personal answers are audited. C2: services hidden by their access rules (groups, roles, departments, locations or below, named users) answer 404 and are left out of the catalog and search for everyone else; requests on behalf only for people the service is offered to; requesters see only their own service requests (others get 404) (`CatalogApiTests.Hidden_services_are_left_out_for_everyone_outside_the_allowed_groups_roles_departments_locations_and_users`). C3: approvals are seen and decided only by the person asked (others get 404); approvers may see the request they are asked about, with its answers and documents (`ApprovalEndpoints`, `ServiceRequestEndpoints.FindVisibleAsync`). C4: AI agents see and request only services the named person may see (hidden services answer not found), and cannot send files on someone's behalf (`McpCatalogTools`; `FulfilmentApiTests.An_ai_agent_finds_reads_and_requests_a_service_for_a_person_who_may_see_it`). C6: requesters see only their own complaints, inquiries and suggestions (404 otherwise); anonymous submitters reach theirs only with the number and tracking code (wrong pairs answer 404 alike, compared in constant time); the anonymous account is disabled, has no roles and is never listed (`CustomerVoiceApiTests`). D2: the portal search finds tickets only by the caller's own user reader (never staff roles), and My tickets lists only tickets the caller asked for (`PortalApiTests`, `PortalSearchTests`). C5: offers are seen, accepted and declined only by the person they are for (others get 404); a bundle's parts must be offered to the requester, and proactive services are offered or started only for people the service is offered to (hidden services stay hidden) (`BundleProactiveApiTests`). E8: only the requester (or who made the request) pays a fee; payments and receipts are seen by those who may see the request and by `payments.read` (others get 404) (`PaymentApiTests`). |
| V8.2.3 | 2 | V8.2 General Authorization Design | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. E1: field rules of an integration's role apply to its responses. |
| V8.3.1 | 1 | V8.3 Operation Level Authorization | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. |
| V8.4.1 | 2 | V8.4 Other Authorization Considerations | Met | PostgreSQL row-level security on every tenant-owned table (guard test), tokens only from the tenant's own realm, one search index per tenant. A1, A2, A4. B2: the eight directory tables included. B3: the ten SLA tables included (`SlaApiTests`). E6: the three MCP tables included; agents see only their own actions. C1: the seven forms and departments tables included (`FormApiTests.Form_tables_are_isolated_between_tenants`). C2: the six catalog tables included (`CatalogApiTests.Catalog_tables_are_isolated_between_tenants`). C3: the three workflow tables included, and every table of the Elsa workflow engine gets a tenant column filled by the database and forced row-level security (`zelvo_secure_elsa_tables()`; `WorkflowApiTests.Elsa_tables_have_forced_row_level_security_and_one_tenant_never_sees_anothers_workflows`). C4: `request_tasks` included. |

## V9 Self-contained Tokens

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V9.1.1 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.1.2 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.1.3 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.1 | 1 | V9.2 Token content | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.2 | 2 | V9.2 Token content | Not assessed |  |
| V9.2.3 | 2 | V9.2 Token content | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.4 | 2 | V9.2 Token content | Not assessed |  |

## V10 OAuth and OIDC

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V10.1.1 | 2 | V10.1 Generic OAuth and OIDC Security | Met | Backend for frontend: tokens stay on the web server, never in the browser. A2. |
| V10.1.2 | 2 | V10.1 Generic OAuth and OIDC Security | Not assessed |  |
| V10.2.1 | 2 | V10.2 OAuth Client | Met | Authorization code flow with PKCE, state and nonce (openid-client). A2. |
| V10.2.2 | 2 | V10.2 OAuth Client | Not assessed |  |
| V10.3.1 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.3.2 | 2 | V10.3 OAuth Resource Server | Not assessed |  |
| V10.3.3 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.3.4 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.4.1 | 1 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.2 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.3 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.4 | 1 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. E1: integration clients may use only the client credentials grant (no browser flows, no redirect URIs, `fullScopeAllowed` off). |
| V10.4.5 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.6 | 2 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.7 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.8 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.9 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.10 | 2 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.11 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.5.1 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.2 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.3 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.4 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.5 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.6.1 | 2 | V10.6 OpenID Provider | Not assessed |  |
| V10.6.2 | 2 | V10.6 OpenID Provider | Not assessed |  |
| V10.7.1 | 2 | V10.7 Consent Management | Not assessed |  |
| V10.7.2 | 2 | V10.7 Consent Management | Not assessed |  |
| V10.7.3 | 2 | V10.7 Consent Management | Not assessed |  |

## V11 Cryptography

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V11.1.1 | 2 | V11.1 Cryptographic Inventory and Documentation | Partly met | A5: key management for stored secrets documented in `docs/architecture/secrets-encryption.md` (data keys, master key file or Vault/OpenBao Transit, rotation with `rewrap-keys`); a full key management policy follows with F6. |
| V11.1.2 | 2 | V11.1 Cryptographic Inventory and Documentation | Partly met | A5: inventory started in `secrets-encryption.md` and `security-baseline.md` (data keys, master key, TLS certificates, audit hash chain, token signing keys in Keycloak). |
| V11.2.1 | 2 | V11.2 Secure Cryptography Implementation | Met | .NET (OpenSSL), Keycloak and OpenBao/Vault implementations; no custom primitives. |
| V11.2.2 | 2 | V11.2 Secure Cryptography Implementation | Met | Versioned formats (`zv1.` values, `kek_id` on data keys, audit chain version) allow algorithm changes. |
| V11.2.3 | 2 | V11.2 Secure Cryptography Implementation | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.1 | 1 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.2 | 1 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.3 | 2 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.4.1 | 1 | V11.4 Hashing and Hash-based Functions | Met | SHA-256 for the audit hash chain and key ids. A1, A5. |
| V11.4.2 | 2 | V11.4 Hashing and Hash-based Functions | Met | Passwords only in Keycloak (PBKDF2/Argon2 per Keycloak policy); OpenSearch internal users as bcrypt hashes. A2, A5. |
| V11.4.3 | 2 | V11.4 Hashing and Hash-based Functions | Met | SHA-256 for the audit hash chain and key ids. A1, A5. |
| V11.4.4 | 2 | V11.4 Hashing and Hash-based Functions | Not assessed |  |
| V11.5.1 | 2 | V11.5 Random Values | Met | `RandomNumberGenerator` for data keys and nonces; random session ids and CSRF tokens. A2, A5. C6: anonymous tracking codes come from `RandomNumberGenerator` and only their SHA-256 is stored. E8: payment references come from `RandomNumberGenerator` (24 characters of 32). |
| V11.6.1 | 2 | V11.6 Public Key Cryptography | Not assessed |  |

## V12 Secure Communication

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V12.1.1 | 1 | V12.1 General TLS Security Guidance | Partly met | A5: TLS 1.3 only for OpenSearch and RabbitMQ; public HTTPS (web, API, Keycloak) is terminated by the hardened install's proxy (F6). |
| V12.1.2 | 2 | V12.1 General TLS Security Guidance | Not assessed |  |
| V12.1.3 | 2 | V12.1 General TLS Security Guidance | Not assessed |  |
| V12.2.1 | 1 | V12.2 HTTPS Communication with External Facing Services | Partly met | External HTTPS with publicly trusted certificates: hardened install (F6) and custom domains (F1). F1: custom domains get publicly trusted certificates from ACME through Caddy, only for hosts a tenant verified by DNS (on-demand TLS "ask"), or the customer's own uploaded certificate (pair, validity and host checked; key encrypted at rest). The installation's own address and Helm come with F6. |
| V12.2.2 | 1 | V12.2 HTTPS Communication with External Facing Services | Open | External HTTPS with publicly trusted certificates: hardened install (F6) and custom domains (F1). |
| V12.3.1 | 2 | V12.3 General Service to Service Communication Security | Partly met | A5: TLS for OpenSearch, RabbitMQ and Vault; PostgreSQL, Valkey, Keycloak back channel and the OTLP collector are still plain inside the Compose network (follow-up, F6). |
| V12.3.2 | 2 | V12.3 General Service to Service Communication Security | Met | A5: clients validate server certificates (CA chain and host name) for OpenSearch, RabbitMQ and Vault. Tests: `ServiceTransportSecurityTests`. |
| V12.3.3 | 2 | V12.3 General Service to Service Communication Security | Partly met | See V12.3.1. |
| V12.3.4 | 2 | V12.3 General Service to Service Communication Security | Partly met | A5: internal certificates from a development CA that clients trust explicitly; installs use their own CA (F6). |

## V13 Configuration

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V13.1.1 | 2 | V13.1 Configuration Documentation | Not assessed |  |
| V13.2.1 | 2 | V13.2 Backend Communication Configuration | Partly met | A5: each service has its own least-privilege user for PostgreSQL (no BYPASSRLS), OpenSearch and RabbitMQ; Valkey still has one shared password. |
| V13.2.2 | 2 | V13.2 Backend Communication Configuration | Partly met | A5: each service has its own least-privilege user for PostgreSQL (no BYPASSRLS), OpenSearch and RabbitMQ; Valkey still has one shared password. |
| V13.2.3 | 2 | V13.2 Backend Communication Configuration | Met | No default credentials: `dev-up.sh` generates random passwords; RabbitMQ `guest` is loopback-only and not created. |
| V13.2.4 | 2 | V13.2 Backend Communication Configuration | Partly met | E1: outgoing webhooks reach only addresses the installation allows (`Integrations:Webhooks:AllowedPrivateNetworks`, HTTPS only, internal networks refused by default). The no-code connector builder (E7) will use the same policy. E4: AI providers likewise (`Ai:AllowedPrivateNetworks`, HTTPS only by default). E6: customers' MCP servers likewise. E2: mail servers and providers likewise (`Channels:Network:AllowedPrivateNetworks`; HTTPS for providers; IMAP and SMTP never send credentials in clear unless "No TLS" is chosen for a server on the same network). E8: payment gateways reach only addresses the installation allows (`Payments:AllowedPrivateNetworks`, HTTPS only unless `Payments:AllowInsecureHttp`). |
| V13.2.5 | 2 | V13.2 Backend Communication Configuration | Not assessed |  |
| V13.3.1 | 2 | V13.3 Secret Management | Partly met | A5: stored secrets encrypted with keys from a master key file or Vault/OpenBao; service passwords are environment variables (`.env`, git-ignored) until the hardened install (F6). F1: uploaded certificates' private keys and DKIM private keys are encrypted with the tenant's data key and never returned; the reverse proxy's TLS endpoints need a shared secret compared in constant time. E8: gateway credentials encrypted with `Zelvo.Secrets`, never returned or audited in clear (`PaymentApiTests.Gateways_keep_their_credentials_secret_and_go_live_only_after_a_sandbox_test_payment`). |
| V13.3.2 | 2 | V13.3 Secret Management | Met | A5: the API and worker can read only the CA certificate, not other services' keys; the Vault policy allows only encrypt/decrypt/rewrap with Zelvo's key. |
| V13.4.1 | 1 | V13.4 Unintended Information Leakage | Met | Images built from explicit project folders; `.git` is not copied. |
| V13.4.2 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.3 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.4 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.5 | 2 | V13.4 Unintended Information Leakage | Met | OpenAPI and the API explorer are on in Development only (elsewhere `OpenApi:Enabled=true`); Prometheus is not published, Grafana and admin consoles are bound to 127.0.0.1 and need a sign-in. |

## V14 Data Protection

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V14.1.1 | 2 | V14.1 Data Protection Documentation | Not assessed |  |
| V14.1.2 | 2 | V14.1 Data Protection Documentation | Not assessed |  |
| V14.2.1 | 1 | V14.2 General Data Protection | Not assessed |  |
| V14.2.2 | 2 | V14.2 General Data Protection | Not assessed |  |
| V14.2.3 | 2 | V14.2 General Data Protection | Met | A5: telemetry stays on the installation (no calls home); traces carry no query strings, client addresses or user ids. |
| V14.2.4 | 2 | V14.2 General Data Protection | Not assessed |  |
| V14.3.1 | 1 | V14.3 Client-side Data Protection | Not assessed |  |
| V14.3.2 | 2 | V14.3 Client-side Data Protection | Not assessed |  |
| V14.3.3 | 2 | V14.3 Client-side Data Protection | Met | The browser stores only the session cookie and the language cookie, and in demo installations the chosen sample organisation. A2, A3, D. |

## V15 Secure Coding and Architecture

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V15.1.1 | 1 | V15.1 Secure Coding and Architecture Documentation | Not assessed |  |
| V15.1.2 | 2 | V15.1 Secure Coding and Architecture Documentation | Met | CycloneDX SBOM per image in CI; Dependabot; Grype image scanning; NuGet and npm vulnerability gates. |
| V15.1.3 | 2 | V15.1 Secure Coding and Architecture Documentation | Not assessed |  |
| V15.2.1 | 1 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.2.2 | 2 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.2.3 | 2 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.3.1 | 1 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.2 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.3 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.4 | 2 | V15.3 Defensive Coding | Met | A5: client address forwarded by the web app and accepted by the API only from trusted proxies. |
| V15.3.5 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.6 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.7 | 2 | V15.3 Defensive Coding | Not assessed |  |

## V16 Security Logging and Error Handling

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V16.1.1 | 2 | V16.1 Security Logging Documentation | Not assessed |  |
| V16.2.1 | 2 | V16.2 General Logging | Met | Audit entries record when, who (actor), what and from where (client address, correlation id); logs carry trace ids. A1, A5. |
| V16.2.2 | 2 | V16.2 General Logging | Met | Audit and telemetry timestamps in UTC. |
| V16.2.3 | 2 | V16.2 General Logging | Not assessed |  |
| V16.2.4 | 2 | V16.2 General Logging | Met | A5: logs as OpenTelemetry records (OTLP) in Loki, correlated with traces. |
| V16.2.5 | 2 | V16.2 General Logging | Partly met | A5: no tokens, passwords, query strings or SQL values in telemetry; a written log inventory with protection levels is still to do. E1: the integration log stores request and response bodies with personal data and secrets masked (`PersonalDataMasker`), kept 30 days. E4: the AI log keeps only masked text (prompts are masked before they are sent; answers are stored as received, still with placeholders) and clears it after 30 days (`Ai:LogRetention`); telemetry of AI calls has model, tokens and outcome, never text. E5: AI decisions on tickets are audited (`ai.triage.*`, `ai.duplicate.*`) with ids only; AI jobs keep no text, only the error code of a failed attempt, and are removed after 7 days. E6: MCP tool calls are audited without their arguments (`ai.mcp.tool.called`); approval requests keep their arguments for the approver, audited by length only; MCP metrics carry tool and outcome only. |
| V16.3.1 | 2 | V16.3 Security Events | Not assessed |  |
| V16.3.2 | 2 | V16.3 Security Events | Partly met | Refused requests (401/403) are logged by ASP.NET Core and visible in Loki; they are not yet written to the audit log as security events. E1: every call made by an integration, including refused ones, is in the audit log (`integrations.api.called`) and the integration log. |
| V16.3.3 | 2 | V16.3 Security Events | Not assessed |  |
| V16.3.4 | 2 | V16.3 Security Events | Met | A5: TLS and dependency failures are logged and shown by readiness checks; rate-limit cache outages logged and counted. |
| V16.4.1 | 2 | V16.4 Log Protection | Not assessed |  |
| V16.4.2 | 2 | V16.4 Log Protection | Met | Audit log append-only and hash-chained in the database (triggers); verification endpoint. A1. |
| V16.4.3 | 2 | V16.4 Log Protection | Met | A5: logs sent over OTLP to Loki, separate from the services. |
| V16.5.1 | 2 | V16.5 Error Handling | Met | Problem details with stable codes and a trace id; no exception details (health responses tested). A1. |
| V16.5.2 | 2 | V16.5 Error Handling | Met | Search outbox with retries, dead-letter queue; rate limiting fails open with an alert; readiness reports each dependency. A4, A5. |
| V16.5.3 | 2 | V16.5 Error Handling | Met | Secrets that cannot be decrypted, tampered values and missing keys raise errors; nothing proceeds without them. A5. |

## V17 WebRTC

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V17.1.1 | 2 | V17.1 TURN Server | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.1 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.2 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.3 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.4 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.3.1 | 2 | V17.3 Signaling | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.3.2 | 2 | V17.3 Signaling | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
