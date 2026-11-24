# Knowledge base (task D1, module 7)

Covers module 7:

- articles in English and Arabic with draft, review, published and retired states;
- internal (agents only) and external (portal) visibility;
- "Was this helpful?" feedback and view counts;
- "turn this ticket into an article".

Also LANG-5 (articles hold both languages; a missing one is reported).

AI-drafted articles come in E5. The portal's search-first home (D2) reuses the reader pages built here.

Product owner decisions (2026-11-24):

- **Four-eyes rule.** A publisher other than the author approves an article. The reviewer may not have created or last changed the version in review. Team leads and administrators can publish by default.
- **One language is enough to publish.** The missing language is listed in Settings → Missing translations, and readers of that language see the article in the language it exists in, with a short note.
- **Visitors can read external articles** when the tenant setting "Visitors who are not signed in can read external articles" is on. It is on by default, like a public help centre. When it is off, people sign in to read them. Internal articles are always for staff only.
- **Content is Markdown with images.** Headings, lists, links, tables and code, with a live preview. Images are uploaded files that are type-checked and scanned for malware, as B1 attachments are. Raw HTML is never rendered.

## Code

| Piece | Where |
| --- | --- |
| Tables `kb_articles`, `kb_article_versions`, `kb_feedback`, `kb_article_views` (row-level security; migration `Knowledge`); article images in `ticket_attachments` (`article_id`) | `src/api/Zelvo.Data/Knowledge` |
| Rules, search documents, missing translations, settings | `src/api/Zelvo.Api/Knowledge/KnowledgeCore.cs` |
| Staff API (articles, review, history, feedback, ticket → article) | `src/api/Zelvo.Api/Knowledge/KnowledgeEndpoints.cs` |
| Reading API (published articles, views, feedback, images, Settings → Knowledge) | `src/api/Zelvo.Api/Knowledge/KnowledgePortalEndpoints.cs` |
| Demo seeder `d1-knowledge` | `src/api/Zelvo.Api/Demo/DemoKnowledgeSeeder.cs` |
| Staff pages `/knowledge`, `/knowledge/new`, `/knowledge/review`, `/knowledge/[id]`; reader pages `/help`, `/help/articles/[id]`; Settings → Knowledge | `src/web/src/app/[locale]/…`, `src/web/src/components/knowledge` |

## Articles and versions

An article has a number (`KB-000123`), a state, a visibility (Internal or External) and an optional category from the B2 category tree. Its content lives in **versions**. A version holds a bilingual title, summary and Markdown body, keywords, the visibility and category it asks for, and a change note.

- **Draft → In review → Published.** A new article is a draft (version 1). *Send for review* moves the working version to In review; while it is in review, nobody can change it. A publisher *approves* it, which publishes it, or *sends it back* with a comment, which is required and returns it to Draft.
- **Changes to a published article** start a new working version (version 2, …). Readers keep seeing the published version until the new one is approved. When it is approved, the old version becomes *Superseded* and stays as history.
- **Retire**: the article is no longer read or found, and its last version is marked Retired. *Reopen as draft* makes a new draft from it, which is published again only through review.
- **Discard** removes the working version. An article that was never published is deleted with it.
- Numbers are taken under a per-tenant transaction lock (`pg_advisory_xact_lock`). Published articles are retired, never deleted, so a number that readers have seen is never reused. The prefix is a tenant setting (`knowledge.numberPrefix`, default `KB`).
- **Concurrency.** The ETag of an article is the version of its working version, or of its published version when there is no working version; a sent If-Match is checked against that. The article row has no concurrency token, because view and feedback counters change it all the time.
- **Audit.** Every change to articles, versions and feedback is audited automatically. The body is long, so the new attribute `[AuditLengthOnly]` records only that it changed and its length; the full text is kept in the versions. Submitting, publishing, sending back, retiring, reopening, deleting and discarding also write named entries (`knowledge.article.published`, …). A feedback comment is marked as sensitive and masked in the audit log.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `knowledge.read` | Read internal articles, the staff list and drafts, and feedback | Agent, team lead, administrator, executive |
| `knowledge.write` | Write and change drafts, add images, send for review, reopen, discard your own drafts, turn an incident into an article | Agent, team lead, administrator |
| `knowledge.publish` | Approve or send back others' versions, retire, discard any draft | Team lead, administrator |

