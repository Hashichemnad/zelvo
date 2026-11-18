# Search (task A4)

Covers **LANG-9** (search across English and Arabic, including Arabic word variants) and **Settings → Search** (module 10, product owner decision 2026-11-18). Code: `src/api/Zelvo.Search`; the pipeline runs in the worker, the query side in the API.

## How a change reaches the index

```
module change ──(same transaction)──▶ search_outbox ──▶ worker relay ──▶ RabbitMQ ──▶ worker indexer ──▶ OpenSearch
                                       (PostgreSQL, RLS)                  zelvo.search.index           tenant's alias
```

1. **Outbox.** A module calls `ISearchIndexer.Upsert(document)` or `Delete(type, id)`. That adds a row to `search_outbox` on the module's own `ZelvoDbContext`, saved by its `SaveChanges` in the same transaction as the change. If the change rolls back, so does the search update. `search_outbox` is tenant-owned with forced row-level security, like every tenant table.
2. **Relay** (`SearchOutboxRelay`, worker). Every second it asks `zelvo_search_outbox_tenants(stale)`, a `SECURITY DEFINER` function that returns only tenant ids and slugs, which tenants have entries to publish. Then, for each tenant, with its tenant set:
   - it locks a batch of up to 100 entries (`FOR UPDATE SKIP LOCKED`, so several workers share the work);
   - it publishes the batch as one message with a broker confirmation;
   - it marks the entries as published.

   Entries published more than 5 minutes ago and still not indexed are published again.
3. **Indexer** (`SearchIndexConsumer`, worker). It takes each message, makes sure the tenant's index exists, and writes the batch with one bulk request to the tenant's alias. Then:
   - entries OpenSearch accepted are deleted from the outbox;
   - entries OpenSearch refuses (for example a document it cannot map) are marked `failed_at` with the reason, which Settings → Search shows;
   - when OpenSearch is unavailable, the message is retried after a pause.
4. **Order and duplicates.** Every write carries the outbox id as an external version (`version_type=external`). A message delivered twice, or an older change arriving after a newer one, is refused by OpenSearch and counts as done. Deletes leave a tombstone for an hour (`index.gc_deletes`), so a late, older update cannot bring a deleted document back.
5. **RabbitMQ.** Exchange `zelvo.search` and quorum queue `zelvo.search.index`. A message that cannot be read, or that fails 10 deliveries, goes to `zelvo.search.index.dead`. Its outbox entries stay in PostgreSQL and are published again, so nothing is lost.

## Tenant isolation

- **One index per tenant.** The alias is `<prefix>-search-<tenant id>` and the physical index is `<alias>-v<schema version>`. Names are built only from the tenant id in `ITenantContext`, never from request input. `Search:IndexPrefix` (default `zelvo`) lets several installations share a cluster.
- **Tenant filter on every query.** Every document also stores `tenantId`, and every query and count filters on it as a second guard.
- **Outbox.** The outbox and `search_index_states` are under row-level security. The relay only ever reads one tenant at a time, with that tenant set.
- **Tests.** `A_tenant_never_finds_another_tenants_documents` stores the same text in two tenants and checks that neither finds the other's.

## Who finds what

- **Readers.** A document lists its readers (`SearchReaders`):
  - `public`: everyone, including visitors;
  - `users`: every signed-in user;
  - `user:<id>`: one user;
  - `role:<key>`: holders of a role;
  - `group:<id>`: members of a group (groups come with B2).

  A search only returns documents with a reader the caller is. A role counts only while it applies, so staff roles count only with a second factor.
- **Document types** are registered with `AddSearchType(type, requiredPermission)`. A search covers only registered types whose permission the caller holds. Asking for other types returns nothing rather than an error, the same way hidden records behave. This is the basis for hidden services (C2) not appearing in search.

## Arabic and English matching (LANG-9)

The analysers are defined in `SearchIndexDefinition` and use the official OpenSearch ICU plugin.

| Variant | Example | How |
| --- | --- | --- |
| Diacritics (tashkeel), shadda, tatweel | إِعَادَة = اعادة, كـــلمة = كلمة | ICU folding, Arabic normalisation |
| Alef forms | أ / إ / آ / ٱ = ا | Arabic normalisation; mapping for alef wasla |
| Teh marbuta | ة = ه | Arabic normalisation |
| Alef maqsura | ى = ي | Arabic normalisation |
| Hamza on a carrier | ؤ = و, ئ = ي | character mapping |
| Persian and Urdu letters, presentation forms | ی = ي, ک = ك, ﻻ = لا | character mapping, ICU (NFKC) |
| Arabic-Indic digits | ١٢٣ = 123 | ICU folding |
| Word forms | الطابعة = طابعه = بالطابعة | Arabic light stemmer (`zelvo_arabic`) |
| English | café = cafe, printers = printer, printer's = printer | ICU folding, light English stemmer, possessives |

