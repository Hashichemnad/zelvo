# AI features (task E5, module 9)

Covers the AI features of module 9 and LANG-7 / LANG-10: auto-categorise, prioritise and route with accept and correct; thread summaries; suggested replies in the agent's language; one-click translation; duplicate and sentiment detection; the virtual agent in the portal and chat with handover to a person; and knowledge article drafts from tickets. Every call goes through the AI gateway of task E4 (`ai-gateway.md`): the feature must be on with a provider and a model, personal data is masked, people's text is wrapped by the prompt-injection guards, and each call is in the AI log and the audit log. Nothing here works until an administrator sets the feature up in Settings → AI.

Product owner decisions (2026-11-24):

- **Triage suggests; the administrator may let it apply itself.** Agents accept or correct each suggestion. Settings → AI → Features → Triage has the rule "apply automatically to new incidents" (off by default), the approved rule module 9 allows. Agents can still correct an applied suggestion, and their corrections feed back as examples.
- **Frustrated requesters alert the group's team leads.** The incident shows a "frustrated" badge, and the new notification `incident.requester-frustrated` goes to the team leads of the incident's group (or the assignee when the group has none). Administrators switch it on or off and change its wording in Settings → Notifications; staff turn its email off in Account → Notifications.

## Code

| Piece | Where |
| --- | --- |
| Tables `ai_jobs`, `ai_triage_suggestions`, `ai_ticket_insights`, `ai_duplicate_candidates`, `ai_translations`; chat columns `chat_widgets.virtual_agent`, `chat_messages.citations`, chat state `Assistant` and sender `Assistant` (row-level security; migration `AiFeatures`) | `src/api/Zelvo.Data/Ai/AiInsightEntities.cs`, `src/api/Zelvo.Data/Channels` |
| Feature services: `TicketTriage`, `SentimentReader`, `DuplicateFinder`, `AgentAssist` (summary, reply, translation, article draft), `KnowledgeGrounding`, `VirtualAgent`; job queue `AiJobInterceptor` and worker job `AiJobRunner` | `src/api/Zelvo.Ai/Features` |
| REST API `/api/v1/incidents/{id}/ai/...`, `/api/v1/ai/translate`, `/api/v1/ai/triage/...` | `src/api/Zelvo.Api/Ai/AiFeatureEndpoints.cs` |
| Virtual agent in chat, handover, chat message translation | `src/api/Zelvo.Api/Channels/ChatEndpoints.cs` |
| Article drafts (`POST /incidents/{id}/knowledge-article?draftWithAi=true`) | `src/api/Zelvo.Api/Knowledge/KnowledgeEndpoints.cs` |
| Notification `incident.requester-frustrated` | `src/api/Zelvo.Notifications` (`SentimentNotificationSource`, `Texts/en.json`, `Texts/ar.json`) |
| Workspace AI assistant, one-click translation, composer buttons, chat window, Settings | `src/web/src/components/ai`, `src/web/src/components/workspace/Conversation.tsx`, `src/web/src/components/channels`, `src/web/src/components/settings/ai/AiFeaturesPanel.tsx` |
| Demo seeder `e5-ai` | `src/api/Zelvo.Api/Demo/DemoAiSeeder.cs` |
| Development AI mock: answers of the right shape per feature | `deploy/compose/ai-mock/server.mjs` |

## Background work: the AI job queue

Saving a ticket never waits for an AI provider. `AiJobInterceptor` runs before every save, in the API and in the worker, and queues `ai_jobs` rows in the same transaction as the change, so a job exists if and only if its change was saved:

| Change | Jobs (only for features that are on) |
| --- | --- |
| A new incident (any channel: portal, staff, email, chat, MCP) | `Triage`, `Duplicates`, `Sentiment` of the description |
| A requester's public message on an incident (portal or email) | `Sentiment` of that message |

The worker's `AiJobRunner` finds tenants with due jobs through the SECURITY DEFINER function `zelvo_ai_job_tenants()` (ids and slugs only), takes up to 20 jobs at a time with a 5-minute lease (`FOR UPDATE SKIP LOCKED`, so several workers share the queue) and runs each in its own scope with the system as actor (`ai.triage`, `ai.duplicates`, `ai.sentiment`). When the feature is off or the monthly limit is reached, the job is done without a call. A provider failure is tried again after 1, 5 and 30 minutes, four attempts in all. Finished jobs are removed after `Ai:JobRetention` (7 days) by `zelvo_purge_ai_jobs`. `Ai:JobInterval` (5 s) sets how often the worker looks.

Service requests get these features with fulfilment (task C4); E5 covers incidents, which is where the workspace shows them.

## Triage (auto-categorise, prioritise and route)

