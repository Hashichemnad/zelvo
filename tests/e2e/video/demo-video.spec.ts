import { mkdirSync } from "node:fs";
import path from "node:path";
import { test, type Page } from "@playwright/test";

/**
 * Task F7: records the demo walkthrough (docs/release-readiness/demo-walkthrough.md) as a video,
 * once in English and once in Arabic, against the demo installation (`make demo`). Runs only with
 * E2E_VIDEO=1 (project demo-video, 1920×1080, slowed down; workflow "Demo video"). Each step signs
 * in as its persona with the demo sign-in, opens a page, shows a short caption naming the step and
 * holds it long enough to watch. Clicks and typing that only make the video livelier never fail the
 * recording: if the sample data has changed, the step still shows its page. Since task L1 it also
 * covers Phase 2 (CMDB, discovery, assets, problems, changes and the advisory board, plain-language
 * rules, custom record types, the HR app, wellbeing, the installable app and passkeys): 58 steps.
 * Two of them change the sample data, as the live walkthrough does: the English run submits the
 * high-risk sample change for approval and the board's chair saves a vote on it (the Arabic run then
 * finds it on the agenda). Reset the demo after recording against `make demo`.
 *
 * The videos are written to tests/e2e/demo-video/<n>-<language>.webm; the workflow joins them.
 */

type Language = "en" | "ar";
type Persona = "visitor" | "requester" | "agent" | "lead" | "approver" | "admin" | "executive";
type Tenant = "demo-municipality" | "demo-company";

interface Step {
  /** Who is signed in; "visitor" is signed out. */
  persona: Persona;
  tenant: Tenant;
  /** Path after the language, "" for the home page. */
  path: string;
  /** The caption, in each language: the module and what the step shows. */
  caption: Record<Language, string>;
  /** Something to do on the page after the caption appears (optional, never fails the video). */
  act?: (page: Page, language: Language) => Promise<void>;
}

const outputDir = path.resolve(__dirname, "../demo-video");
const holdMs = Number(process.env.DEMO_VIDEO_HOLD_MS ?? 4500);
const gov: Tenant = "demo-municipality";
const company: Tenant = "demo-company";

/** Opens the first link whose text matches, if there is one. */
const openLink = (name: RegExp) => async (page: Page) => {
  await page.getByRole("link", { name }).first().click({ timeout: 5_000 });
};

/** Opens the first link whose address matches, if there is one (detail pages of a list). */
const openHref = (href: RegExp) => async (page: Page) => {
  const links = page.locator("main a[href]");
  const hrefs = await links.evaluateAll((all) => all.map((a) => a.getAttribute("href") ?? ""));
  const index = hrefs.findIndex((h) => href.test(h.split("?")[0] ?? ""));
  if (index >= 0) {
    await links.nth(index).click({ timeout: 5_000 });
  }
};

/** Scrolls the part of the page with this test id into the middle of the screen. */
const scrollTo = (testId: string) => async (page: Page) => {
  const part = page.getByTestId(testId).first();
  await part.waitFor({ state: "visible", timeout: 15_000 });
  await part.evaluate((el) => el.scrollIntoView({ block: "center" }));
};

/** Presses the first button whose name matches, when the page has one (sample data may have moved on). */
const pressIfThere = (name: RegExp) => async (page: Page) => {
  const button = page.getByRole("button", { name });
  if ((await button.count()) > 0) {
    await button.first().click({ timeout: 5_000 });
    await page.waitForTimeout(1_500);
  }
};

/** Does several things in order. */
const all = (...acts: NonNullable<Step["act"]>[]) => async (page: Page, language: Language) => {
  for (const act of acts) {
    await act(page, language);
  }
};

