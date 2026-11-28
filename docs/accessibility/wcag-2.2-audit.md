# WCAG 2.2 AA audit (tasks F7 and L1)

Zelvo must meet WCAG 2.2 AA in English and Arabic, with keyboard-only use and screen readers in both languages (GOV-9, NFR "Accessibility"). Every task since setup has checked its own pages with axe in its end-to-end tests. Task F7 adds a full audit of every page, for every persona, in both languages, and fixes what it found.

Audit date: 2026-11-26 (Phase 1), extended to Phase 2 on 2026-11-28 (task L1), against the demo installation (`make demo`), whose sample data fills every module, with Chromium (Playwright's build) and axe-core through `@axe-core/playwright`.

## How the audit runs

`tests/e2e/specs/accessibility.spec.ts`, Playwright project `demo-accessibility` (runs after the demo tests in `make e2e-demo` and in CI's `e2e-demo` job):

| Check | What it does | WCAG 2.2 |
| --- | --- | --- |
| axe on every page | Each persona (visitor, requester, agent, team lead, approver, tenant administrator, executive) signs in with the demo sign-in and opens every page its role uses, about 100 pages, each in English and Arabic. Detail pages (an incident, a request, an article, a user, a role, a form…) are found by following the first link on their list page. axe runs the WCAG 2.0, 2.1 and 2.2 A and AA rules on each. | All criteria axe can test, including 1.1.1, 1.3.1, 1.4.3, 2.4.2, 2.5.8, 3.1.1, 4.1.2 |
| Language and direction | Every page says its language (`lang="en"` / `lang="ar"`) and direction (`dir="ltr"` / `dir="rtl"`). | 3.1.1, 1.3.2 |
| Keyboard | On the main pages of visitors, requesters, agents and administrators: the first Tab reaches "Skip to main content", which moves focus to the main content; 40 more Tabs move through the page without getting stuck; every focused control shows a visible focus indicator. | 2.1.1, 2.1.2, 2.4.1, 2.4.3, 2.4.7 |
| Reflow | Every page of the audit at 320 CSS pixels wide (1280 px at 400 % zoom), in both languages: the page scrolls only vertically; wide data tables scroll inside their own box. | 1.4.10 |
| Content-Security-Policy | Anything the new policy (ASVS V3.4.3) blocks on any page is reported too, so the policy cannot silently break a page. | — |
| Phase 2 on a phone (task L1) | The Phase 2 pages of the requester, agent, team lead and administrator (CMDB, an item, assets, an asset, the scanner, problems, a problem, known errors, changes, a change, the calendar, the board and a meeting, custom records and apps, My workload and the wellbeing dashboard, passkeys, malware definitions and the Phase 2 settings) on a Pixel 7 screen (412 CSS pixels, touch) in both languages: axe in the phone layout, no sideways scrolling, and the header's Menu button (task J1) that opens the links and says so (`aria-expanded`). Runs in the other sample organisation, like the reflow tests. | As axe on every page, plus 1.4.10, 4.1.2 |

Findings are written per persona to `tests/e2e/accessibility-report/<persona>.json` (`<persona>-phone.json` for the phone pass) (git-ignored) before a test fails, so one run shows everything. Detail pages that depend on data the demo may not have at that moment (an approval waiting for the approver, a webhook, a paid receipt) are listed as skipped rather than failed.

### Phase 2 pages (task L1)

Each Phase 2 task added its pages to the audit as it was built (G1 the CMDB, G2 the review queue, G3 assets, H1–H5 problems, known errors, suggestions, changes, the calendar and the board, J1 the offline page, J3 passkeys, J4 the Teams tab, K2 and K3 records and apps, M1 malware definitions, M2 the setup link page). L1 checked every page of the web app against the audit's list and added the ones still missing: the agent's **My workload** (`/account/workload`), the team lead's **wellbeing dashboard** (`/wellbeing`) and **Settings → Wellbeing** (task K4), and an imported app package's review page (`/settings/apps/imports/[id]`, task K3; skipped when the demo has no import, as its HR app is installed by the seeder). The pages not in the audit are not pages a person reads: the catch-all not-found route (the audit opens `/no-such-page`), the Teams sign-in pop-up's end page, the payment return page (it redirects) and the workspace route with a ticket (covered as `/workspace/[ticket]`). L1 also added the phone pass above.

The run of the extended audit is CI's `e2e-demo` job on the L1 pull request; record any findings and their fixes in the table below.

## What the audit found and what was fixed

The pages built in earlier tasks were already clean for axe; the new checks found these, all fixed in F7:

| Finding | Where | Criterion | Fix |
| --- | --- | --- | --- |
| The read-only "attempted username" field on Keycloak's sign-in pages has no accessible name (axe `label`, critical). Shown when Keycloak asks for the password or second factor again, for example the privacy officer's fresh sign-in. | Keycloak sign-in pages, both languages | 1.3.1, 4.1.2 | The zelvo login theme carries Keycloak 26.8.2's `template.ftl` with the field given its label as `aria-label` (`src/keycloak/src/main/resources/theme/zelvo/login/template.ftl`). `scripts/check-keycloak-version.sh` fails when Keycloak is upgraded without comparing the copy. |
| Date fields lose their focus ring when focus moves to the calendar button inside them (only the browser's thin ring around the small icon stays). | Every date and date-time field (forms, reports, payments, SLA calendars, updates) | 2.4.7 | `globals.css`: date and time fields keep the brand-coloured ring while any part of them has focus (`:focus-within`). |
| Pages scroll sideways at 320 px: wide data tables without their own scrolling box, and visually hidden texts inside scrolling tables placed against the page. | Settings → Users, Catalog, Forms, Skills, Messaging, Groups, Roles, Departments, Translations, Branding, Privacy, License, Updates; requests, my requests, payments, scheduled reports, approvals, the customer voice report, the setup wizard and the workspace | 1.4.10 | 25 tables wrapped in a horizontally scrolling box (`overflow-x-auto`); every such box now contains its absolutely positioned children (`globals.css`); the Settings, setup and workspace grids get `grid-cols-1` and `minmax(0, 1fr)` columns so a wide child cannot widen the page; the article editor's language tabs wrap; file pickers never exceed the screen. |
| Task L1 (phone pass): the "Back to assets" link above an asset is smaller than a 24-pixel touch target and too close to the assets menu below it (axe `target-size`, serious). | `/assets/[id]` on a phone, both languages | 2.5.8 | The link is a block of at least 24 pixels (`inline-block min-h-6 py-0.5`). |
| Task L1 (phone pass): an asset's page in Arabic is 3 pixels wider than the phone. Each licence in its list was marked `dir="auto"`, so an English licence name made the list item left to right inside the right-to-left list and put its bullet outside the list, on the left. | `/assets/[id]` in Arabic (also the connector errors list of Settings → Connectors, same pattern) | 1.4.10 | The list items follow the page's direction; only the name inside them is `dir="auto"`. Checked by rendering the page in Chromium at 412 pixels right to left: 414 before, 412 after. |

After the fixes, every check passes for every persona in both languages, with no content blocked by the Content-Security-Policy.

## What automated checks cannot cover

Automated checks find about a third to a half of WCAG failures. These are for a person, with real assistive technology, before release (book with the external penetration test):

- **Screen readers in both languages**: NVDA with Firefox and JAWS with Chrome on Windows, VoiceOver with Safari on macOS and iOS, TalkBack with Chrome on Android; English and Arabic voices. Journeys: sign in with an email code and with a second factor; report an incident; request a service with a form (including the signature field's typed alternative and file uploads); follow a ticket and reply; rate with the happiness meter; agent workspace (queue, open, reply, internal note, live updates announced politely); approve a request; Settings → Branding preview; reports (check that every chart has a text or table alternative).
- **Meaningful sequence and reading order in Arabic** with mixed Arabic and Latin text (numbers, emails, ticket numbers in `dir="ltr"` / `<bdi>`): reading order and pronunciation.
- **Text spacing** (1.4.12) and **resize text to 200 %** (1.4.4) on the main pages.
- **Focus not obscured** (2.4.11) by the header, the demo panel, the chat button and the maintenance banner while tabbing.
- **Dragging movements** (2.5.7): the form builder's move buttons and keyboard dragging (C1) with a screen reader.
- **Consistent help** (3.2.6) and **redundant entry** (3.3.7) across the multi-step flows (setup wizard, bundle requests).
- **Accessible authentication** (3.3.8): email codes can be pasted; passwords and codes allow password managers.
- **Tenant branding**: colours are checked for AA contrast before they can be published (F1); check a published tenant logo is announced with the product name.
- **Emails and PDF exports**: notification emails (B5) and branded PDFs (F1, F2) in both languages with a screen reader.
- **Phase 2 journeys** (task L1): the CMDB relationship map's keyboard navigation and its live region, and the equal list view (G1); scanning an asset label from a photo and typing the tag instead (G3); the 5 Whys and fishbone editors and "Insert into reply" of a workaround (H1); the risk questionnaire, conflicts and the board's votes and decision (H3, H4); the wellbeing dashboard and My workload (K4); installing the app, the offline page and camera attachments on real iPhones and Android phones (J1); adding and using a passkey with VoiceOver and TalkBack (J3); the Teams tabs and the "Raise a ticket" dialog in Teams' own screen reader support (J4).

Record the results of these sessions in this document (date, tester, assistive technology and version, journeys, findings).