External published articles need no permission. Writers who lack `knowledge.read` see only their own articles and published external ones. Integrations can hold all three permissions. They create versions without a person behind them, so a person always reviews those versions.

## Reading, views and feedback

- `GET /api/v1/knowledge/published` and `/published/{id}` return published articles the caller may read: external ones for signed-in users and, when the setting allows it, for visitors; internal ones too for staff. When visitors may not read the knowledge base, the API answers `401 sign-in-required`.
- **Views** (`POST …/views`) count once per viewer per day (UTC). The viewer is a signed-in user, or a visitor's device: a random id the browser keeps in local storage and sends as `visitorId`. Only a SHA-256 hash of the tenant and the id is stored (`kb_article_views`). Counts are increased in the database, so views at the same moment all count. Views are not audited.
- **"Was this helpful?"** (`PUT …/feedback`) allows one answer per user or per device, which people can change. An optional comment can be added. Totals are counted again in the database after each answer. Staff with `knowledge.read` see the answers (`GET /knowledge/articles/{id}/feedback`).
- The web app's API proxy lets visitors through only to these paths (`src/web/src/lib/visitor-paths.ts`): reading, images, views, feedback and the knowledge settings. Changes must come from the site's own pages (Origin check).

## Search (A4)

Articles are the `article` document type, with title and body analysed as Arabic and English. Readers:

| Article | Readers |
| --- | --- |
| Published, External | `public` (or `users` when visitors may not read) |
| Published, Internal | roles with `knowledge.read` or `knowledge.publish` |
| Not yet published (Draft or In review) | roles with `knowledge.write` or `knowledge.publish`, and the author |
| Retired | removed from the index |

When an article is published, only its published version is indexed; pending changes are not searchable. Changing the public setting re-indexes external articles. A rebuild reads every article through `KnowledgeSearchSource`.

## Turn this ticket into an article

`POST /api/v1/incidents/{id}/knowledge-article` works on resolved or closed incidents. It makes an internal draft from the title, the description (under *Problem*) and the resolution notes (under *Solution*), in Arabic or English depending on the text, with the incident's category. The draft is linked to the incident (`source_ticket_id`), and `GET /incidents/{id}/knowledge-articles` lists articles made from it.

Personal data is replaced by placeholders such as `[PERSON_1]` and `[EMAIL_1]` (E4's `PersonalDataPseudonymizer`), and is never put back. This covers the requester's and assignee's names and email addresses, plus phone numbers, Emirates IDs and IBANs. The article is for everyone; the ticket was about one person.

## Markdown and images

- **Rendering** (`MarkdownView`) uses `react-markdown` with `remark-gfm` (both MIT): raw HTML is skipped, links keep only safe protocols, and `img` sources other than the article's own images are dropped. Code and tables are shown left to right in both languages, and each language's text is shown in its own direction.
- **Images** are uploaded with `POST /knowledge/articles/{id}/images`: PNG, JPEG, GIF or WebP up to the tenant's attachment size. The content must match the type, and the image is scanned by ClamAV before anyone can see it. The scan job now names an article's images in its audit entry (`knowledge.image.infected`). Markdown refers to an image as `![text](zelvo-image:<id>)`. The image is served to whoever may read the article, with `nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`.

## Demo

Seeder `d1-knowledge` (order 200) creates articles for both sample organisations in their languages: published external and internal articles with views and feedback, one waiting for review, one draft, and a draft made from a resolved sample incident.

## Not in D1

- AI article drafts (E5).
- The portal's search-first home across catalog, knowledge and tickets (D2).
- Article attachments other than images.
- Scheduled review dates.
- Numbered-list markers follow the browser's digits, not the user's choice of Arabic-Indic digits.