const steps: Step[] = [
  // Portal, knowledge, catalog and customer voice for visitors (modules 4, 7, 2, 3).
  { persona: "visitor", tenant: gov, path: "", caption: { en: "Zelvo demo walkthrough: one product, in English and Arabic, under your own name", ar: "جولة في زيلفو: منصة واحدة باللغتين العربية والإنجليزية وباسم جهتكم" } },
  {
    persona: "visitor", tenant: gov, path: "",
    caption: { en: "Self-service portal: search first, results as you type", ar: "بوابة الخدمة الذاتية: البحث أولًا والنتائج أثناء الكتابة" },
    act: async (page, language) => {
      await page.getByRole("searchbox").first().pressSequentially(language === "ar" ? "رخصة" : "licence", { delay: 150, timeout: 5_000 });
    },
  },
  { persona: "visitor", tenant: gov, path: "/help", caption: { en: "Knowledge base: help articles visitors may read", ar: "قاعدة المعرفة: مقالات المساعدة المتاحة للزوار" } },
  { persona: "visitor", tenant: gov, path: "/catalog", caption: { en: "Service catalog: public services with fees and documents", ar: "دليل الخدمات: خدمات عامة برسومها ومستنداتها" } },
  { persona: "visitor", tenant: gov, path: "/contact", caption: { en: "Customer voice: complaints, inquiries and suggestions, also without signing in", ar: "صوت المتعامل: شكاوى واستفسارات واقتراحات، حتى دون تسجيل الدخول" } },
  { persona: "visitor", tenant: gov, path: "/track", caption: { en: "Follow an anonymous message with its number and private code", ar: "متابعة رسالة دون تسجيل الدخول برقمها ورمزها الخاص" } },

  // The requester (modules 4, 1, 2, 3).
  { persona: "requester", tenant: gov, path: "", caption: { en: "Signed in: open tickets with progress, raise a ticket in two clicks", ar: "بعد تسجيل الدخول: الطلبات المفتوحة وتقدّمها، وتقديم طلب بنقرتين" } },
  { persona: "requester", tenant: gov, path: "/my-tickets", caption: { en: "My tickets: every ticket in one list, with the team that has it", ar: "طلباتي: جميع الطلبات في قائمة واحدة مع الفريق المختص" } },
  { persona: "requester", tenant: gov, path: "/my-incidents/new", caption: { en: "Incident management: report a problem with the category's form", ar: "إدارة البلاغات: الإبلاغ عن مشكلة بنموذج الفئة" } },
  {
    persona: "requester", tenant: gov, path: "/catalog",
    caption: { en: "Service requests: the service card with steps, documents and FAQ", ar: "طلبات الخدمات: بطاقة الخدمة بخطواتها ومستنداتها وأسئلتها الشائعة" },
    act: openLink(/New laptop|حاسوب محمول جديد/),
  },
  { persona: "requester", tenant: gov, path: "/my-voice", caption: { en: "Customer voice: the requester's complaints, inquiries and suggestions", ar: "صوت المتعامل: شكاوى المتعامل واستفساراته واقتراحاته" } },

  // The agent (modules 5, 1, 8, 6, 9, 2).
  {
    persona: "agent", tenant: company, path: "/workspace",
    caption: { en: "Agent workspace: the queue, the incident and the customer on one screen", ar: "مساحة العمل: قائمة البلاغات والبلاغ والمتعامل في شاشة واحدة" },
    act: openLink(/Email delivery delayed/),
  },
  { persona: "agent", tenant: company, path: "/workspace", caption: { en: "Conversation, internal notes, canned responses and the AI assistant", ar: "المراسلات والملاحظات الداخلية والردود الجاهزة والمساعد الذكي" }, act: openLink(/Laptop docking station|VPN disconnects/) },
  { persona: "agent", tenant: company, path: "/incidents", caption: { en: "SLA management: response and resolution clocks in business time", ar: "إدارة مستوى الخدمة: مؤقتا الاستجابة والحل وفق ساعات العمل" }, act: openHref(/\/incidents\/[0-9a-f-]{36}$/) },
  { persona: "agent", tenant: company, path: "/chats", caption: { en: "Omnichannel: live chat beside email, WhatsApp, SMS and Teams", ar: "القنوات المتعددة: المحادثة المباشرة إلى جانب البريد وواتساب والرسائل النصية وتيمز" } },
  { persona: "agent", tenant: company, path: "/tasks", caption: { en: "Fulfilment: the tasks of service requests", ar: "تنفيذ الطلبات: مهام طلبات الخدمات" } },
  { persona: "agent", tenant: gov, path: "/knowledge", caption: { en: "Knowledge base: articles in both languages, made from resolved tickets", ar: "قاعدة المعرفة: مقالات باللغتين تُعدّ من الطلبات المحلولة" } },

  // Team lead and approver (modules 7, 2, 10).
  { persona: "lead", tenant: gov, path: "/knowledge/review", caption: { en: "Four-eyes review: a second person publishes each article", ar: "المراجعة الرباعية: ينشر كل مقالة شخص غير كاتبها" } },
  { persona: "approver", tenant: gov, path: "/approvals", caption: { en: "Approvals: approve or reject from the portal, email or Teams", ar: "الاعتمادات: الاعتماد أو الرفض من البوابة أو البريد أو تيمز" } },

  // The executive (modules 10, 3, 15).
  { persona: "executive", tenant: gov, path: "/reports", caption: { en: "Reports: dashboards with six months of trends", ar: "التقارير: لوحات معلومات باتجاهات ستة أشهر" } },
  { persona: "executive", tenant: gov, path: "/reports/sla", caption: { en: "SLA compliance against the entity's target", ar: "الالتزام بمستوى الخدمة مقارنة بهدف الجهة" } },
  { persona: "executive", tenant: gov, path: "/customer-voice/report", caption: { en: "Customer voice and happiness report", ar: "تقرير صوت المتعامل والسعادة" } },
  { persona: "executive", tenant: gov, path: "/payments", caption: { en: "Online payments: receipts, export and daily reconciliation", ar: "المدفوعات الإلكترونية: الإيصالات والتصدير والمطابقة اليومية" } },

  // The tenant administrator (modules 10, 2, 8, 11, 9, 14, 6, 15, 12, 13, GOV).
  { persona: "admin", tenant: gov, path: "/settings", caption: { en: "Administration: everything is set up on screen, without code", ar: "الإدارة: تُهيّأ جميع الإعدادات من الشاشة دون برمجة" } },
  { persona: "admin", tenant: gov, path: "/setup", caption: { en: "Setup wizard and department templates", ar: "معالج الإعداد وقوالب الإدارات" } },
  { persona: "admin", tenant: gov, path: "/settings/catalog", caption: { en: "Catalog builder: versions, review, bundles and proactive services", ar: "منشئ دليل الخدمات: الإصدارات والمراجعة والباقات والخدمات الاستباقية" } },
  { persona: "admin", tenant: gov, path: "/settings/forms", caption: { en: "Form builder: fields, rules and previews in both languages", ar: "منشئ النماذج: الحقول والقواعد والمعاينة باللغتين" }, act: openHref(/\/settings\/forms\/[0-9a-f-]{36}$/) },
  { persona: "admin", tenant: gov, path: "/settings/workflows", caption: { en: "Workflow rules and approvals", ar: "قواعد سير العمل والاعتمادات" } },
  { persona: "admin", tenant: gov, path: "/settings/business-calendars", caption: { en: "UAE business calendar: the government week and public holidays", ar: "تقويم العمل في الدولة: أسبوع العمل الحكومي والعطل الرسمية" } },
  { persona: "admin", tenant: gov, path: "/settings/notifications", caption: { en: "Notifications: wording in English and formal Arabic", ar: "الإشعارات: الصياغة بالإنجليزية والعربية الرسمية" } },
  { persona: "admin", tenant: gov, path: "/settings/branding", caption: { en: "White-label branding: name, colours, domain and email sender", ar: "الهوية المؤسسية: الاسم والألوان والنطاق ومُرسِل البريد" } },
  { persona: "admin", tenant: gov, path: "/settings/ai", caption: { en: "AI with your own provider and key, personal data masked", ar: "الذكاء الاصطناعي بمزوّدكم ومفتاحكم، مع حجب البيانات الشخصية" } },
  { persona: "admin", tenant: gov, path: "/settings/integrations", caption: { en: "Integrations: API keys, OAuth clients and signed webhooks", ar: "التكاملات: مفاتيح الواجهة البرمجية وعملاء OAuth والإشعارات الموقّعة" } },
  { persona: "admin", tenant: gov, path: "/settings/connectors", caption: { en: "Connectors: directories, CRMs and the REST connector builder", ar: "الموصلات: أدلة المستخدمين وأنظمة علاقات المتعاملين ومنشئ الموصلات" } },
  { persona: "admin", tenant: gov, path: "/settings/messaging", caption: { en: "Messaging channels: WhatsApp, SMS and Microsoft Teams", ar: "قنوات المراسلة: واتساب والرسائل النصية ومايكروسوفت تيمز" } },
  { persona: "admin", tenant: gov, path: "/settings/payments", caption: { en: "Payment gateways connected from their documentation", ar: "بوابات الدفع تُربط وفق وثائقها" } },
  { persona: "admin", tenant: gov, path: "/settings/sign-in/uae-pass", caption: { en: "UAE PASS sign-in", ar: "تسجيل الدخول بالهوية الرقمية" } },
  { persona: "admin", tenant: gov, path: "/settings/privacy", caption: { en: "UAE privacy law: notice, consent, retention and requests", ar: "قانون حماية البيانات الشخصية: الإشعار والموافقة والاحتفاظ والطلبات" } },
  { persona: "admin", tenant: gov, path: "/settings/license", caption: { en: "Licensing: offline activation and release integrity", ar: "الترخيص: تفعيل دون اتصال وسلامة الإصدار" } },
  { persona: "admin", tenant: gov, path: "/settings/updates", caption: { en: "Offline update packages with backup and automatic rollback", ar: "حزم التحديث دون اتصال مع نسخ احتياطي وتراجع تلقائي" } },

  // Phase 2 (modules 16–21): CMDB, discovery and assets (18).
  {
    persona: "agent", tenant: gov, path: "/cmdb?q=srv-web-01",
    caption: { en: "CMDB: a server, what a fault of it affects and its relationship map", ar: "قاعدة بيانات إدارة التهيئة: خادم وما يتأثر بعطله وخريطة علاقاته" },
    act: all(openHref(/\/cmdb\/[0-9a-f-]{36}$/), scrollTo("ci-impact")),
  },
  { persona: "admin", tenant: gov, path: "/cmdb/review", caption: { en: "Discovery and reconciliation: possible duplicates and doubtful matches wait for review", ar: "الاكتشاف والمطابقة: العناصر المكررة المحتملة والمطابقات غير المؤكدة بانتظار المراجعة" } },
  { persona: "agent", tenant: company, path: "/assets/licenses", caption: { en: "Asset management: software licences against what discovery found installed", ar: "إدارة الأصول: تراخيص البرمجيات مقارنة بالبرمجيات المثبّتة التي رصدها الاكتشاف" } },

  // Problem management and problem detection (16).
  {
    persona: "agent", tenant: gov, path: "/problems?stage=KnownError",
    caption: { en: "Problem management: 5 Whys, fishbone and the known error", ar: "إدارة المشكلات: الأسئلة الخمسة ومخطط هيكل السمكة والخطأ المعروف" },
    act: all(openHref(/\/problems\/[0-9a-f-]{36}$/), scrollTo("fishbone-list")),
  },
  {
    persona: "agent", tenant: gov, path: "/workspace",
    caption: { en: "The known error's workaround, inserted into the reply in one click", ar: "إدراج الحل البديل للخطأ المعروف في الرد بنقرة واحدة" },
    act: all(openLink(/انقطاع بوابة الخدمات الإلكترونية/), async (page, language) => {
      const name = language === "ar" ? /إدراج في الرد \(العربية\)/ : /Insert into reply \(English\)/;
      await page.getByTestId("incident-known-errors").getByRole("button", { name }).first().click({ timeout: 15_000 });
    }, scrollTo("composer-body")),
  },
  { persona: "lead", tenant: gov, path: "/problems/suggestions", caption: { en: "Problem detection: similar incidents and rising trends suggested to the problem manager", ar: "اكتشاف المشكلات: بلاغات متشابهة واتجاهات متصاعدة تُقترح على مدير المشكلات" } },

  // Change management, the change advisory board and the what-if simulation (17).
  { persona: "lead", tenant: gov, path: "/changes/calendar", caption: { en: "Change calendar: planned windows, conflicts and blackout periods", ar: "تقويم التغييرات: النوافذ المخطط لها والتعارضات وفترات الحظر" } },
  {
    // The high-risk sample change; the English run submits it, so the board has it on its agenda.
    persona: "lead", tenant: gov, path: "/changes?riskLevel=High&kind=Normal",
    caption: { en: "Change management: risk questionnaire, conflicts and submission for approval", ar: "إدارة التغيير: استبيان المخاطر والتعارضات والتقديم للاعتماد" },
    act: all(openHref(/\/changes\/[0-9a-f-]{36}$/), scrollTo("change-work"), pressIfThere(/Submit for approval|تقديم للاعتماد/), scrollTo("change-risk")),
  },
  {
    persona: "lead", tenant: gov, path: "/changes?riskLevel=High&kind=Normal",
    caption: { en: "What-if simulation: blast radius, similar past changes and predicted risk", ar: "محاكاة «ماذا لو»: نطاق الأثر والتغييرات السابقة المماثلة والمخاطر المتوقعة" },
    act: all(openHref(/\/changes\/[0-9a-f-]{36}$/), scrollTo("what-if-blast")),
  },
  {
    persona: "lead", tenant: gov, path: "/changes/board",
    caption: { en: "Change advisory board: the agenda, the members' votes and the chair's decision", ar: "المجلس الاستشاري للتغيير: جدول الأعمال وأصوات الأعضاء وقرار الرئيس" },
    act: all(openHref(/\/changes\/board\/[0-9a-f-]{36}$/), scrollTo("meeting-facts"), pressIfThere(/Update the agenda|تحديث جدول الأعمال/), async (page) => {
      const item = page.getByTestId("agenda-item").first();
      await item.scrollIntoViewIfNeeded({ timeout: 5_000 });
      await item.getByTestId("vote-form").getByRole("radio", { name: /^(Approve|موافقة)$/ }).check({ timeout: 5_000 });
      await item.getByRole("button", { name: /Save my vote|حفظ صوتي/ }).click({ timeout: 5_000 });
    }),
  },

  // Plain-language rules, custom record types and the app builder (20).
  {
    persona: "admin", tenant: gov, path: "/settings/workflows",
    caption: { en: "Plain-language rules: described in Arabic or English, run only once approved", ar: "القواعد باللغة الطبيعية: توصف بالعربية أو الإنجليزية ولا تُطبَّق إلا بعد اعتمادها" },
    act: scrollTo("rule-drafts"),
  },
  {
    persona: "agent", tenant: gov, path: "/records/facilities",
    caption: { en: "Custom record types: facilities requests with their own states, numbers and SLA", ar: "أنواع السجلات المخصصة: طلبات المرافق بحالاتها وأرقامها ومستوى خدمتها" },
    act: openHref(/\/records\/facilities\/[0-9a-f-]{36}$/),
  },
  { persona: "requester", tenant: company, path: "/apps/hr", caption: { en: "App builder: the HR app with its leave requests, made on screen without code", ar: "منشئ التطبيقات: تطبيق الموارد البشرية وطلبات الإجازة، مُعدّ من الشاشة دون برمجة" } },

  // Agent wellbeing (21).
  { persona: "lead", tenant: company, path: "/wellbeing", caption: { en: "Agent wellbeing: balancing workload before anyone is overloaded, not for ranking", ar: "رفاهية الموظفين: موازنة أعباء العمل قبل أن تتجاوز طاقة أي موظف، دون ترتيب أو تقييم" } },
  { persona: "agent", tenant: company, path: "/account/workload", caption: { en: "My workload: every agent sees the figures their team leads see", ar: "أعباء عملي: يطّلع كل موظف على الأرقام التي يطّلع عليها قادة فريقه" } },

  // Installable app and passkeys (19).
  { persona: "visitor", tenant: gov, path: "/offline", caption: { en: "Installable app: your name and icon on the home screen, and an offline page", ar: "التطبيق القابل للتثبيت: اسم جهتكم وشعارها على الشاشة الرئيسية، مع صفحة لعدم الاتصال" } },
  { persona: "requester", tenant: gov, path: "/account/security", caption: { en: "Passkeys: sign in with a fingerprint or face, which also counts as the second factor", ar: "مفاتيح المرور: تسجيل الدخول ببصمة الإصبع أو الوجه، ويُعدّ ذلك عاملًا ثانيًا للتحقق" } },

  { persona: "admin", tenant: gov, path: "", caption: { en: "Demo mode: one-click users and Reset demo. Thank you.", ar: "وضع العرض التوضيحي: تبديل المستخدمين بنقرة وإعادة الضبط. شكرًا لكم." } },
];

