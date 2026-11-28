# Demo walkthrough (tasks F7 and L1)

A script for showing Zelvo to a prospective customer with the demo installation (`docs/architecture/demo-mode.md`). It covers every Phase 1 module (1–15 in `docs/requirements.md`), the Phase 2 modules (16–21, task L1), demo mode and the UAE government requirements. It runs in **both languages**: the government entity in **Arabic** (right to left, its default language) and the private company in **English**. Each module has an Arabic talking point in formal Arabic (per `docs/guidelines/arabic-formal-correspondence.md`) for Arabic-speaking audiences.

- **Full walkthrough:** about 140 minutes (all sections): about 90 for Phase 1 and 50 for Phase 2. For a long session, split it in two meetings at the Phase 2 heading.
- **Short walkthrough:** about 30 minutes. Follow the steps marked ★ only: the Phase 1 highlights, then the Phase 2 highlights (the CMDB's impact map, a problem with its known error, a change through the advisory board with its what-if simulation).
- Everything shown is fictional sample data. Emails go to Mailpit, payments to a sandbox, and no real AI provider, mail server, WhatsApp number or directory is reached.

## Video of the walkthrough

The GitHub Actions workflow **Demo video** (Actions → Demo video → Run workflow; `.github/workflows/demo-video.yml`) records a video of this walkthrough for prospects who cannot attend. It starts the demo installation, runs `tests/e2e/video/demo-video.spec.ts` (58 steps across every Phase 1 and Phase 2 module, each persona signed in with the demo sign-in, a caption naming each step), first in English and then in Arabic, at 1920×1080. It then joins the two clips into one MP4, `zelvo-demo-walkthrough`, kept as an artifact for 7 days. The inputs set how long each step stays on screen (`hold_ms`, 4500 by default) and how much every action is slowed down (`slow_mo_ms`, 250). The video shows pages and a few clicks; the live presentation below goes further (approving, paying, chatting). Two clicks change the sample data, as the live walkthrough does: the English run submits the high-risk sample change (section 17) and the board's chair saves a vote on it, so the Arabic run finds it on the board's agenda. The video has no sound.

To record it on your own computer against `make demo`:

```bash
cd tests/e2e && npx playwright install chromium ffmpeg && E2E_VIDEO=1 npx playwright test
```

The clips are written to `tests/e2e/demo-video/` (git-ignored). Reset the demo afterwards (Before the meeting, step 2), because the recording submitted a change.

## Before the meeting

1. On the demo computer (development tools as in `README.md`), with the development stack stopped:

   ```bash
   make demo
   ```

2. Open `http://localhost:3000/ar`. In the demo panel under the header (**العرض التوضيحي**), sign in as the administrator (`admin@municipality.example`) and press **إعادة ضبط العرض التوضيحي** (Reset demo), then **إعادة الضبط الآن**. The reset runs as a background job and restores the clean sample state of both organisations; it takes about a minute. The demo panel shows a progress bar (**مدى تقدّم إعادة الضبط**) with the percentage and the step (for example «حذف التغييرات التي أُجريت في العرض التوضيحي», then «إضافة البيانات النموذجية»), and reloads the page when it ends. Until then the other pages of the sample organisations are unavailable, in every tab and for every person, so start it at least five minutes before the meeting. When it ends, the administrator gets a notification (in the app and in Mailpit) that it finished or failed.
3. Open three browser tabs: the demo (`http://localhost:3000/ar`), Mailpit (`http://localhost:8025`, the demo's outgoing email) and a spare tab for the API explorer (`http://localhost:8080/api-explorer`).
4. Check the screen at 100 % zoom and turn off notifications of other apps. For a phone demonstration, open the portal on a phone on the same network, or use the browser's device view.
5. Optional, for the AI section: as the administrator, Settings → AI → Providers, add an "OpenAI-compatible" provider with the address `http://ai-mock:8097/v1` and any key. Then turn on the features in Settings → AI → Features, including Phase 2's **Rule builder** (section 20), **Problem detection** (section 16) and **Change what-if explanation** (section 17). The development mock gives fixed answers of the right shape; say so if asked. Without it, the AI assistant still shows the seeded suggestions, and the Phase 2 sections show their seeded samples.

**Switching people and organisations.** The demo panel lists the six demo users of the current organisation and the other organisation. One click signs in as that person, with the second factor counted. The language switch in the header changes the language for this visit only.

| Persona | Government entity (Arabic) | Company (English) |
| --- | --- | --- |
| Requester | `requester@municipality.example` | `requester@gulfhorizon.example` |
| Agent | `agent@municipality.example` | `agent@gulfhorizon.example` |
| Team lead | `lead@municipality.example` | `lead@gulfhorizon.example` |
| Approver | `approver@municipality.example` | `approver@gulfhorizon.example` |
| Tenant administrator | `admin@municipality.example` | `admin@gulfhorizon.example` |
| Executive | `executive@municipality.example` | `executive@gulfhorizon.example` |

## Opening (2 minutes) ★

**Show:** the government entity's portal home in Arabic, then the company's in English.

1. Signed in as the government requester, point at the product name **الخدمات الإلكترونية للبلدية** and the green brand colours: this entity runs Zelvo under its own name. The **بتقنية زيلفو** ("Powered by Zelvo") footer always stays.
2. Open the demo panel and switch to the other organisation (**فتح جهة نموذجية أخرى**). The company appears as **Company IT Help**, purple, in English, left to right.

**Say:** "One product, installed on your own servers, in Arabic and English, under your name. It is bought once, with no subscription and no data leaving your network."

**قولوا:** «منصة واحدة تُثبَّت على خوادمكم، باللغتين العربية والإنجليزية، وتحمل اسم جهتكم. تُشترى مرة واحدة دون اشتراك، ولا تغادر بياناتكم شبكتكم.»

## 4. Self-service portal ★

**Who:** government requester, Arabic.

1. The home page starts with search (**البحث في الخدمات ومقالات المساعدة وطلباتكم**). Type a few letters of a service or problem (for example «رخصة»). Results appear as you type, grouped into services, help articles and the person's own tickets.
2. Point at the three shortcuts under **تقديم طلب** (Raise a ticket): الإبلاغ عن مشكلة، طلب خدمة، تواصل معنا (report a problem, request a service, contact us). Any ticket is two clicks away.
3. Show the person's open tickets with their progress bars, the popular services and today's date in both the Gregorian and Hijri calendars.
4. Open **طلباتي** (My tickets): every ticket of every kind in one list, with progress and the **team** that has it. Requesters never see the agent's name.
5. Narrow the window to phone width: the portal adapts and stays right to left.

**Say:** "Citizens and employees search first and find the answer or the right service. If they need a person, they raise a ticket in two clicks and follow it in one place."

**قولوا:** «يبدأ المتعامل بالبحث، فيجد الإجابة أو الخدمة المناسبة. وإن احتاج إلى موظف، يقدّم طلبه بنقرتين ويتابعه من مكان واحد.»

## 1. Incident management ★

**Who:** government requester, then the government agent, Arabic.

1. As the requester, **الإبلاغ عن مشكلة** (Report a problem): the form is the category's own form (C1). Submit it; it gets a number such as `INC-000007` and is routed to a group at once.
2. Switch to the agent. Open **البلاغات** (Incidents) and the major incident **انقطاع بوابة الخدمات الإلكترونية** (the e-services portal outage), with its two child incidents.
3. Show the priority (impact × urgency → P1–P5), the major-incident flag and its communication timeline, which requesters of the child incidents also see.
4. Open the history: every change, field by field, with who and when.
5. Show the states: put an incident on hold with a reason, resolve it with notes. The requester can reopen it until it closes automatically after the tenant's period.

**Say:** "Incidents follow ITIL: priority from impact and urgency, major incidents with their own communication, parent and child tickets, and a full history of every change."

**قولوا:** «تُدار البلاغات وفق أفضل الممارسات: الأولوية من الأثر والإلحاح، وللحوادث الكبرى مسار تواصل خاص، مع ربط البلاغات الفرعية وسجل كامل لكل تعديل.»

## 5. Agent workspace ★

**Who:** company agent, English (switch organisation in the demo panel).

1. Open **Workspace**. The incident list, the open incident and the customer are on one screen. Moving between incidents does not reload the page.
2. Open **Email delivery delayed for all staff** (the company's major incident). Show the facts, the live SLA timers, the conversation with the requester's message, the public reply and the internal note that mentions the team lead.
3. Write a public reply with a **canned response** (placeholders filled in), then an internal note with an @mention.
4. Press `?` to show the keyboard shortcuts (`j`/`k` to move, `x` to select, `r` to reply, `/` to search).
5. Select two incidents with `x` and show a bulk action (for example, set the urgency).
6. Show the saved view the team lead shares with the service desk.

**Say:** "Everything an agent needs is on one screen, updated live. Canned answers, shared views and bulk actions save time on every ticket."

**قولوا:** «كل ما يحتاجه موظف الدعم في شاشة واحدة تُحدَّث لحظيًا، مع ردود جاهزة وطرق عرض مشتركة وإجراءات جماعية توفّر الوقت في كل طلب.»

## 8. SLA management

**Who:** company agent, then the company administrator.

1. In the workspace, point at the two clocks on an incident: response and resolution, counting business time only.
2. Filter the list to **at risk** and **breached**, sorted by the soonest due.
3. As the administrator, open Settings → **SLA policies**: the VIP and standard policies with targets per priority and escalations at 75 % and 100 %. Use the policy tester.
4. Open Settings → **Business calendars**: the company's office hours and 24×7 calendar. Switch to the government entity in Arabic: **تقويمات العمل** shows the UAE government week (Monday to Thursday 07:30–15:30, Friday until 12:00) and UAE public holidays.

**Say:** "Service levels count your working hours and your public holidays. Teams are alerted before a target is missed, not after."

**قولوا:** «تُحتسب مستويات الخدمة وفق ساعات العمل الرسمية والعطل الرسمية في الدولة، ويُنبَّه الفريق قبل تجاوز الهدف لا بعده.»

## 2. Service requests and catalog ★

**Who:** government requester, the team lead and the approver, Arabic.

1. As the requester, open **الخدمات** (Services). Show the catalog: a main service with sub-services (IT services), a public service with a fee and required documents (**حجز اسم تجاري**, trade name reservation, 200 AED), an external service (residence visa) and the suspended **شاشة إضافية** (extra monitor) with its message.
2. Open **حاسوب محمول جديد** (new laptop): the service card with description, steps, documents, working days, FAQ and related services.
3. Request it. Tick **إضافة شاشة** (add a monitor): the **حجم الشاشة** (monitor size) field appears and becomes required. This is a form rule (C1).
4. The request waits for approval: switch to the team lead (the requester's manager), open **اعتماداتي** (My approvals) and approve. Then switch to the approver and approve the second level. Show the approval email with its Approve and Reject buttons in Mailpit.
5. Show **New employee onboarding** (C4), switching to the company in English: its tasks (laptop and network account in parallel, then the hand-over) and the requester's journey moving as the tasks are done. As the agent, open **My tasks**.
6. Show the bundle **New joiner package** (باقة الموظف الجديد): one request with a child request per service, later parts held until the earlier ones finish, one tracking screen. On the requester's My tickets, **Services for you** shows the proactive offer of the package, with the form filled in.
7. As the administrator, open Settings → **Catalog** and a service in the editor: versions, four-eyes review (a draft and one waiting for review), access per service, templates, fulfilment and the proactive section.

**Say:** "Your service catalog is built on screen, without code: forms, documents, fees, approvals, tasks and service levels per service. Packages and proactive services go to people before they ask."

**قولوا:** «يُبنى دليل الخدمات على الشاشة دون برمجة: النماذج والمستندات والرسوم والاعتمادات والمهام ومستويات الخدمة لكل خدمة، مع باقات وخدمات استباقية تصل إلى المتعامل قبل أن يطلبها.»

## 15. Online payments

**Who:** government requester, then the executive, Arabic.

1. As the requester, request **حجز اسم تجاري** (trade name reservation). The fee panel shows 200 AED due on submission; the request waits in Awaiting payment.
2. Press **السداد الآن** (Pay now). The sandbox gateway page opens (no card details, no real money). Choose **Pay**.
3. Back in Zelvo, the payment is confirmed with the gateway (never from the browser alone), a receipt number `RCT-…` is issued and the request moves on. Open the bilingual e-receipt and its email in Mailpit.
4. Switch to the executive and open **المدفوعات** (Payments): the period's payments, Excel export and the daily reconciliation per gateway.
5. Mention that the customer's own gateway (AjmanPay, or any other) is connected in Settings → Payments from the gateway's documentation, without code.

**Say:** "Fees are paid online through your own payment gateway. Every payment is confirmed with the gateway, receipted in both languages and reconciled every day."

**قولوا:** «تُسدَّد الرسوم إلكترونيًا عبر بوابة الدفع الخاصة بجهتكم، ويُتحقَّق من كل عملية لدى البوابة، ويصدر إيصال باللغتين، وتُطابَق المدفوعات يوميًا.»

## 3. Customer voice ★

**Who:** a visitor (signed out), the government requester and the executive, Arabic.

1. Sign out (or open a private window at `http://localhost:3000/ar`). Open **تواصل معنا** (Contact us) and send a **شكوى** (complaint) **without signing in**: no email or personal details are asked. Show the reference number and the private access code shown once, then **متابعة رسالة دون تسجيل الدخول** (follow an anonymous message).
2. As the requester, open **رسائلي** (My messages): an inquiry with knowledge suggestions, a complaint waiting for their confirmation, a suggestion with its review.
3. Show the **happiness meter** (three faces) on a resolved ticket.
4. As the executive, open **تقرير صوت المتعامل** (Customer voice report): six months of complaints, inquiries and suggestions per type, channel and service; complaints by severity and root cause; happiness that improves month by month; CSV export.

**Say:** "Complaints, inquiries and suggestions have their own rules: root cause, the customer's confirmation and anonymous submission. The happiness meter and its report follow your customer happiness charter."

**قولوا:** «للشكاوى والاستفسارات والاقتراحات مسارها الخاص: تحديد السبب الجذري، وتأكيد المتعامل، وإمكانية التقديم دون الإفصاح عن الهوية. ويتوافق مقياس السعادة وتقاريره مع ميثاق إسعاد المتعاملين لدى جهتكم.»

## 7. Knowledge base

**Who:** government agent and team lead, Arabic; then a visitor.

1. As the agent, open **المعرفة** (Knowledge): published external and internal articles in both languages, with views and "Was this helpful?" feedback.
2. Show the article made from a resolved incident (personal data replaced) and the draft.
3. As the team lead, open **قائمة المراجعة** (Review queue) and publish the article waiting for review. The author cannot approve their own article (four-eyes rule).
4. Signed out, open **المساعدة** (Help): visitors read external articles only.

**Say:** "Answers that worked become articles, reviewed by a second person before anyone reads them, in Arabic and English."

**قولوا:** «تتحول الحلول الناجحة إلى مقالات معرفية يراجعها شخص آخر قبل نشرها، باللغتين العربية والإنجليزية.»

## 6. Omnichannel intake

**Who:** company agent and administrator, English.

1. In the workspace, open the incident that came in **by email**: the conversation shows each message's source, and replies go back in the same email thread.
2. Open the incident that came in **by WhatsApp**: the requester's message, a **voice note** with its transcript, and the agent's reply that went back on WhatsApp.
3. Open **Chats**: the ended chat and the chat widget. On the portal, press **Chat with us** as the requester (**تحدّث معنا** in Arabic). As the agent, accept the chat, answer, then make a ticket from it.
4. As the administrator, show Settings → **Mailboxes** (Microsoft 365, Gmail or IMAP), **Live chat** (widgets and their embed code), **Messaging** (WhatsApp, SMS and Microsoft Teams, turned off in the demo) and **Monitoring** (an alert from Prometheus that raised a P1 incident).

**Say:** "Email, live chat, WhatsApp, SMS, Teams and monitoring alerts all land in the same ticket conversation, and answers go back the way the customer wrote."

**قولوا:** «تصل الرسائل من البريد الإلكتروني والمحادثة المباشرة وواتساب والرسائل النصية وتيمز وتنبيهات المراقبة إلى محادثة الطلب نفسها، ويُرَدّ على المتعامل عبر القناة التي تواصل منها.»

## 9. AI capabilities ★

**Who:** company agent, then the company administrator, English.

1. In the workspace, open the incident with an **AI suggestion**: the AI assistant suggests category, priority and group. Accept or correct it; corrections teach later suggestions.
2. Open the incident with the **frustrated requester**: the sentiment reading, and the alert the team leads received.
3. Open the **possible duplicate** and link it as a child, or dismiss it.
4. If the mock provider was added (Before the meeting, step 5): summarise the thread, suggest a reply from published articles, translate a message to Arabic with one click, and draft an article.
5. As the administrator, open Settings → **AI**: providers (the customer's own Claude, OpenAI, Azure OpenAI, Gemini or a self-hosted model and key), the provider and model per feature, the monthly spending limit, usage and cost, and the AI log with masked prompts. Open **MCP server** and **AI approvals**: the customer's own AI agents use Zelvo through the built-in MCP server, and every change they propose waits for a person.

**Say:** "AI is included, with your own provider and key. Personal data is masked before it leaves Zelvo, every call is logged, and the AI never changes anything without a person's approval."

**قولوا:** «الذكاء الاصطناعي مُضمَّن، ويعمل عبر المزوّد ومفتاح الاستخدام الخاصّين بجهتكم. تُحجب البيانات الشخصية قبل إرسالها، وتُسجَّل كل عملية، ولا يُنفِّذ الذكاء الاصطناعي أي تغيير دون موافقة موظف.»

## 10. Notifications, reporting and administration ★

**Who:** government team lead and executive, Arabic; then the administrator.

1. Point at the bell in the header (**الإشعارات**): in-app notifications in the person's language. Open Mailpit: the Arabic emails are right to left, in formal Arabic, with the entity's letterhead.
2. As the executive, open **التقارير** (Reports): the overview and the fixed dashboards (volumes, backlog, SLA compliance, agent performance, customer happiness) with six months of trends. Filter by group and period. Export to Excel and to a branded PDF in Arabic.
3. Show **التقارير المجدولة** (Scheduled reports): the weekly overview emailed to the executive.
4. As the team lead, the same dashboards show only the groups they lead. As the agent, **أدائي** (My performance) shows only their own figures.
5. As the administrator, open the **الإعدادات** (Settings) menu: users, roles with field-level rules, groups, categories, locations, assignment rules (with "Try the rules"), forms and the form builder (drag and drop, desktop and mobile preview in both languages), workflow rules, notification wording side by side in English and Arabic.
6. Open **معالج الإعداد** (Setup wizard): the steps a new customer follows, and the four department templates (IT service desk, human resources, facilities, customer happiness centre) that set up a department in one step.

**Say:** "Managers see live dashboards and receive reports by email. Administrators set up everything on screen, from forms and workflows to the wording of every notification."

**قولوا:** «يتابع المديرون لوحات معلومات محدَّثة ويتسلّمون التقارير عبر البريد الإلكتروني، ويُهيّئ المسؤولون كل شيء من الشاشة، من النماذج وسير العمل إلى صياغة كل إشعار.»

## 11. White-label branding

**Who:** government administrator, Arabic.

1. Open Settings → **الهوية المؤسسية** (Branding): product name in both languages, colours for light and dark mode, the font, and the live preview in both languages.
2. Change a colour to a pale one: the contrast check refuses to publish it (WCAG 2.2 AA). Discard the change.
3. Show the logos, the custom domains (a DNS record to verify, then an automatic certificate) and the email sender domain with its SPF, DKIM and DMARC check.
4. On an incident, **تنزيل ملف PDF** (Download PDF): a branded export in Arabic.

**Say:** "Your name, your colours, your domain and your email address, with accessibility checked before anything is published."

**قولوا:** «اسم جهتكم وألوانها ونطاقها وعنوان بريدها، مع التحقق من سهولة الوصول قبل نشر أي تغيير.»

## 14. Integrations and developer tools

**Who:** company administrator, English.

1. Open Settings → **Integrations**: create an API key acting as a role, narrowed by scopes, with its own rate limit and expiry. It is shown once.
2. In the spare tab, open the API explorer (`http://localhost:8080/api-explorer`), enter the key, add the header `X-Zelvo-Tenant: demo-company` (the API's default tenant in the demo is the government entity) and call `GET /api/v1/incidents`. The UI uses the same public API.
3. Show outgoing **webhooks** (signed, retried, with a delivery log and a tester), **Import and export** of users and service cards, and **Connectors**: Entra ID or Active Directory sync and sign-in, the CRMs, the REST connector builder and the workflow rule that calls it.

**Say:** "Everything on screen is also in the documented REST API, with signed webhooks and ready connectors to your directory and CRM."

**قولوا:** «كل ما يظهر على الشاشة متاح أيضًا عبر واجهة برمجة موثّقة، مع إشعارات آلية موقّعة وموصلات جاهزة لدليل المستخدمين ونظام إدارة علاقات المتعاملين لديكم.»

## 12. Licensing and 13. Offline update packages

**Who:** government administrator, Arabic (the operator tenant of the demo).

1. Open Settings → **الترخيص** (License): the license kind (Demo), the licensed modules and versions, this installation's fingerprint and **سلامة الإصدار** (release integrity).
2. Explain activation: the installation downloads an activation request, Zelvo returns a signed license file, and the customer imports it. Everything works offline, with no license server.
3. Open Settings → **التحديثات** (Updates): signed update packages are uploaded or imported from a USB drive, checked, then installed now or in a maintenance window, with a backup and automatic rollback. Security updates are free; feature updates need an upgrade license.

**Say:** "Zelvo runs fully offline. Licenses and updates are signed files you import yourself, and every update can roll back on its own."

**قولوا:** «يعمل زيلفو دون اتصال بالإنترنت؛ فالتراخيص والتحديثات ملفات موقّعة تستوردونها بأنفسكم، ويمكن لأي تحديث أن يتراجع تلقائيًا عند حدوث خلل.»

## Phase 2: Control (modules 16–21)

The sections below show the Phase 2 modules with the same sample organisations: the CMDB and assets (18), problem management (16), change management (17), the installable app, passkeys and the Teams app (19), plain-language rules, custom record types and the app builder (20), and agent wellbeing (21). They take about 50 minutes in all; the ★ sections add about ten minutes to the short walkthrough. Some features use AI that is off in the demo (plain-language rules, problem detection's comparison of similar incidents, the what-if explanation): the seeded samples show their results, and the mock provider (Before the meeting, step 5) lets you try them live.

## 18. CMDB and the relationship map ★

**Who:** government agent, Arabic.

1. In the header, open **قاعدة بيانات إدارة التهيئة** (CMDB). The list shows the entity's business services, applications, servers, databases and network devices, each numbered `CI-…`. Item names are the same in both languages (they are technical names such as `srv-web-01`); descriptions are bilingual.
2. Type `srv-web-01` in **الرقم أو الاسم** (Number or name) and press **عرض** (Show). Open the server: its class, status, owner group and attributes (IP address `10.10.1.11`, Ubuntu 24.04, 8 cores, 32 GB).
3. Point at **التأثير** (Impact): a fault of this server affects the **E-services website** and the **E-services portal** business service, and the people who use it (the customer happiness department and the VIP customer group). The retired server `srv-web-legacy` is never counted.
4. Scroll to **خريطة العلاقات** (Relationship map): choose how far to look (1–4 steps) and filter by relationship type and class. Click the map and move with the arrow keys; the selected item is announced. Switch to **القائمة** (List): the same relationships as text, the equal alternative for screen readers.
5. Open the major incident **انقطاع بوابة الخدمات الإلكترونية**: the configuration items it is about appear in its panel, and the same items link problems and changes later in this walkthrough.

**Say:** "Your servers, applications and services are mapped with how they depend on each other, so any fault or change shows at once which services, departments and customers it touches."

**قولوا:** «تجمع قاعدة بيانات إدارة التهيئة خوادمكم وتطبيقاتكم وخدمات الأعمال وعلاقاتها، فيتبيّن عند أي عطل أو تغيير ما يتأثر به من خدمات وإدارات ومتعاملين.»

## 18. Discovery and the review queue

**Who:** government administrator, Arabic.

1. Open Settings → **قاعدة بيانات إدارة التهيئة** → **الاكتشاف** (Discovery). Two sources: **Network scanner**, the entity's own tool pushing records through the discovery API, and **Microsoft Intune** on the E7 connector (turned off in the demo, like the connectors). Explain the other sources: Entra ID devices, Azure, Active Directory computers and VMware vCenter; AWS is planned when a customer asks.
2. Explain reconciliation: each source's records are matched to existing items by identifying values (serial number, host name, MAC address), and each attribute keeps the value of the source it trusts most. What people enter on screen wins by default.
3. Open **قائمة المراجعة** (Review queue, `/cmdb/review`): a record that matches two items (`srv-app-01` and the older entry `srv-app-01 (asset list 2023)`) and a possible duplicate (**Web server 2** shares its serial number with `srv-web-02`). Show **الدمج مع الإبقاء على …** (Merge, keeping …) and **هما عنصران مختلفان** (They are different). Do not decide in a short demo, so the next audience sees the same queue.
4. Back in the list, tick **العناصر التي لم يعد أي مصدر يُبلغ عنها فقط** (Only items no source reports any more): the second-floor printer the scanner stopped reporting is marked, never deleted.
5. Open `srv-web-02`: the item's discovery panel shows which source set each value and the installed software it reported.

**Say:** "The CMDB keeps itself up to date from the tools you already run. Zelvo never merges or deletes anything it is unsure about; a person decides."

**قولوا:** «تُحدَّث قاعدة البيانات تلقائيًا من الأنظمة القائمة لديكم، مثل Intune وActive Directory وvCenter، ولا تُدمج العناصر المكررة أو المطابقات غير المؤكدة إلا بقرار من المسؤول.»

## 18. Assets, warranties and licences

**Who:** company requester, then the company agent, English.

1. As the requester, open **My assets**: the laptop **LT-0042** assigned to them, with its tag `AST-…` and the **Laptop warranty, 1 year** that ends within the alert period. The same appears under **My tickets**.
2. Switch to the agent and open **Assets**: LT-0042 assigned, LT-0051 **in repair** and LT-0052 **in stock**, each a hardware configuration item with its lifecycle (requested → ordered → received → in stock → assigned → in repair → retired → disposed).
3. Open **Contracts and warranties**: the warranty ending soon, the 3-year warranty and the **Cloud support plan**. Expiry alerts reach the owner and asset managers in the app and by email before the end date.
4. Open **Software licences**: **Microsoft 365 Apps for enterprise** is licensed per device for 2 devices, but discovery found it on more laptops, so the compliance column shows the shortfall. **Fictional CRM Desktop** is licensed per person.
5. Mention the label sheets (PDF with a QR code and Code 128 per tag) and **Scan a label**: a photo of the label from the phone's camera opens the asset, and typing the tag is the equal alternative. In the workspace, the customer panel of the requester's incidents shows their assets and warranties.

**Say:** "Every laptop, contract and licence is tracked from purchase to disposal. You are warned before warranties end, and you see a licence shortfall before an audit does."

**قولوا:** «تُتابَع الأصول من الطلب إلى الإتلاف، مع الضمانات والعقود وتراخيص البرمجيات، وتصل التنبيهات قبل انتهائها، ويتبيّن النقص في التراخيص قبل أي تدقيق.»

## 16. Problem management ★

**Who:** government agent, Arabic.

1. In the header, open **المشكلات** (Problems). Open the problem **انقطاعات متكررة في بوابة الخدمات الإلكترونية** (recurring e-services portal outages), raised from the outage's incidents, at the **خطأ معروف** (Known error) stage.
2. Show **تحليل السبب الجذري** (Root-cause analysis): the **الأسئلة الخمسة (لماذا)** (5 Whys), from «تتوقف البوابة عن الاستجابة صباحًا» to the conclusion, and the **مخطط هيكل السمكة** (fishbone) grouped by category, with the root cause «حجم مجمّع اتصالات قاعدة البيانات» marked.
3. Show the linked incidents, the configuration items, the timeline and the change that fixes it (section 17).
4. Show **الخطأ المعروف** (Known error): symptoms and a workaround in Arabic and English (ask the customer to try again after 10:00 or to use the Smart app).
5. Open one of the problem's incidents in the workspace. The known error appears on the incident with why it matches; press **إدراج في الرد (العربية)** (Insert into reply, Arabic): the workaround is in the reply box. Do not send it.
6. Mention: the problem manager publishes a known error to the portal as a knowledge article through the four-eyes review, and agents raise a problem from one or many incidents (the workspace's bulk bar, or **إنشاء مشكلة من هذا البلاغ** on an incident).

**Say:** "Recurring incidents become a problem with a structured root-cause analysis. Until it is fixed, every agent sees the workaround on the incidents it affects and pastes it into the reply in one click."

**قولوا:** «تعالج إدارة المشكلات السبب الجذري للبلاغات المتكررة بأدوات تحليل منظَّمة، ويظهر الحل البديل لموظفي الدعم في البلاغات المتأثرة ليُدرَج في الرد بنقرة واحدة.»

## 16. Problem detection

**Who:** government team lead (a problem manager), Arabic.

1. Open **المشكلات** → **المشكلات المقترحة** (Suggested problems).
2. The first suggestion groups **six similar incidents** about the customer happiness centre's printer: «انحشار الورق المتكرر في طابعة مركز إسعاد المتعاملين», with the AI's reason. It is sample data as the AI would make it; with a provider set up, Zelvo compares the incidents of the last 60 days every 6 hours, or at once with **البحث الآن** (Look now).
3. The second is a **اتجاه متصاعد** (rising trend) Zelvo found itself, without AI: more incidents of the Printers category in the last 30 days than usual.
4. Press **إنشاء المشكلة** (Raise the problem): the problem opens with the six incidents linked. Or show **رفض** (Dismiss): a reason is required, and the same group is not suggested again.

**Say:** "Zelvo spots repeating faults and unusual trends, and suggests a problem. It never raises one on its own: the problem manager decides."

**قولوا:** «يقترح النظام المشكلات من البلاغات المتشابهة ومن الارتفاع غير المعتاد في عدد البلاغات، ويبقى القرار لمدير المشكلات، فلا تُنشأ أي مشكلة دون موافقته.»

## 17. Change management ★

**Who:** government team lead, Arabic.

1. In the header, open **التغييرات** (Changes), then **تقويم التغييرات** (Change calendar): the scheduled fix of the problem «توسيع مجمّع اتصالات قاعدة بيانات البوابة» in three days, and the blackout periods «أسبوع الذروة لبوابة الخدمات الإلكترونية» (the portal's peak week) and «إقفال نهاية السنة المالية» (fiscal year-end; move to December if needed).
2. Back in the list, filter to risk **مرتفعة** (High) and stage **جديد** (New), and open «نقل نظام إدارة المعاملات إلى مركز البيانات الجديد» (move the transaction system to the new data centre), raised by the agent.
3. Show **المخاطر والأثر** (Risk and impact): the answers to the risk and impact questionnaire and the high risk level they give. Show the implementation, test and **خطة التراجع** (back-out) plans, the planned window and the affected configuration items.
4. Show **التعارضات** (Conflicts): its window falls in the portal's peak week, so the blackout is listed; approvers see it too.
5. Press **تقديم للاعتماد** (Submit for approval): a high-risk change goes to the change advisory board, then an executive. The change shows **بانتظار الاعتماد** (Awaiting approval).
6. Mention the other types: **standard changes** from the catalog (for example «إعادة تشغيل مجدولة لخادم تطبيقات», pre-approved, refused in a blackout period) and the **emergency change** «تطبيق تحديث أمني عاجل على خادم البوابة», implemented last night and waiting for its post-implementation review. As the administrator, Settings → **التغييرات** holds the questionnaire, the approval path of each risk level, the blackout periods and the standard changes.

**Say:** "Every change is assessed before it is approved, and its risk decides who approves it. Zelvo warns about clashes and blackout periods before anything is done, and every change is reviewed afterwards."

**قولوا:** «يُقيَّم كل تغيير باستبيان للمخاطر قبل اعتماده، ويحدد مستوى المخاطر جهة الاعتماد، وينبّه النظام إلى التعارضات وفترات الحظر قبل التنفيذ، ثم يُراجَع كل تغيير بعد تنفيذه.»

## 17. Change advisory board and the what-if simulation ★

**Who:** government team lead (who chairs the board), then the executive, Arabic. Submit the change first (section 17, step 5).

1. On the change page, show **ماذا لو** (What if): **نطاق الأثر** (Blast radius: affected items, business services, the departments and customer groups that use them, and the number of people), **تغييرات سابقة مماثلة** (Similar past changes with their outcomes, including the backed-out «تحديث إصدار خادم تطبيقات البوابة») and **المخاطر المتوقعة** (Predicted risk) beside the questionnaire's level, with the reasons. It informs the board; it never decides. With the mock provider, **الشرح بالذكاء الاصطناعي** (Explain with AI) explains it in Arabic or English.
2. Open **المجلس الاستشاري للتغيير** (Change advisory board): **التغييرات بانتظار المجلس** (Changes waiting for the board) lists the change. Open the planned meeting «الاجتماع الأسبوعي للمجلس الاستشاري للتغيير» (in two days, «قاعة الاجتماعات ٣»), with its chair and members. When a meeting is planned or changed, members get an email invitation with a calendar file (`.ics`) for their calendar.
3. Press **تحديث جدول الأعمال** (Update the agenda): the change joins the agenda with its blast radius and chance of failing.
4. Choose **موافقة** (Approve) and **حفظ صوتي** (Save my vote): the tally shows the vote, which is advice. Then **تسجيل القرار** (Record the decision) as the chair: **اعتمده المجلس** (Approved by the board), with the minutes.
5. Switch to the executive and open **اعتماداتي** (My approvals): the high-risk change waits for the executive's level, with the same what-if summary. Approve it; the change is **مُجدول** (Scheduled).
6. Mention the **emergency board**, which decides emergency changes in My approvals within 4 hours, without a meeting.

**Say:** "The board's agenda builds itself. Members vote, the chair decides, and the what-if simulation shows the blast radius and how similar changes went before."

**قولوا:** «يُعدّ جدول أعمال المجلس الاستشاري للتغيير تلقائيًا، فيصوّت الأعضاء ويسجّل الرئيس القرار، وتعرض محاكاة «ماذا لو» نطاق الأثر والمخاطر المتوقعة استنادًا إلى نتائج التغييرات السابقة، ويبقى القرار للمجلس.»

## 20. Plain-language rules

**Who:** government administrator, Arabic.

1. Open Settings → **قواعد سير العمل** (Workflow rules). Under **مسودات بانتظار اعتمادكم** (Drafts waiting for your approval), the draft «البلاغات الحرجة لكبار المتعاملين إلى مدير تقنية المعلومات» is marked **مسودة غير مطبّقة** (Draft, not running).
2. Show **وصفكم:** (You described): «عندما يقدّم أحد كبار المسؤولين بلاغًا حرجًا، أبلغ مدير تقنية المعلومات عبر واتساب», and the rule as a diagram: when (an incident is raised), if (the requester is in the VIP customer group and the priority is P1), then (tell the service desk's team leads by WhatsApp). Anything the AI could not map is listed, never guessed.
3. Press **اعتماد وتفعيل** (Approve and turn on), or **التعديل في أداة الإنشاء** (Change in the builder) to show that the draft is an ordinary rule.
4. With the mock provider (Before the meeting, step 5, feature "Rule builder"): type a rule under **صِف القاعدة بكلماتك** (Describe a rule in your own words) and press **إعداد القاعدة** (Build the rule). Every existing rule can also be shown as a diagram.

**Say:** "Administrators describe a rule in their own words, in Arabic or English. The AI drafts it from your own groups and services, and nothing runs until an administrator approves it."

**قولوا:** «يصف المسؤول القاعدة بلغته العربية أو الإنجليزية، فيُعدّ الذكاء الاصطناعي مسودة منها اعتمادًا على مجموعات جهتكم وخدماتها، ولا تُطبَّق إلا بعد مراجعتها واعتمادها.»

## 20. Custom record types

**Who:** government administrator, requester and agent, Arabic.

1. As the administrator, open Settings → **أنواع السجلات** (Record types): «طلب مرافق» (Facilities request) with its own number series `FAC-…`, six states (جديد، مجدول، في الموقع، بانتظار قطع الغيار، منجز، مغلق) each mapped onto a state of the ticket core, a portal entry and permissions per type for the roles chosen.
2. As the requester, open **السجلات** (Records) and «طلب مرافق» → **تقديم طلب** (Raise one): the type's own form.
3. As the agent, open **السجلات** → «طلب مرافق» → **عرض الكل** (See all): three samples, «تعطل التكييف في قاعة الاجتماعات الكبرى» (new), «إنارة الممر في الطابق الثاني لا تعمل» (on site) and «استبدال كرسي مكتب مكسور» (done). Open one: its states, its own SLA policy's clocks, the conversation and assignment work as for incidents.

**Say:** "You add your own kinds of requests, with their own states, numbers, forms and service levels, and they work with everything else: routing, SLAs, workflow rules, notifications, search and reports."

**قولوا:** «تُنشئ جهتكم أنواع طلباتها الخاصة بحالاتها وأرقامها ونماذجها ومستويات خدمتها دون برمجة، وتعمل مع جميع إمكانات المنصة من الإسناد ومستويات الخدمة إلى الإشعارات والتقارير.»

## 20. The HR app from the app builder

**Who:** company requester, then the company administrator, English.

1. As the requester, open **Apps** in the header, then **Human resources**: the portal entry **Request leave**. Open it: the leave request form takes **Leave type** from a data list (annual, sick, emergency, Hajj, study leave).
2. As the administrator, open Settings → **Apps** → **Human resources**: what the app groups together, the record type **Leave request** (`LVE-…`) with its states and form, the data list **Leave types**, the role **HR officer**, a workflow rule, list views, menu entries and the portal entry. **Switch the app off** hides all of it and **Switch the app on** restores it.
3. Explain export and import: the app is exported as one JSON package and imported into another installation (for example from the test installation to production). The import is validated against that installation, previewed with every permission it grants, and stays inactive until an administrator approves it, choosing to keep or replace what already exists. Everything is audited.
4. Open Settings → **Data lists**: simple lists that dropdowns of any form can use.

**Say:** "Departments get their own apps, built on screen without code, and move from your test installation to production as one package an administrator checks and approves."

**قولوا:** «تُبنى تطبيقات الإدارات، مثل الموارد البشرية، من الشاشة دون برمجة، وتُنقل بين المنصات حزمةً واحدة لا تُفعَّل إلا بعد فحصها واعتمادها من المسؤول.»

## 21. Agent wellbeing

**Who:** company team lead, then the company agent, English. Before the meeting, decide whether to show this module: present it as workload balancing, never as performance monitoring.

1. As the team lead, open **Wellbeing** in the header (**Agent wellbeing**). The note at the top says what the figures are for: balancing work, **not for ranking or scoring**. Agents are listed by name, never sorted by their figures, and there is no export.
2. The sample agent has open alerts: open work weighted by priority above the group's threshold, many new assignments, after-hours hours and a high share of angry or escalated tickets. The figures come only from what Zelvo already keeps (tickets, the business calendar), never from presence or keystrokes.
3. Press **Suggest reassignment** for the agent: unstarted tickets and available, qualified colleagues under the threshold. The team lead applies a suggestion one ticket at a time; nothing moves on its own.
4. Switch to the agent: Account → **My workload** shows the same figures and alerts the team lead sees.
5. Mention Settings → **Wellbeing**: off by default, turned on only after the administrator confirms staff were told, and then every agent is notified. Executives see group totals only.

**Say:** "Team leads see who is overloaded before it becomes a problem, and rebalance the work. It is not a league table: agents see their own figures, and it follows the UAE privacy law."

**قولوا:** «تساعد لوحة رفاهية الموظفين قادة الفرق على موازنة الأعباء قبل أن تتجاوز طاقة أي موظف، ولا تُستخدم لترتيب الموظفين أو تقييمهم، ويطّلع كل موظف على الأرقام المتعلقة به، وفق قانون حماية البيانات الشخصية.»

## 19. The installable app

**Who:** government requester on a phone (or the browser's device view), Arabic.

1. Open the portal on the phone. On Android (Chrome, Edge) the install offer **تثبيت التطبيق** (Install the app) appears; on iPhone and iPad, Zelvo explains Share → Add to Home Screen. The installed app carries the entity's own name and icon (Settings → **الهوية المؤسسية** → Installable app, with a QR code of the address to print).
2. Below phone width the header's links fold behind a menu button, and every page stays right to left.
3. On **الإبلاغ عن مشكلة** (Report a problem), show **التقاط صورة** (Take a photo) and **تسجيل مقطع فيديو** (Record a video): the phone's camera, then the usual checked and scanned upload.
4. Turn off the network (airplane mode, or Offline in the browser's developer tools) and open a page: **لا يوجد اتصال بالشبكة** (You are offline) with the person's last-viewed tickets, read-only. A new incident or reply written offline is kept on the device and sent when the connection is back. Turn the network on again.
5. Mention web push notifications on the phone (Account → Notifications, per device): they need outgoing internet to the browsers' push services, so offline installations use email, in-app and Teams notifications.

**Say:** "There is no app store: people install Zelvo on their phone with your name and icon, take photos straight into a ticket, and keep working when the connection drops."

**قولوا:** «يُثبَّت التطبيق على الهاتف باسم جهتكم وشعارها دون الحاجة إلى متاجر التطبيقات، ويتيح التقاط الصور مرفقاتٍ مباشرة، ويحفظ ما يُكتب دون اتصال لإرساله عند عودة الاتصال.»

## 19. Passkeys and the Teams app (describe only)

Both need real devices or a real Microsoft 365 tenant, which the demo does not have. Show the screens and describe the rest.

1. **Passkeys.** As the administrator, Settings → **تسجيل الدخول** → **مفاتيح المرور** (Passkeys): **متوقفة**، **مسموح بها**، **إلزامية للموظفين** (Off, Allowed, Required for staff). As the requester, Account → **الأمان** (Security): the person's passkeys and **إضافة مفتاح مرور** (Add a passkey). Explain: the phone's or computer's fingerprint, face or PIN replaces the password, and for staff it also counts as the second factor. Adding one needs a real device with a fingerprint reader or face recognition, so do not press it on the demo computer unless it has one.
2. **Microsoft Teams app.** The Teams app package is downloaded from Settings → **المراسلة** (Messaging), Teams (turned off in the demo). In Teams, people get three personal tabs (My tickets, My work, Approvals) showing Zelvo inside Teams, the bot's commands «رفع طلب»، «طلباتي»، «مساعدة» (Raise a ticket, My tickets, Help), "Raise a ticket" from any Teams message, status cards, and approvals decided on an Adaptive Card. Outside Teams, the tab page (`/teams/tab`) only says it opens in Teams.

**Say:** "Staff sign in with their fingerprint instead of a password, and work in Zelvo from inside Microsoft Teams."

**قولوا:** «يسجّل الموظفون الدخول ببصمة الإصبع أو الوجه دون كلمة مرور، ويستخدمون المنصة من داخل مايكروسوفت تيمز لتقديم الطلبات ومتابعتها واعتمادها.»

## UAE government requirements

**Who:** government administrator and requester, Arabic.

| Point | Where to show it |
| --- | --- |
| GOV-1 UAE PASS | Settings → تسجيل الدخول → الهوية الرقمية (UAE PASS): the approved rules (staff SOP2 and above, requesters' minimum level, automatic requester accounts). Not connected to real UAE PASS in the demo |
| GOV-2, GOV-3 happiness | The happiness meter and the customer voice report (section 3) |
| GOV-4 hosting in the UAE | The opening: installed on the customer's own servers |
| GOV-5 privacy law | Settings → الخصوصية (Privacy): the versioned privacy notice, optional consent purposes, retention periods. As the requester, Account → Privacy: consents and a request for a copy of their data. As the administrator, طلبات الخصوصية (Privacy requests): the open data subject request. Agent wellbeing is off by default and turned on only after staff are told (section 21) |
| GOV-6 Information Assurance | The control mapping delivered as evidence (`docs/security/uae-ia-mapping.md`) |
| GOV-7 business calendar | The government week and holidays (section 8) |
| GOV-8 formal Arabic | The Arabic emails and notification wording (section 10) |
| GOV-9 accessibility | Keyboard use (Tab, skip link) and right-to-left layout on any page |

**قولوا:** «صُمّمت المنصة لتلبية متطلبات الجهات الحكومية في الدولة: الدخول عبر الهوية الرقمية، ومقياس السعادة، وقانون حماية البيانات الشخصية، ومعيار ضمان أمن المعلومات، وتقويم العمل الرسمي، والمراسلات العربية الرسمية.»

## Demo mode and closing (2 minutes) ★

1. Open the demo panel once more: six people per organisation, one click each. Point at **آخر إعادة ضبط** (last reset).
2. As the administrator, press **إعادة ضبط العرض التوضيحي** (Reset demo) and **إعادة الضبط الآن**: the progress bar in the demo panel follows the background job, and in about a minute the demo is clean again, ready for the next meeting. Say that the other pages are unavailable while it runs, and that the administrator is notified when it ends. Do it at the end of the meeting, not in the middle of a section.

**Say:** "What you saw is the product as it is installed, not slides. We can install a test installation on your servers next."

**قولوا:** «ما شاهدتموه هو المنتج كما يُثبَّت فعليًا، وليس عرضًا تقديميًا. ويمكننا تثبيت نسخة تجريبية على خوادمكم في الخطوة التالية.»

## If something goes wrong

| Problem | Fix |
| --- | --- |
| A screen shows another person's data from an earlier demo | Reset the demo (Before the meeting, step 2) |
| Pages say the demo is being reset, or the sample organisations are unavailable | A reset is running (perhaps started in another tab or by a colleague). Open the demo panel to follow its progress bar; the pages come back by themselves in about a minute |
| The reset says it failed | Press **إعادة ضبط العرض التوضيحي** again; if it fails again, `make demo-wipe`, then `make demo` |
| The advisory board's agenda is empty | Submit the high-risk change first (section 17, step 5), then press **تحديث جدول الأعمال** (Update the agenda) |
| The high-risk change is no longer New, or the draft rule or suggested problems are gone | An earlier demo used them: reset the demo |
| The demo panel is missing | The installation is not the demo: `make down`, then `make demo` |
| Emails do not appear in Mailpit | Wait 30 seconds (the worker sends them); check `docker compose -p zelvo-demo ps` |
| The payment page does not open | The payment mock is part of Compose: `docker compose -p zelvo-demo ps payment-mock` |
| AI buttons are missing | No provider is set up: Before the meeting, step 5, or show the seeded suggestions only |

## Rehearsal checklist

Run the whole script once in each language before the first customer meeting, and after every release:

- [ ] Every ★ step works in Arabic (government entity) and English (company).
- [ ] Every module section works at least once, Phase 1 and Phase 2.
- [ ] The Phase 2 ★ path works in Arabic: `srv-web-01`'s impact and relationship map, the known error inserted into a reply, the high-risk change submitted, put on the board's agenda, voted on and decided, then approved by the executive.
- [ ] The high-risk change shows the peak-week blackout under **التعارضات**, and the change calendar shows both blackout periods (the year-end one from 28 December).
- [ ] The review queue, the suggested problems, the draft plain-language rule and the wellbeing alerts are there as seeded.
- [ ] The phone view of the portal works; installing the app and the offline page work on a phone or in the browser's device view.
- [ ] Mailpit shows the Arabic and English emails, and, after you change the board meeting, its invitation with the calendar file.
- [ ] "Reset demo" shows its progress bar, ends with the notification and restores the clean state afterwards (about a minute).

Record the date, the version and anything that did not work in the release-readiness report. Anything that did not work is a defect to fix before the release.
