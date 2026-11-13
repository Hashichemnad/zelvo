# Settings and localization (task A3)

Covers LANG-1, LANG-2, LANG-3 and LANG-8, the base of GOV-8, and the Settings shell that later modules add their sections to.

## Tenant settings store

- **Storage.** Table `tenant_settings`: one row per tenant and key, with the value stored as JSON (`jsonb`).
  - Row-level security applies like on every tenant-owned table.
  - Every change is audited field by field, as entity `tenant_setting` with id `<tenant>,<key>`.
- **Definitions live in code.** Each setting is a `SettingDefinition<T>` in `Zelvo.Api/Settings` that sets the key, type, default, validation and the permission needed to change it.
  - A key without a row has its default, so new settings need no data migration.
  - A stored value that is no longer valid also falls back to the default.
- **Reading and writing.** `TenantSettingsStore` is scoped to the request. It reads all of the tenant's rows once and stages changes. The endpoint's `SaveChangesAsync` writes them; values that did not change write nothing and are not audited.
- **Endpoints.** Each settings area has its own typed endpoint, so the OpenAPI document describes it properly. A3 adds `GET` and `PUT /api/v1/settings/localization`.
  - `GET` is open to anonymous visitors, because their pages need the defaults too.
  - `PUT` needs `settings.manage`, which tenant administrators get by default (given once to existing tenants by `migrate`).

## Language, calendar and digits

| Setting | Key | Default |
| --- | --- | --- |
| Tenant default language (LANG-1) | `localization.defaultLanguage` | `en` |
| Tenant default calendar (LANG-8) | `localization.defaultCalendar` | `Gregorian` |
| Tenant default digits (LANG-8) | `localization.defaultDigits` | `Western` |

- **User choices.** Each user can choose their own language, calendar and digits: columns `users.preferred_language`, `users.calendar` and `users.digits`. `null` means "use the tenant default".
- **Endpoints.** `GET` and `PUT /api/v1/me/preferences` read and change the user's own choices (the change is audited). The response gives the choices and what applies (`effective`). `GET /api/v1/me` includes the same block.
- **Calendar and digit values.** Hijri means Umm al-Qura (`islamic-umalqura` in `Intl`). Arabic-Indic digits are `arab` in `Intl`.

## Display language order

Product owner decision, 2026-11-13:

| Who | Order |
| --- | --- |
| Signed in | Profile language → tenant default → English |
| Not signed in | Browser language, if English or Arabic → tenant default → English |
| Anyone, after using the language switch | The switched language, for this visit; remembered on the device |

How the web app does it:

- **Language in the URL.** Pages carry the language in the URL (`/en`, `/ar`). The switch links to the same page in the other language (LANG-3).
- **Device cookie.** Visiting a page in a language stores it in the `zelvo-language` cookie for a year. This is next-intl's locale cookie, renamed and made persistent.
- **Addresses without a language,** such as `/`, are redirected by `src/web/src/proxy.ts`. The order is the device cookie, then the browser language (visitors only), then the tenant default, then English. `lib/localization.ts` holds this order, with unit tests.
- **Sign-in.** At sign-in (`/auth/callback`), the user's effective language (profile, then tenant default) is written to the cookie, and the page they came from opens in that language. So the next sign-in again starts in the profile language, even if they switched language on the last visit.
- **Saving a new profile language.** The web proxy writes the cookie when the save succeeds, and the page reloads in the new language.
- **Never the other way round.** The language switch never changes the profile.
- **Tenant defaults in the web app.** They are cached for one minute. A change made through this web app clears the cache at once.

## Settings shell

- **Pages.** `/[locale]/settings` holds an overview and a menu of sections, and each section is its own page.
- **Sections.** They are listed in `src/web/src/lib/settings-sections.ts`, each with the permission it needs. Users see only the sections their permissions allow, and the header shows "Settings" when there is at least one.
- **Sections in A3:**
  - **Language and region:** `settings.manage`.
  - **Missing translations:** `translations.read`, the LANG-5 report from A1. Every Settings page also shows a warning while texts are missing a language.
- **Later modules** add their sections (forms, SLAs, workflows, branding, catalog, payments, AI) to the same list.
- **Saving.** Forms save through the web app's API proxy (`/bff/api/...`) with the session's CSRF token, the same as every browser change.

## Formal Arabic (GOV-8 base)

`docs/guidelines/arabic-formal-correspondence.md` sets the tone, structure, terms and review checklist for Arabic templates. Task B5 builds the template engine on it, and F1 adds the entity letterhead.

## Tests

- **API (`src/api/tests`):**
  - Defaults and changes, audit entries, saves that change nothing, permissions.
  - Validation, isolation between tenants, fallback for invalid stored values.
  - Preferences over tenant defaults, `null` back to the default.
  - The row-level security guard covers `tenant_settings`.
- **Web:**
  - Unit tests for the language order, Accept-Language parsing, path rewriting and the cookie name shared with next-intl.
  - Component tests for the Settings menu, the defaults form and the preferences form (preview digits, null for defaults, language change).
- **End to end (`tests/e2e/specs/settings.spec.ts`).** It runs as its own Playwright project after all others, because it changes tenant-wide defaults. It checks:
  - An admin sets the defaults, and visitors with a French browser then get Arabic, Hijri and Arabic-Indic digits, while English browsers still get English.
  - A requester's own choices win and survive the next sign-in; the language switch does not change them.
  - Settings refuses users without settings permissions, and asks visitors to sign in.
  - axe accessibility checks in both languages.

## Not in this task

- Tenant time zone (dates use Asia/Dubai).
- Template engine and letterhead (B5, F1).
- Settings sections for users and roles (B2).