/** Signs in as a demo user of a sample organisation, or opens it signed out (no persona). */
async function signInAs(page: Page, tenant: Tenant, persona: Persona, language: Language, baseURL: string) {
  const response = await page.request.post("/auth/demo", {
    form: { tenant, locale: language, persona: persona === "visitor" ? "" : persona },
    headers: { Origin: baseURL },
  });
  if (!response.ok()) {
    throw new Error(`demo sign-in as ${persona} of ${tenant}: ${response.status()}`);
  }
}

/** Waits for the page's main content (signed-in pages keep a live connection, so never "networkidle"). */
async function settle(page: Page) {
  await page.waitForLoadState("load");
  await page.locator("main").first().waitFor({ state: "visible", timeout: 30_000 });
}

/** Shows the caption at the bottom of the page, in the step's language and direction. */
async function caption(page: Page, text: string, language: Language, number: number) {
  await page.evaluate(
    ({ text, rtl, number }) => {
      let box = document.getElementById("zelvo-video-caption");
      if (!box) {
        box = document.createElement("div");
        box.id = "zelvo-video-caption";
        box.setAttribute("aria-hidden", "true");
        Object.assign(box.style, {
          position: "fixed", insetInline: "0", insetBlockEnd: "48px", marginInline: "auto", width: "fit-content",
          maxWidth: "80%", padding: "16px 28px", borderRadius: "12px", background: "rgba(17, 24, 39, 0.88)",
          color: "#ffffff", fontWeight: "600", fontSize: "30px", lineHeight: "1.4", textAlign: "center",
          // The tenant's brand font is bundled with the web app and has Arabic letters; the runner's may not.
          fontFamily: `${getComputedStyle(document.body).fontFamily}, sans-serif`,
          zIndex: "2147483647", boxShadow: "0 8px 24px rgba(0, 0, 0, 0.35)", pointerEvents: "none",
        });
        document.body.appendChild(box);
      }
      box.dir = rtl ? "rtl" : "ltr";
      box.textContent = `${number}. ${text}`;
    },
    { text, rtl: language === "ar", number },
  );
}