`TicketTriage.SuggestAsync` sends the incident's title and description, the tenant's selectable categories (with their path in English and Arabic) and active groups, each under a short reference such as `c3` or `g2`, and examples (below). The model answers `{category, group, impact, urgency, confidence, reason}`; unknown references and levels outside 1–3 are dropped. Zelvo stores an `ai_triage_suggestions` row (audited) and shows it in the workspace's AI assistant with the priority the tenant's matrix gives.

- **Accept** (`POST …/ai/triage/{id}/accept` without fields) applies it as suggested: status `Accepted`.
- **Correct** (the same call with the fields the agent changed) applies the agent's values: status `Corrected`.
- **Dismiss** leaves the incident as it is.

Applying goes through the same rules as any change: field rules of the agent's role (A2), only active categories and groups, a group change needs `incidents.assign` and takes off an assignee who is not a member (B2), the priority comes from the tenant's matrix, SLA clocks are updated (B3) and the search index follows. Each decision is audited (`ai.triage.accepted`, `ai.triage.corrected`, `ai.triage.dismissed`).

**Corrections improve accuracy.** Zelvo does not train models. Instead, the 15 most recent accepted or corrected suggestions are given to the next suggestion as examples: the incident's title and what it finally got, with "corrected by an agent" where the agent changed something. The model follows the tenant's own practice without any data leaving the tenant beyond the AI call. `GET /ai/triage/accuracy` (Settings → AI → Features) shows how many suggestions were accepted, corrected, dismissed and applied automatically, and how often category, priority and group were kept.

**Applying automatically** (`ai.triage.autoApply`, `PUT /ai/triage/settings`, `ai.manage`, audited as a setting): the worker applies a new suggestion at once, but only fills in what nobody chose: the category when the incident has none, impact and urgency, and the group unless someone has taken the incident. Status `AutoApplied`, audit `ai.triage.auto-applied` with the system as actor.

## Sentiment and frustrated requesters

`SentimentReader` reads the requester's newest message (or the description of a new incident) and records `positive`, `neutral`, `negative` or `frustrated` with a short reason in `ai_ticket_insights` (audited). Messages read out of order never overwrite a newer reading. When the reading turns to frustrated, `FrustratedSince` is set; when a later message is not frustrated, it is cleared. Turning frustrated is what `SentimentNotificationSource` finds in the save, and it queues `incident.requester-frustrated` for the group's team leads, or the assignee when the group has none (never the requester, never the person saving). The workspace shows the reading in the AI assistant.

## Duplicates

`DuplicateFinder` compares a new incident with open incidents of the last 14 days on Zelvo's side first: words in English and Arabic are lower-cased, diacritics and tatweel removed, alef, teh marbuta, alef maqsura and hamza forms unified, Arabic-Indic digits read as Western digits, `ال` taken off longer words and common words ignored; a word-overlap score (plus a little for the same category) picks up to five. Only that shortlist goes to the AI, which says which report the same problem (same fault, same service, same time) and how sure it is; candidates below 60 % are dropped. The agent links a candidate (the new incident becomes a child of the other one, through the same checks as "set parent", so it is resolved with it) or dismisses it. Both are audited (`ai.duplicate.linked`, `ai.duplicate.dismissed`).

## Summaries, suggested replies, translation (LANG-7, LANG-10)

