# Installable web app (task J1, module 19)

Covers module 19's installable web app (product owner decision 2026-11-26: a progressive web app instead of native iOS and Android apps): each customer's app installs with its own name and icon from its own address, opens offline, takes photos and videos with the phone's camera, and has phone layouts. Web push (J2), passkeys (J3), the Teams app (J4) and barcode scanning (G3) build on it.

Decision 13 (2026-11-26), **offline-friendly**: the app opens without a connection, shows the requester's own last-viewed tickets read-only (cleared at sign-out), keeps unsent new tickets and replies on the device until the connection is back; agents' queues are never cached; a tenant setting turns offline data off.

## Code

| Piece | Where |
| --- | --- |
| App icon slot `AppIcon` in the branding images (migration `InstallableAppIcon`: the `branding_slot` check) | `src/api/Zelvo.Data/Branding/BrandingValue.cs` |
| PNG reader, resizer and writer (no image library), icon sizes | `src/api/Zelvo.Tickets/Branding/PngImage.cs`, `AppIcons.cs` |
| REST API: `GET /app`, `GET /branding/app-icons/{name}`, `GET/PUT /settings/app`; tenant setting `app.offlineData` | `src/api/Zelvo.Api/Branding/AppEndpoints.cs` |
| Video and HEIC file types for camera attachments (`mp4`, `mov`, `webm`, `heic`; known, not allowed by default) | `src/api/Zelvo.Tickets/FileTypes.cs` |
| Manifest per host and language, app icon route, service worker | `src/web/src/app/manifest.webmanifest`, `src/web/src/app/app-icons/[tenant]/[name]`, `src/web/src/app/sw.js`, `src/web/src/lib/pwa/manifest.ts`, `service-worker.ts` |
| Offline store (IndexedDB), offline page | `src/web/src/lib/pwa/offline-store.ts`, `src/web/src/app/[locale]/offline`, `src/web/src/components/pwa/OfflineView.tsx` |
| On every page: service worker registration, owner check, sending unsent work, offline banner, install offer | `src/web/src/components/pwa/AppRuntime.tsx` |
| Camera buttons, header menu on phones, remembering a ticket | `src/web/src/components/pwa/CameraButtons.tsx`, `CollapsibleNav.tsx`, `RememberTicket.tsx` |
| Settings → Branding → Installable app (QR code, offline data) | `src/web/src/components/settings/InstallableApp.tsx` |
| Zelvo's own icons (used until a tenant publishes an app icon) | `src/web/public/zelvo-icons` |
| Tests | `InstallableAppTests` (API), `src/web/src/lib/pwa/pwa.test.tsx`, `tests/e2e/specs/installable-app.spec.ts`; `/offline` in the accessibility audit |

## Name, colours and icon per host

The web app serves `/manifest.webmanifest?lang=en|ar` for the host the request came to: the installation's address or a verified custom domain (F1's `host-tenant.ts`), so the host decides the tenant. The manifest takes the published product name (short name: up to 12 characters, else the first word), the light brand colour as `theme_color`, `lang` and `dir` (right to left for Arabic), `start_url` `/<lang>?source=app`, `scope` `/`, `display: standalone` and the same `id` (`/`) in both languages, so switching language never installs a second app. Every page links the manifest of its language, the iOS home-screen icon (`apple-touch-icon`) and `apple-mobile-web-app-*` meta tags, and sets `theme-color` for light and dark mode.

**App icon**: a new branding image (Settings → Branding → Images → App icon), a **square PNG of at least 512 × 512 pixels** (at most 4096 a side, not interlaced, 2 MB), checked like the other images (content type, ClamAV scan, publish with the branding). Zelvo resizes it itself (no new dependency: `PngImage` reads every non-interlaced PNG colour type and bit depth with `ZLibStream`, averages areas with alpha weighting and writes RGBA PNGs):

| Name | Size | Use |
| --- | --- | --- |
| `any-192`, `any-512` | 192, 512 | manifest, purpose `any` (transparency kept) |
| `maskable-192`, `maskable-512` | 192, 512 | manifest, purpose `maskable`: the icon at 56 % in the middle of a full background, so round and squircle masks never cut it (the safe zone is a circle of 80 %) |
| `apple-180` | 180 | iOS home screen: on a full background (iOS shows transparency as black) |

