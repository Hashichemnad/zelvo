# WCAG 2.2 AA audit (task F7)

Zelvo must meet WCAG 2.2 AA in English and Arabic, with keyboard-only use and screen readers in both languages (GOV-9, NFR "Accessibility"). Every task since setup has checked its own pages with axe in its end-to-end tests. Task F7 adds a full audit of every page, for every persona, in both languages, and fixes what it found.

Audit date: 2026-11-26, against the demo installation (`make demo`), whose sample data fills every module, with Chromium (Playwright's build) and axe-core through `@axe-core/playwright`.

## How the audit runs

`tests/e2e/specs/accessibility.spec.ts`, Playwright project `demo-accessibility` (runs after the demo tests in `make e2e-demo` and in CI's `e2e-demo` job):

| Check | What it does | WCAG 2.2 |
| --- | --- | --- |
| axe on every page | Each persona (visitor, requester, agent, team lead, approver, tenant administrator, executive) signs in with the demo sign-in and opens every page its role uses, about 100 pages, each in English and Arabic. Detail pages (an incident, a request, an article, a user, a role, a form…) are found by following the first link on their list page. axe runs the WCAG 2.0, 2.1 and 2.2 A and AA rules on each. | All criteria axe can test, including 1.1.1, 1.3.1, 1.4.3, 2.4.2, 2.5.8, 3.1.1, 4.1.2 |
| Language and direction | Every page says its language (`lang="en"` / `lang="ar"`) and direction (`dir="ltr"` / `dir="rtl"`). | 3.1.1, 1.3.2 |
| Keyboard | On the main pages of visitors, requesters, agents and administrators: the first Tab reaches "Skip to main content", which moves focus to the main content; 40 more Tabs move through the page without getting stuck; every focused control shows a visible focus indicator. | 2.1.1, 2.1.2, 2.4.1, 2.4.3, 2.4.7 |
| Reflow | Every page of the audit at 320 CSS pixels wide (1280 px at 400 % zoom), in both languages: the page scrolls only vertically; wide data tables scroll inside their own box. | 1.4.10 |
| Content-Security-Policy | Anything the new policy (ASVS V3.4.3) blocks on any page is reported too, so the policy cannot silently break a page. | — |

Findings are written per persona to `tests/e2e/accessibility-report/<persona>.json` (git-ignored) before a test fails, so one run shows everything. Detail pages that depend on data the demo may not have at that moment (an approval waiting for the approver, a webhook, a paid receipt) are listed as skipped rather than failed.

## What the audit found and what was fixed

The pages built in earlier tasks were already clean for axe; the new checks found these, all fixed in F7:

| Finding | Where | Criterion | Fix |
| --- | --- | --- | --- |
| The read-only "attempted username" field on Keycloak's sign-in pages has no accessible name (axe `label`, critical). Shown when Keycloak asks for the password or second factor again, for example the privacy officer's fresh sign-in. | Keycloak sign-in pages, both languages | 1.3.1, 4.1.2 | The zelvo login theme carries Keycloak 26.8.2's `template.ftl` with the field given its label as `aria-label` (`src/keycloak/src/main/resources/theme/zelvo/login/template.ftl`). `scripts/check-keycloak-version.sh` fails when Keycloak is upgraded without comparing the copy. |
| Date fields lose their focus ring when focus moves to the calendar button inside them (only the browser's thin ring around the small icon stays). | Every date and date-time field (forms, reports, payments, SLA calendars, updates) | 2.4.7 | `globals.css`: date and time fields keep the brand-coloured ring while any part of them has focus (`:focus-within`). |
| Pages scroll sideways at 320 px: wide data tables without their own scrolling box, and visually hidden texts inside scrolling tables placed against the page. | Settings → Users, Catalog, Forms, Skills, Messaging, Groups, Roles, Departments, Translations, Branding, Privacy, License, Updates; requests, my requests, payments, scheduled reports, approvals, the customer voice report, the setup wizard and the workspace | 1.4.10 | 25 tables wrapped in a horizontally scrolling box (`overflow-x-auto`); every such box now contains its absolutely positioned children (`globals.css`); the Settings, setup and workspace grids get `grid-cols-1` and `minmax(0, 1fr)` columns so a wide child cannot widen the page; the article editor's language tabs wrap; file pickers never exceed the screen. |

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

Record the results of these sessions in this document (date, tester, assistive technology and version, journeys, findings).