| Feature | Call | Notes |
| --- | --- | --- |
| Summary | `POST /incidents/{id}/ai/summary {language?, refresh?}` (`incidents.read`) | Title, description, resolution notes and the comments (internal notes too), newest kept when the thread is long (24,000 characters). People appear as "Requester", "Agent" or "Agent (internal note)", never by name, and their names and email addresses are masked as well. At most six bullet points in the agent's language (profile, else tenant default) or the one asked for. Cached in `ai_ticket_insights` until a comment is added or the incident changes. |
| Suggested reply | `POST /incidents/{id}/ai/reply {language?}` (`incidents.edit`) | Drafted from the thread and up to three published knowledge articles the agent may read (search index with the agent's readers; without the index, a word comparison in the database), in the language the agent chose in the composer. Answers with the reply and the articles it used. Nothing is sent: the text goes into the composer for the agent to edit and send. |
| Translation of a message | `POST /incidents/{id}/ai/translation {language, commentId?}` | Title and description, or one comment. Staff translate what they see; requesters translate their own incident and its public replies (internal notes stay hidden: 404). Cached in `ai_translations` per text and language. The conversation shows a "Translate" link under messages in the other language, and the translation is marked as a machine translation under the original. |
| Translation of a draft | `POST /ai/translate {text, language, incidentId?}` (`incidents.edit` or `chat.handle`) | "Translate into Arabic/English" in the composer: the agent writes in their language and sends it in the requester's, marked with that language. Not cached. |
| Chat messages | `POST /chat/sessions/{id}/messages/{messageId}/translation` (`chat.handle`) | A "Translate" link under the visitor's messages on the Chats page. |

The instructions name Modern Standard Arabic and Gulf dialect (LANG-10) and ask for formal Arabic in replies (GOV-8).

## Virtual agent in the portal and chat

A chat widget's **"Virtual agent answers first"** switch (Settings → Live chat, `chat_widgets.virtual_agent`) together with the AI feature `virtualAgent` puts new chats in the state **`Assistant`**, also when nobody is online (`GET …/public` says `virtualAgent: true`). Each visitor message is answered before the call returns:

- `KnowledgeGrounding` finds up to three **published** articles for the person's recent words: through the search index with the caller's readers (visitors are `public` only), and **external articles only for people who are not signed in**. Hidden or internal articles are never sent to the model or cited.
- The model answers only from those articles, in the chat's language, briefly; it never claims to have done anything (it cannot change data; the full virtual agent with requests and actions is module 23, Phase 3). The answer is a chat message with sender `Assistant` and the articles it used (`citations`, linked to `/help/articles/{id}` while they stay published).
- **Handover**: when the person asks for a person, sounds frustrated or the articles do not answer, the model says so (`handover: true`); the visitor can also press **"Talk to a person"** (`POST /chat/sessions/{id}/handover`). With an agent online, the chat joins the group's queue (state `Waiting`, system message `handover`) and the agent who accepts it sees the whole conversation, the virtual agent's answers included. With nobody online, the visitor may leave the conversation as a message (`{leaveMessage: true}`): it becomes an incident (channel Chat, the widget's group and category) with the conversation as its description, and the chat ends, as for any message left while nobody is online (E2 decision).
- When the AI cannot answer at all (off, limit, provider error), the chat is handed over if someone is online, otherwise the visitor is told so and offered the message.
- **Customers' MCP servers** (task E6; decided 2026-11-24 that the virtual agent is the first feature to use them): when the tenant turned on tools in Settings → AI → MCP servers, the virtual agent runs through `IAiToolRunner` and may use the **read-only** tools to look things up (masked arguments put back on Zelvo's side, tool answers masked, every tool call audited). A tool that **changes data** is never run from the chat: the run stops for approval, and the virtual agent hands the chat to a person instead. Approving such a change inside the conversation comes with the full virtual agent (module 23).

Chat rate limits of E2 apply (20 messages a minute per chat), and every answer is an AI call in the AI log.

## Article drafts from tickets

"Turn into a knowledge article" (D1) gets a second button, **"Draft with AI"**, when the feature `articleDrafts` is on: `POST /incidents/{id}/knowledge-article?draftWithAi=true`. The AI writes title, summary and a Markdown body ("Problem", "Cause", "Solution" as steps; "المشكلة"، "السبب"، "الحل") in English and Arabic, plus keywords, from the whole thread. It is told to leave out anything about the person, and the names and email addresses of the people in the ticket are masked again in what comes back (the article is for everyone). The result is an internal **draft** linked to the incident: the four-eyes review of D1 still applies. Audited with the AI call's id.

## Security and privacy

- Every call goes through `IAiGateway`: masking, data blocks with a random marker, injection phrases flagged, JSON answers checked, AI log and audit entry (`ai-gateway.md`).
- Readers decide what grounds an answer: staff replies use the agent's search readers; the virtual agent uses the visitor's.
- The AI never changes a ticket by itself, except triage under the administrator's rule; agents' decisions go through the same handlers and checks as manual changes and are audited.
- Answers are shown as text (React escapes them); chat answers are plain text; article drafts are Markdown rendered without raw HTML (D1).
- Row-level security on the five new tables (guard test), and the worker's tenant function returns ids and slugs only.

## Settings and permissions

| What | Who | Default |
| --- | --- | --- |
| Features on, provider and model (E4) | `ai.manage` | off |
| Apply triage automatically | `ai.manage` | off |
| Virtual agent per chat widget | `channels.manage` | off |
| Notification `incident.requester-frustrated` on/off, wording | `notifications.manage` | on |
| `Ai:JobInterval`, `Ai:JobRetention` | installation operator | 5 s, 7 days |

No new permissions: the features use `incidents.read`, `incidents.edit`, `incidents.assign`, `chat.handle` and `knowledge.write`.

## Not in E5

- Service requests, complaints, inquiries and suggestions get triage, sentiment and duplicates with their tasks (C4, C6).
- Voice notes and WhatsApp messages (E3) and the full virtual agent with catalog requests and actions (module 23).
- Embeddings (pgvector) for duplicates and knowledge search: the word shortlist and the search index were enough; embeddings would need an embeddings API in the gateway for every provider kind.
