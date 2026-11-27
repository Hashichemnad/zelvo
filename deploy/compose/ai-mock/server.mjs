// Development AI provider mock (Compose only). Speaks the OpenAI Chat Completions API, so
// Settings → AI can be tried and the end-to-end tests can run without a real provider or key:
// add an "OpenAI-compatible" provider with the endpoint http://ai-mock:8097/v1. It answers
// connection tests with "OK", the AI features of task E5 and the K1 rule builder (recognised by the "Task: …" line of
// their instructions) with fixed answers of the shape each one needs, and anything else with a
// fixed sentence; counts tokens roughly, and records what it received (GET /received) so tests can
// check that personal data was masked. Never part of an installation.
import { createServer } from "node:http";

const port = Number(process.env.PORT ?? 8097);
const received = [];
const maxKept = 100;
const tokens = (text) => Math.max(1, Math.ceil(text.length / 4));

// The content of the data blocks of a prompt (<ZELVO-DATA-… label="…"> … </ZELVO-DATA-…>).
function blocks(prompt) {
  const found = [];
  const pattern = /<ZELVO-DATA-([0-9a-f]+) label="([^"]*)">\n?([\s\S]*?)\n?<\/ZELVO-DATA-\1>/g;
  for (const match of prompt.matchAll(pattern)) found.push({ label: match[2], text: match[3] });
  return found;
}

// Fixed answers per feature: enough to try every screen without a real model.
function featureAnswer(system, prompt) {
  const task = /Task: ([A-Za-z]+)\./.exec(system)?.[1];
  const data = blocks(prompt);
  const block = (label) => data.find((b) => b.label === label)?.text ?? "";
  const arabic = /into Arabic|in Arabic/.test(system);
  const articles = [...new Set(prompt.match(/KB-\d{6}/g) ?? [])];
  switch (task) {
    case "triage":
      return JSON.stringify({
        category: /\bc1:/.test(prompt) ? "c1" : null,
        group: /\bg1:/.test(prompt) ? "g1" : null,
        impact: 2,
        urgency: /urgent|down|stopped|عاجل|متوقف/i.test(block("incident")) ? 1 : 2,
        confidence: 72,
        reason: "Development mock: the first category and group.",
      });
    case "sentiment": {
      const text = block("message");
      const frustrated = /unacceptable|third time|angry|fed up|complain|غير مقبول|للمرة الثالثة|زهقت|بشتكي|سأشتكي/i.test(text);
      return JSON.stringify({ sentiment: frustrated ? "frustrated" : /thank|شكر/i.test(text) ? "positive" : "neutral", reason: "Development mock." });
    }
    case "duplicates":
      return JSON.stringify({ duplicates: /\bd1 \(/.test(prompt) ? [{ ref: "d1", confidence: 80, reason: "Development mock: the closest open incident." }] : [] });
    case "summary":
      return arabic
        ? "- ملخص تجريبي من خدمة الذكاء الاصطناعي الوهمية.\n- المشكلة كما وصفها مقدّم البلاغ.\n- لا توجد خطوات أخرى بعد."
        : "- Development mock summary.\n- The problem as the requester described it.\n- No further steps yet.";
    case "reply":
      return JSON.stringify({
        reply: arabic
          ? "شكرًا لتواصلكم. يُرجى اتباع الخطوات الواردة في المقال المرفق، وإبلاغنا إن استمرت المشكلة."
          : "Thank you for getting in touch. Please follow the steps in the linked article and let us know if the problem continues.",
        articles: articles.slice(0, 1),
      });
    case "translation": {
      const text = block("text");
      return arabic ? `[ترجمة تجريبية] ${text}` : `[Mock translation] ${text}`;
    }
    case "virtualAgent": {
      const last = data.filter((b) => b.label === "message").at(-1)?.text ?? "";
      const handover = /person|human|agent|staff|موظف|شخص/i.test(last);
      return JSON.stringify({
        answer: handover
          ? (arabic ? "سأحوّل المحادثة إلى أحد الموظفين." : "I will pass you to a member of staff.")
          : articles.length > 0
            ? (arabic ? `قد يساعدكم المقال ${articles[0]}.` : `Article ${articles[0]} may help.`)
            : (arabic ? "لم أجد مقالًا يجيب عن سؤالكم. هل تريدون التحدث إلى أحد الموظفين؟" : "I could not find an article that answers this. Would you like to talk to a member of staff?"),
        articles: handover ? [] : articles.slice(0, 1),
        handover,
      });
    }
    case "ruleBuilder": {
      // Task K1: a rule from the description, picking the first listed items it mentions.
      const text = block("description");
      const ref = (label, pattern) => {
        const lines = block(label).split("\n");
        return (lines.find((l) => pattern.test(l)) ?? lines[0] ?? "").split(":")[0].trim() || null;
      };
      const vip = /vip|كبار/i.test(text) ? ref("customerGroups", /vip|كبار|key|senior/i) : null;
      const group = ref("groups", /service desk|it |تقنية|مكتب الخدمة/i);
      const channels = [/whatsapp|واتساب/i.test(text) && "WhatsApp", /\bsms\b|رسالة نصية/i.test(text) && "Sms", /teams/i.test(text) && "Teams"].filter(Boolean);
      return JSON.stringify({
        name: { en: "Rule from the description (development mock)", ar: "قاعدة من الوصف (خدمة تجريبية)" },
        trigger: "TicketCreated",
        recordType: /incident|بلاغ/i.test(text) ? "Incident" : null,
        match: "All",
        conditions: [
          ...(vip ? [{ field: "requesterCustomerGroup", op: "in", value: [vip] }] : []),
          ...(/critical|p1|حرج/i.test(text) ? [{ field: "priority", op: "eq", value: 1 }] : []),
        ],
        actions: group ? [{
          type: "notify",
          recipients: [`groupLeads:${group}`],
          channels,
          email: true,
          subject: { en: "{{ticket.number}}: {{ticket.title}}", ar: "{{ticket.number}}: {{ticket.title}}" },
          body: { en: "Dear {{recipient.name}}, {{requester.name}} raised {{ticket.number}}.", ar: "حضرة {{recipient.name}}، قدّم {{requester.name}} البلاغ {{ticket.number}}." },
        }] : [],
        unmapped: /hours|ساعات/i.test(text) ? ["Working hours only"] : [],
      });
    }
    case "articleDraft":
      return JSON.stringify({
        title: { en: "How to fix the reported problem", ar: "طريقة حل المشكلة المبلّغ عنها" },
        summary: { en: "Steps that solved this problem before.", ar: "خطوات حلّت هذه المشكلة من قبل." },
        body: {
          en: "## Problem\n\nThe problem as reported.\n\n## Solution\n\n1. Follow the steps the agent took.",
          ar: "## المشكلة\n\nالمشكلة كما وردت في البلاغ.\n\n## الحل\n\n1. اتبع الخطوات التي اتخذها الموظف.",
        },
        keywords: ["mock"],
      });
    default:
      return null;
  }
}

function send(response, status, body) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(body));
}

