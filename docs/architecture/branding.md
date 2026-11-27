# White-label branding (task F1, module 11)

Covers module 11: product name in English and Arabic; logos for light and dark mode, favicon and sign-in image; brand colours and a font with automatic contrast checks; a live preview before publishing; custom domains with automatic TLS; the email sender name and the customer's own email domain with a guided SPF, DKIM and DMARC check; branded email and notification templates and branded PDF exports; and the "Powered by Zelvo" mark, which is always shown.

Product owner decisions (2026-11-24):

- **Custom domains and automatic TLS through Caddy** (Apache-2.0) in front of the web app: certificates by ACME (Let's Encrypt, or the customer's own ACME server for offline networks), only for hosts a tenant verified, plus the customer's own uploaded certificate. Adds Caddy to the stack (Compose now; Helm with F6).
- **Branded PDF exports with headless Chromium in the worker**: correct Arabic shaping and right to left, the same look as the screens. First exports: incidents and service requests; F2's reports reuse it.

## Code

| Piece | Where |
| --- | --- |
| Tables `tenant_brandings`, `tenant_domains`, `sender_domains`, `document_exports`; branding images in `ticket_attachments` (`branding_slot`) (row-level security; migration `Branding`) | `src/api/Zelvo.Data/Branding` |
| Branding value, fonts and the WCAG contrast checks | `src/api/Zelvo.Data/Branding/BrandingValue.cs` |
| Reading the published branding and its images, host names, DNS lookups (DnsClient, Apache-2.0, new dependency) | `src/api/Zelvo.Tickets/Branding` |
| PDF exports: document, Chromium renderer, worker job, labels in English and Arabic | `src/api/Zelvo.Tickets/Exports` |
| REST API: `/branding`, `/settings/branding/...`, `/platform/hosts/{host}`, `/incidents/{id}/exports`, `/service-requests/{id}/exports`, `/exports/{id}`; the proxy's `/internal/tls/...` | `src/api/Zelvo.Api/Branding/BrandingEndpoints.cs` |
| Branded emails: letterhead, embedded logo, tenant sender with DKIM | `src/api/Zelvo.Notifications` |
| Sign-in pages: realm display name and the web client's addresses | `KeycloakAdminClient.ApplyBrandingAsync` |
| Web: published look on every page, host → tenant, Settings → Branding, PDF buttons, image route | `src/web/src/lib/branding.ts`, `src/web/src/lib/host-tenant.ts`, `src/web/src/components/settings/BrandingPanel.tsx`, `src/web/src/app/branding/[tenant]/[slot]` |
| Reverse proxy | `deploy/compose/caddy/Caddyfile`, service `proxy` |
| Demo seeder `f1-branding` | `src/api/Zelvo.Api/Demo/DemoBrandingSeeder.cs` |

## Draft, checks and publishing

Settings → Branding (`branding.manage`, tenant administrators) edits a **draft** (`tenant_brandings.draft`); everyone sees the **published** value (`published`), Zelvo's default look until the first publish. The draft holds:

- the product name in English and Arabic (up to 80 characters each), shown instead of "Zelvo" in the header, the browser tab (`%s · <product>`), emails, PDFs, the chat widget and the sign-in pages;
- the brand colour for light mode and for dark mode (`#rrggbb`);
- a font from the bundled list, each with Arabic and Latin letters and working offline: IBM Plex Sans Arabic (default), Cairo, Tajawal, Almarai (`@fontsource`, OFL-1.1);
- images: logo for light and for dark backgrounds, favicon, sign-in image, and from task J1 the installable app's icon (a square PNG of at least 512 pixels that Zelvo resizes; see `installable-app.md`).

**Contrast checks** (WCAG 2.2 AA, 4.5:1), computed the same way in C# (`BrandingValue.Checks`) and in the browser as the administrator types (`contrastChecks`): the brand colour as text on the light surface (`#ffffff`) and the surface colour as button text on the brand colour, and the same for dark mode (surface `#141a20`). **Publishing is refused** (`409 branding-not-ready`) while a check fails or an image is not scanned clean, and the problems are listed.

**Live preview**: the editor shows the draft in English and Arabic, in light and dark mode (product name, logo, text, link and button), before anything is published. Publishing (`POST /settings/branding/publish`, audited `branding.published`) copies the draft; "throw the draft away" goes back to the published one. The web app keeps the published branding in Next's data cache for a minute and expires it at once when an administrator publishes through it.