**Fields**

- Bilingual `title` and `body` are indexed with the analyser of their language. Each also has a `folded` sub-field: normalised but not stemmed, so exact wording ranks higher.
- Titles have a `prefix` sub-field for search as you type.
- `text` holds text whose language is not known (for example a requester's description), and `keywords` holds an admin's search keywords. Both are analysed as Arabic and as English.

**Queries**

- A query runs on every field in its own language. Separately, it also runs across all the folded fields as if they were one field, so a query with words in both languages ("password المرور") matches.
- Results come with highlighted fragments. They are HTML-escaped, with matches in `<mark>`.

**Limits**

- In a mixed-language query, words match after normalisation but are not stemmed.
- The Arabic stemmer is a light stemmer: it removes common prefixes and suffixes and does not reduce words to their roots. Broken plurals (كتب / كتاب) do not match each other.

`SearchLanguageTests` covers every row of the table above, both at the analyser level and through real searches.

## Index versions and upgrades

`SearchIndexDefinition.SchemaVersion` changes whenever the mapping or analysers change. When the indexer first meets a tenant whose alias points at an older version, `SearchIndexManager`:

1. creates the new index;
2. refreshes the old one and copies its documents across, keeping their versions;
3. moves the alias to the new index in one step;
4. copies once more to catch writes made in between, then deletes the old index.

Searches keep working throughout. Several workers upgrading the same tenant at the same moment is not yet coordinated: in-process locking only. Multi-node installs arrive with Helm (F6), and a PostgreSQL advisory lock is planned there.

## API

| Endpoint | Who | What |
| --- | --- | --- |
| `GET /api/v1/search?q=&types=&page=&pageSize=` | anyone (visitors see public content) | Results with `type`, `id`, bilingual `title`, `url`, `updatedAt`, `score`, `attributes`, `highlights`. Paging stops at 10,000 results. OpenSearch down → 503 `search-unavailable`. |
| `GET /api/v1/search/status` | `search.manage` | Documents in the index, `pendingUpdates`, `failedUpdates`, `lastError` and when, `lastIndexedAt`, schema version, rebuild requested and completed times. |
| `POST /api/v1/search/rebuild` | `search.manage` | 202 with `documentsQueued`; audited as `search.index.rebuild-requested` (Admin). |

`search.manage` is a new permission. Tenant administrators get it by default, and existing tenants receive it once through the default-grant mechanism of A2.

**Rebuild** (`SearchRebuild`):

1. It reserves the next outbox id as its start.
2. It drops failed entries, because their records are about to be sent again.
3. Every registered `ISearchDocumentSource` streams its records into the outbox.
4. It adds a **purge** entry. When the indexer reaches it, it deletes the tenant's documents last written before the start, which are documents whose records no longer exist.

Until modules register sources (C2, D1 and later), a rebuild only purges.

## For modules (how to make a record searchable)

1. Register the type in the API: `services.AddSearchType("article", requiredPermission: null)`, or with a permission, for example `"tickets.read"`.
2. Whenever a record changes, call `ISearchIndexer.Upsert(new SearchDocument { … })` before `SaveChanges`. Call `Delete(type, id)` when the record is deleted or hidden.
3. Register an `ISearchDocumentSource` that streams every current record, for rebuilds.
4. Set `Readers` carefully. They are the only per-record access control in search.

## Operations

- **Image.** OpenSearch runs as `zelvo/opensearch` (`deploy/opensearch`): the official image plus the official `analysis-icu` plugin. For offline or mirrored builds, put the plugin zip at `deploy/opensearch/plugins/analysis-icu.zip`. CI builds the image, scans it and uses it for the integration tests.
- **Worker configuration.** The worker now needs `ConnectionStrings:Postgres` (the application role), `ConnectionStrings:RabbitMq` and `Services:OpenSearch`.
- **Tuning.** `Search:Replicas` sets index replicas (0 on a single node). `Search:Pipeline:*` sets the poll interval, republish delay, batch size, retry delay and prefetch.
- **Security (A5).** OpenSearch runs with its security plugin (HTTPS, TLS 1.3): the API signs in as `zelvo-api` (reads) and the worker as `zelvo-worker` (writes `zelvo-*` indices only), set with `Search:Username`, `Search:Password` and `Search:CaFile`. RabbitMQ is reached with `amqps://` in the `zelvo` virtual host (`RabbitMq:CaFile`). Alias lookups ask only the tenant's own indices (`<alias>-*/_alias/<alias>`), so a limited user may make them. See [security-baseline.md](security-baseline.md).
- **Metrics (A5).** `zelvo_search_outbox_published_total`, `zelvo_search_indexed_total`, `zelvo_search_refused_total`, `zelvo_search_retried_total` and a `search.index` span per message; see [observability.md](observability.md).
- **Not in A4.** The search screens for users come with the portal (D2).
