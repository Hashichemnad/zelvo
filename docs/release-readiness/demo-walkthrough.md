# Demo walkthrough (task F7)

A script for showing Zelvo to a prospective customer with the demo installation (`docs/architecture/demo-mode.md`). It covers every Phase 1 module (1–15 in `docs/requirements.md`), demo mode and the UAE government requirements. It runs in **both languages**: the government entity in **Arabic** (right to left, its default language) and the private company in **English**. Each module has an Arabic talking point in formal Arabic (per `docs/guidelines/arabic-formal-correspondence.md`) for Arabic-speaking audiences.

- **Full walkthrough:** about 90 minutes (all sections).
- **Short walkthrough:** about 20 minutes. Follow the steps marked ★ only.
- Everything shown is fictional sample data. Emails go to Mailpit, payments to a sandbox, and no real AI provider, mail server, WhatsApp number or directory is reached.

## Video of the walkthrough

The GitHub Actions workflow **Demo video** (Actions → Demo video → Run workflow; `.github/workflows/demo-video.yml`) records a video of this walkthrough for prospects who cannot attend. It starts the demo installation, runs `tests/e2e/video/demo-video.spec.ts` (41 steps across every module, each persona signed in with the demo sign-in, a caption naming each step), first in English and then in Arabic, at 1920×1080. It then joins the two clips into one MP4, `zelvo-demo-walkthrough`, kept as an artifact for 7 days. The inputs set how long each step stays on screen (`hold_ms`, 4500 by default) and how much every action is slowed down (`slow_mo_ms`, 250). The video shows pages and a few clicks; the live presentation below goes further (approving, paying, chatting). The video has no sound.

To record it on your own computer against `make demo`:

```bash
cd tests/e2e && npx playwright install chromium ffmpeg && E2E_VIDEO=1 npx playwright test
```

The clips are written to `tests/e2e/demo-video/` (git-ignored).

## Before the meeting

1. On the demo computer (development tools as in `README.md`), with the development stack stopped:

   ```bash
   make demo
   ```

2. Open `http://localhost:3000/ar`. In the demo panel under the header (**العرض التوضيحي**), sign in as the administrator (`admin@municipality.example`) and press **إعادة ضبط العرض التوضيحي** (Reset demo), then **إعادة الضبط الآن**. It takes seconds and restores the clean sample state of both organisations.
3. Open three browser tabs: the demo (`http://localhost:3000/ar`), Mailpit (`http://localhost:8025`, the demo's outgoing email) and a spare tab for the API explorer (`http://localhost:8080/api-explorer`).
4. Check the screen at 100 % zoom and turn off notifications of other apps. For a phone demonstration, open the portal on a phone on the same network, or use the browser's device view.
5. Optional, for the AI section: as the administrator, Settings → AI → Providers, add an "OpenAI-compatible" provider with the address `http://ai-mock:8097/v1` and any key. Then turn on the features in Settings → AI → Features. The development mock gives fixed answers of the right shape; say so if asked. Without it, the AI assistant still shows the seeded suggestions.

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

## UAE government requirements

**Who:** government administrator and requester, Arabic.

| Point | Where to show it |
| --- | --- |
| GOV-1 UAE PASS | Settings → تسجيل الدخول → الهوية الرقمية (UAE PASS): the approved rules (staff SOP2 and above, requesters' minimum level, automatic requester accounts). Not connected to real UAE PASS in the demo |
| GOV-2, GOV-3 happiness | The happiness meter and the customer voice report (section 3) |
| GOV-4 hosting in the UAE | The opening: installed on the customer's own servers |
| GOV-5 privacy law | Settings → الخصوصية (Privacy): the versioned privacy notice, optional consent purposes, retention periods. As the requester, Account → Privacy: consents and a request for a copy of their data. As the administrator, طلبات الخصوصية (Privacy requests): the open data subject request |
| GOV-6 Information Assurance | The control mapping delivered as evidence (`docs/security/uae-ia-mapping.md`) |
| GOV-7 business calendar | The government week and holidays (section 8) |
| GOV-8 formal Arabic | The Arabic emails and notification wording (section 10) |
| GOV-9 accessibility | Keyboard use (Tab, skip link) and right-to-left layout on any page |

**قولوا:** «صُمّمت المنصة لتلبية متطلبات الجهات الحكومية في الدولة: الدخول عبر الهوية الرقمية، ومقياس السعادة، وقانون حماية البيانات الشخصية، ومعيار ضمان أمن المعلومات، وتقويم العمل الرسمي، والمراسلات العربية الرسمية.»

## Demo mode and closing (2 minutes) ★

1. Open the demo panel once more: six people per organisation, one click each. Point at **آخر إعادة ضبط** (last reset).
2. As the administrator, press **إعادة ضبط العرض التوضيحي** (Reset demo): the demo is clean again in seconds, ready for the next meeting.

**Say:** "What you saw is the product as it is installed, not slides. We can install a test installation on your servers next."

**قولوا:** «ما شاهدتموه هو المنتج كما يُثبَّت فعليًا، وليس عرضًا تقديميًا. ويمكننا تثبيت نسخة تجريبية على خوادمكم في الخطوة التالية.»

## If something goes wrong

| Problem | Fix |
| --- | --- |
| A screen shows another person's data from an earlier demo | Reset the demo (Before the meeting, step 2) |
| The demo panel is missing | The installation is not the demo: `make down`, then `make demo` |
| Emails do not appear in Mailpit | Wait 30 seconds (the worker sends them); check `docker compose -p zelvo-demo ps` |
| The payment page does not open | The payment mock is part of Compose: `docker compose -p zelvo-demo ps payment-mock` |
| AI buttons are missing | No provider is set up: Before the meeting, step 5, or show the seeded suggestions only |

## Rehearsal checklist

Run the whole script once in each language before the first customer meeting, and after every release:

- [ ] Every ★ step works in Arabic (government entity) and English (company).
- [ ] Every module section works at least once.
- [ ] The phone view of the portal works.
- [ ] Mailpit shows the Arabic and English emails.
- [ ] "Reset demo" restores the clean state afterwards.

Record the date, the version and anything that did not work in the release-readiness report. Anything that did not work is a defect to fix before the release.