The background is the icon's own when its top-left pixel is opaque, else white. Sizes are made on request and kept in memory for an hour under the image's hash (`GET /api/v1/branding/app-icons/{name}`, public, `nosniff`, `default-src 'none'`), and served to browsers at `/app-icons/<tenant>/<name>?v=<hash>`. Without a published app icon the manifest uses Zelvo's icons (`/zelvo-icons/*.png`).

## Service worker and offline

`/sw.js` (scope `/`, `Cache-Control: no-store`, `Service-Worker-Allowed: /`) is generated with the build's version (`ZELVO_APP_BUILD`, set in `next.config.ts`), so each release replaces the cache.

- **Pages and API answers always come from the network.** The worker caches nothing the API answers and no page except the offline page; `/bff/...` calls, images and the manifest pass through untouched.
- **App shell**: Next.js's hashed static files (`/_next/static/`) and Zelvo's icons, cache first as they are used.
- **Offline page**: `/en/offline` and `/ar/offline` with the scripts and styles they need, fetched **without cookies** when the worker installs, so they hold nobody's name. A page that cannot load without a connection is redirected to the offline page of its language (the page must be shown at its own address for Next.js to start).
- The offline page shows only what the device keeps (`offline-store.ts`, IndexedDB `zelvo-offline`):
  - the requester's **own last-viewed tickets** (incidents and service requests, at most 20): number, title, state, description, public replies, dates as the person saw them (their calendar and digits), written by the requester's own ticket pages only (`RememberTicket`), never by staff pages, so agents' queues are never kept;
  - **unsent work**: a requester's new incident or reply that failed for want of a connection is kept and sent in order when the connection comes back (`online` event and every page load); an item the API refuses is kept with the reason for the person to discard, and the run stops at the next connection failure. Files and photos are not kept (said on the page and in the form).
- **One person's data**: the store records its owner. Another person signing in, signing out (the next page loads as a visitor) and the tenant turning offline data off clear everything. The offline page itself never clears it.
- **Tenant setting** `app.offlineData` (Settings → Branding → Installable app, `branding.manage`, on by default, audited `app.settings.changed`). Off: the app still opens offline but keeps nothing, and devices clear what they kept at their next page.
- A banner says when the device is offline and links to the offline page; a hidden live region announces sent and refused work.

## Install

`AppRuntime` keeps the browser's `beforeinstallprompt` and offers "Install" in a bar; on iPhone and iPad Safari (no install prompt) the bar says to tap Share, then Add to Home Screen. "Not now" is remembered on the device (`localStorage`), and nothing is offered in the installed app. **QR code**: Settings → Branding → Installable app shows a QR code (`uqr`, MIT, new dependency) that opens the installation's address or one of the tenant's verified custom domains, with a download as SVG, so people reach their own organisation's app and branding.

## Camera attachments

`CameraButtons` adds "Take a photo" and "Record a video" (`<input capture="environment">`, the phone's camera app) on touch devices beside the existing file pickers: incident attachments (the incident page, where replies are written), "Report an incident" (new: photos and files chosen with the report are uploaded through the incident's attachment upload once it exists; refusals are shown on the incident), file fields of forms (C1) and required documents of service requests and bundles (C2, C5). Every file goes through the existing checked and ClamAV-scanned uploads. Only kinds the place accepts are offered: photos where JPEG, PNG, WebP or HEIC is allowed, videos where MP4, QuickTime or WebM is; the video types are new known types that administrators add in Settings → Attachments (the default list is unchanged). `Permissions-Policy: camera=()` stays: the `capture` attribute uses the camera app, not the page's camera access.

## Phone layouts

The header's links fold behind a **Menu** button below the `sm` width (`aria-expanded`, closes on the next page); the workspace already shows the list or the open incident on phones. My tickets, ticket pages, approvals and chats keep the single-column layouts the F7 reflow audit checks at 320 px.

## Not in J1

- **Chat attachments**: chats have no file upload yet, so they have no camera button (a chat file API is needed first).
- Web push (J2, see `notifications.md`), passkeys (J3, see `identity-access.md`), the Teams tab (J4, see `messaging.md`), barcode scanning (G3).
- Phase 2 modules are not yet separate license modules; the app follows the licensed modules of each page.
- Not tried on real iOS and Android devices yet (Chromium with a phone profile only).