test.describe.configure({ mode: "serial" });

(["en", "ar"] as const).forEach((language, index) => {
  test(`demo walkthrough video in ${language === "en" ? "English" : "Arabic"}`, async ({ page, baseURL }) => {
    test.setTimeout(30 * 60_000);
    const origin = baseURL ?? "http://localhost:3000";
    let signedIn: string | null = null;
    for (const [i, step] of steps.entries()) {
      const who = `${step.tenant}/${step.persona}`;
      if (who !== signedIn) {
        await signInAs(page, step.tenant, step.persona, language, origin);
        signedIn = who;
      }
      await page.goto(`/${language}${step.path}`);
      await settle(page);
      await caption(page, step.caption[language], language, i + 1);
      await page.waitForTimeout(holdMs / 3);
      if (step.act) {
        try {
          await step.act(page, language);
          await settle(page);
          // A click may have opened another page: show the caption there too.
          await caption(page, step.caption[language], language, i + 1);
        } catch (error) {
          console.warn(`step ${i + 1} (${step.path}): ${(error as Error).message.split("\n")[0]}`);
        }
      }
      await page.mouse.wheel(0, 400);
      await page.waitForTimeout(holdMs);
    }

    mkdirSync(outputDir, { recursive: true });
    const video = page.video();
    await page.close();
    await video?.saveAs(path.join(outputDir, `${index + 1}-${language}.webm`));
  });
});