createServer((request, response) => {
  const url = new URL(request.url ?? "/", "http://localhost");
  if (request.method === "GET" && url.pathname === "/healthz") {
    response.writeHead(200, { "content-type": "text/plain" }).end("ok");
    return;
  }
  if (request.method === "GET" && url.pathname === "/received") {
    send(response, 200, received);
    return;
  }
  if (request.method === "GET" && url.pathname === "/v1/models") {
    send(response, 200, { object: "list", data: [{ id: "zelvo-mock-fast", object: "model" }, { id: "zelvo-mock-strong", object: "model" }] });
    return;
  }
  if (request.method === "POST" && url.pathname === "/v1/chat/completions") {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      received.unshift({ receivedAt: new Date().toISOString(), authorization: request.headers.authorization ? "set" : null, body });
      received.length = Math.min(received.length, maxKept);
      let messages = [];
      let model = "zelvo-mock-fast";
      try {
        const json = JSON.parse(body);
        messages = Array.isArray(json.messages) ? json.messages : [];
        model = typeof json.model === "string" ? json.model : model;
      } catch {
        send(response, 400, { error: { message: "The request is not JSON." } });
        return;
      }
      const prompt = messages.map((m) => String(m.content ?? "")).join("\n");
      const system = String(messages.find((m) => m.role === "system")?.content ?? "");
      const answer = prompt.includes("connection test") ? "OK" : featureAnswer(system, prompt) ?? "This is an answer from the Zelvo development AI mock.";
      send(response, 200, {
        id: `mock-${Date.now()}`,
        object: "chat.completion",
        model,
        choices: [{ index: 0, message: { role: "assistant", content: answer }, finish_reason: "stop" }],
        usage: { prompt_tokens: tokens(prompt), completion_tokens: tokens(answer), total_tokens: tokens(prompt) + tokens(answer) },
      });
    });
    return;
  }
  send(response, 404, { error: { message: "Not found." } });
}).listen(port, () => console.log(`AI mock listening on ${port}`));