**Applied on every page**: the root layout writes the brand colours and the font as CSS variables (`--color-brand`, `--font-sans`) for light and dark mode. Only values that pass the format checks get into the style sheet (colours `#rrggbb`, fonts from the fixed list), so nothing else can be injected. The header shows the logo (dark-mode logo with `prefers-color-scheme`) next to the product name, which also gives the logo its text alternative.

## Images

Uploaded to `POST /settings/branding/images/{slot}`: PNG, JPEG or WebP up to 2 MB, the favicon also ICO up to 256 KB. The content must match the type (magic bytes); **SVG is not accepted** (it can carry scripts). Images are stored like attachments (`ticket_attachments` with `branding_slot`, content in PostgreSQL) and **scanned by ClamAV** before they can be published, as decided for attachments in B1. The form-upload clean-up skips them.

Served by `GET /branding/images/{slot}` (published, anyone, `Cache-Control: public, max-age=300`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'`) or with `draft=true` for `branding.manage` (live preview). The web app serves them at `/branding/<tenant>/<slot>` so Keycloak's sign-in pages can show the sign-in image.

## Sign-in pages

Publishing (and verifying a domain) updates the tenant's realm through Keycloak's admin API: the display name is the product name, and the HTML display name is the sign-in image with the product name, built by Zelvo with every value encoded. The `zelvo-web` client's redirect URIs and web origins become the installation's address plus each verified custom domain. When Keycloak cannot be reached the change is kept in Zelvo and applied by the next publish or verification.

## Custom domains and TLS

1. **Add** a host in Settings → Branding → Custom domains (lower case, punycode for international names; `localhost`, `.local`, `.internal` and the installation's own host refused). A host belongs to one tenant in the whole installation (unique index; the clash is reported without saying which tenant has it). At most 10 per tenant.
2. **DNS**: a CNAME to the installation (`Branding:PublicHost`) and a TXT record `_zelvo-verify.<host>` = `zelvo-verify=<token>` (a random token per domain).
3. **Verify** looks the TXT record up (`IDnsLookup`, no cache). Verified hosts are served: `GET /api/v1/platform/hosts/{host}` (anyone; the SECURITY DEFINER function `zelvo_tenant_for_host` returns only the slug) tells the web app which tenant a host serves. The web app (`host-tenant.ts`) asks for any host other than its own address, keeps answers (unknown hosts too) for a minute, and then uses `https://<host>` as its address for that tenant: sign-in redirects, cookies and links. The host decides; the tenant cookie of demo installations cannot change it.
4. **Certificates** (Caddy, `deploy/compose/caddy/Caddyfile`):
   - **Automatic**: on-demand TLS with ACME; Caddy asks `GET /internal/tls/ask?domain=<host>&token=…` before getting a certificate, and Zelvo answers 200 only for verified hosts, so nobody can make the proxy request certificates for other names. `ZELVO_ACME_CA` points at the customer's own ACME server for offline networks.
   - **Uploaded**: the customer's PEM certificate chain and unencrypted private key (`PUT /settings/branding/domains/{id}/certificate`). The pair must belong together, be valid now and match the host. The key is encrypted with the tenant's data key (`Zelvo.Secrets`, purpose `branding.domain:<id>.privateKey`) and never returned. Caddy fetches it in the TLS handshake from `GET /internal/tls/certificate?server_name=<host>&token=…`; Zelvo answers 204 for hosts without an uploaded certificate, and Caddy uses ACME.
   - The `/internal/tls/...` endpoints are outside `/api`, answer 404 without the shared secret (`Branding:TlsToken`, `ZELVO_TLS_TOKEN`, compared in constant time), are left out of the OpenAPI document, and the proxy forwards public traffic only to the web app.

Compose runs Caddy as `proxy` on `https://localhost:8443` (development uses Caddy's internal certificate authority; `ZELVO_PUBLIC_TLS` empty uses ACME for the installation's own host). On Kubernetes the chart runs the same proxy (task F6, `deploy/helm/zelvo/files/Caddyfile`).

## Email sender domain

Settings → Branding → Email sender domain (`PUT /settings/branding/email`): the local part and the domain, for example `noreply@entity.gov.ae`.

- A new domain gets a new **DKIM key** (RSA 2048, selector `zelvo<yyyyMM>`). The private key (PKCS #8) is encrypted with the tenant's data key; the public key is shown as the DNS record.
- The **guided check** (`POST /settings/branding/email/check`) looks up three records and shows each as found, missing or different:
  - SPF on the domain, which must contain the installation's include (`Branding:SpfInclude`, for example `include:spf.mail.example.ae`) when the operator set one;
  - the DKIM key at `<selector>._domainkey.<domain>`;
  - DMARC at `_dmarc.<domain>`, advised (a `p=quarantine` record is suggested) but not required.
- With **SPF and DKIM passed**, notification emails (B5) go out `From: <organisation name> <local@domain>`, **signed with DKIM** by Zelvo (MimeKit, relaxed canonicalisation; From, To, Subject, Date, Message-ID, Content-Type) and with Message-IDs on that domain. Until then, and after the sender is removed, the installation's sender is used. Emails from E2 mailboxes keep their mailbox's own address.

## Branded emails and templates

The letterhead of every notification email (B5) uses the published brand colour for the heading, links and button, shows the light logo (embedded as `cid:zelvo-logo`, so mail apps that block remote images still show it), the organisation's bilingual name (GOV-8), and the product name beside "Powered by Zelvo" in the footer. The wording stays the tenant's templates (B5). WhatsApp and SMS templates come with their channels (E3).

## PDF exports

`POST /incidents/{id}/exports` and `POST /service-requests/{id}/exports` (`{language}`; default the caller's language) queue a `document_exports` row for whoever can see the ticket: staff get the staff view (category, group, assignee, internal notes), requesters the public view of their own. At most five pending per person. The worker's `DocumentExportJob` (tenant function `zelvo_export_tenants`, a lease, three attempts with a pause) builds the HTML (`TicketDocument`):

- the logo (embedded as `data:`), product and organisation names, brand colour and font (bundled font files in the worker image, with Arabic and Latin ranges; Noto as fallback);
- facts, description, resolution notes and the conversation, with labels from `Exports/Texts/{en,ar}.json`, dates in the person's calendar and digits (LANG-8) and the tenant's time zone;
- right to left for Arabic, people's text HTML-encoded and isolated (`dir="auto"`, `<bdi>`);
- the footer "Powered by Zelvo" / "بتقنية زيلفو" with the export date.

Then headless Chromium prints it to PDF (`ChromiumPdfRenderer`, A4): the document's policy (`default-src 'none'`, images only as `data:`, fonts only from local files) allows no scripts and no network, every host name resolves to nothing (`--host-resolver-rules`), and a fresh profile is used each time. The person who asked polls `GET /exports/{id}` and downloads `GET /exports/{id}/file` (audited `export.downloaded`); nobody else sees it, and exports are removed after a day (`zelvo_purge_document_exports`). The worker image copies in Chromium's headless shell as Playwright publishes it (pinned to the end-to-end tests' Playwright version, 1.65.1, so both move together), installs the libraries it needs and `fonts-noto-core`, and copies the bundled fonts from the npm packages (`Pdf:ChromiumPath`, `Pdf:FontsDirectory`). Ubuntu's `chromium` package is not used: on Ubuntu 24.04 it only installs the snap, which brings snapd and does not run in a container.

## "Powered by Zelvo"

Always shown, in the portal footer, the chat widget, emails and PDFs, in the page's language. It is not part of branding: no setting, value or API can hide or change it.

## Settings and permissions

| What | Who | Default |
| --- | --- | --- |
| Draft, images, publish, custom domains, certificates, sender domain | `branding.manage` (tenant administrators; not for integrations, for approval) | Zelvo's default look, no domains, the installation's sender |
| `Branding:PublicHost`, `Branding:TlsToken`, `Branding:SpfInclude` | installation operator | none |
| `Pdf:ChromiumPath`, `Pdf:FontsDirectory`, `Pdf:Timeout` | installation operator (set by the worker image) | `chromium`, none, 60 s |
| Caddy: `ZELVO_PUBLIC_HOST`, `ZELVO_PUBLIC_TLS`, `ZELVO_ACME_CA`, `ZELVO_ACME_EMAIL`, ports | installation operator | `localhost`, internal CA, Let's Encrypt, 8088/8443 |

Every change is audited: draft, images, publish and discard, domains (added, checked, certificate uploaded or removed, removed), sender domain (set, checked, removed), exports (asked for, downloaded). Keys are audited only as set.

## Not in F1

- WhatsApp and SMS template branding: with E3.
- Report PDFs: added by F2 with the same renderer (`docs/architecture/reporting.md`).
- Not tried against a real ACME server or a real customer DNS zone yet; DNS checks were tested with a fake resolver.
