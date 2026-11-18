// Development stand-in for UAE PASS (task D3, GOV-1). Never part of an install.
//
// It answers like the documented UAE PASS web integration (docs.uaepass.ae, "Web Integration"):
//   GET  /idshub/authorize  -> sign-in page; then {redirect_uri}?code=...&state=...
//   POST /idshub/token      -> client authenticated with HTTP Basic only; opaque access token, no ID token
//   GET  /idshub/userinfo   -> the profile (uuid, userType SOP1/SOP2/SOP3, names in English and Arabic, ...)
//   GET  /idshub/logout     -> back to redirect_uri
// Instead of the UAE PASS app, the sign-in page lists test people to choose from.
// No dependencies: plain Node.js, so the image stays small and has nothing to update.

import { randomBytes, timingSafeEqual } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";

const port = Number(process.env.PORT ?? 8095);
const clientId = process.env.UAEPASS_MOCK_CLIENT_ID ?? "zelvo-mock";
const clientSecret = process.env.UAEPASS_MOCK_CLIENT_SECRET ?? "";
// UAE PASS takes the code within 10 seconds; the mock allows a little more for slow machines.
const codeSeconds = Number(process.env.UAEPASS_MOCK_CODE_SECONDS ?? 30);
const requiredScope = "urn:uae:digitalid:profile:general";

if (!clientSecret) {
  console.error("UAEPASS_MOCK_CLIENT_SECRET is required.");
  process.exit(1);
}

const people = JSON.parse(readFileSync(new URL("./people.json", import.meta.url), "utf8"));

/** Codes and tokens live in memory only; a restart signs everyone out. */
const codes = new Map(); // code -> { person, redirectUri, clientId, expires }
const tokens = new Map(); // token -> { person, expires }

const text = {
  en: {
    title: "UAE PASS (development mock)",
    heading: "Sign in with UAE PASS",
    notice: "Development stand-in for UAE PASS. Choose a test person; no real identity is used.",
    choose: "Continue as",
    cancel: "Cancel",
    invalid: "This sign-in request is not valid",
    sop: { SOP1: "Basic (SOP1, unverified)", SOP2: "Advanced (SOP2, verified)", SOP3: "Qualified (SOP3, verified)" },
  },
  ar: {
    title: "الهوية الرقمية (محاكاة للتطوير)",
    heading: "تسجيل الدخول باستخدام الهوية الرقمية",
    notice: "بديل مؤقت للهوية الرقمية لأغراض التطوير. اختر شخصًا تجريبيًا؛ لا تُستخدم هوية حقيقية.",
    choose: "المتابعة باسم",
    cancel: "إلغاء",
    invalid: "طلب تسجيل الدخول هذا غير صالح",
    sop: { SOP1: "أساسي (SOP1، غير موثّق)", SOP2: "متقدم (SOP2، موثّق)", SOP3: "مؤهل (SOP3، موثّق)" },
  },
};

const escape = (value) =>
  String(value).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

const isWebUrl = (value) => {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
};

function secretMatches(given, expected) {
  const a = Buffer.from(given);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

function send(res, status, body, headers = {}) {
  res.writeHead(status, {
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    ...headers,
  });
  res.end(body);
}

const json = (res, status, body) => send(res, status, JSON.stringify(body), { "Content-Type": "application/json" });

function page(res, status, lang, body) {
  const t = text[lang];
  send(
    res,
    status,
    `<!doctype html>
<html lang="${lang}" dir="${lang === "ar" ? "rtl" : "ltr"}">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escape(t.title)}</title>
<style>
  body { font-family: system-ui, sans-serif; margin: 0; background: #f4f6f5; color: #1b1f1d; }
  main { max-width: 34rem; margin-inline: auto; padding: 2rem 1rem; }
  .notice { background: #fff4d6; border: 1px solid #b38600; padding: 0.75rem; border-radius: 0.5rem; }
  ul { list-style: none; padding: 0; }
  li { margin-block: 0.75rem; }
  button { inline-size: 100%; text-align: start; padding: 0.75rem 1rem; border-radius: 0.5rem;
           border: 1px solid #1b6e4b; background: #fff; font: inherit; cursor: pointer; }
  button:focus-visible { outline: 3px solid #1b6e4b; outline-offset: 2px; }
  .cancel { border-color: #6b6b6b; text-align: center; }
  small { display: block; color: #4a524e; }
</style></head>
<body><main>${body}</main></body></html>`,
    { "Content-Type": "text/html; charset=utf-8", "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; form-action *" },
  );
}

/** "First Last" in the page's language; UAE PASS's fullname fields are comma-separated parts. */
function displayName(person, lang) {
  const first = lang === "ar" ? person.firstnameAR ?? person.firstnameEN : person.firstnameEN;
  const last = lang === "ar" ? person.lastnameAR ?? person.lastnameEN : person.lastnameEN;
  return [first, last].filter(Boolean).join(" ");
}

function hidden(name, value) {
  return `<input type="hidden" name="${escape(name)}" value="${escape(value)}">`;
}

function authorize(req, res, url) {
  const q = url.searchParams;
  const lang = q.get("ui_locales")?.toLowerCase().startsWith("ar") ? "ar" : "en";
  const t = text[lang];
  const redirectUri = q.get("redirect_uri") ?? "";
  const scope = (q.get("scope") ?? "").split(" ");
  const valid =
    q.get("response_type") === "code" &&
    q.get("client_id") === clientId &&
    isWebUrl(redirectUri) &&
    scope.includes(requiredScope) &&
    (q.get("acr_values") ?? "").startsWith("urn:safelayer:tws:policies:authentication:level:");
  if (!valid) {
    page(res, 400, lang, `<h1>${escape(t.invalid)}</h1>`);
    return;
  }

  const common =
    hidden("redirect_uri", redirectUri) + hidden("state", q.get("state") ?? "") + hidden("client_id", clientId) + hidden("lang", lang);
  const choices = people
    .map(
      (p) => `<li><form method="post" action="/idshub/authorize">${common}${hidden("person", p.uuid)}
<button type="submit" data-email="${escape(p.email)}">${escape(t.choose)} ${escape(displayName(p, lang))}
<small>${escape(p.email)} · ${escape(t.sop[p.userType])}</small></button></form></li>`,
    )
    .join("");
  page(
    res,
    200,
    lang,
    `<h1>${escape(t.heading)}</h1><p class="notice" role="note">${escape(t.notice)}</p><ul>${choices}</ul>
<form method="post" action="/idshub/authorize">${common}${hidden("cancel", "1")}<button class="cancel" type="submit">${escape(t.cancel)}</button></form>`,
  );
}

async function readBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 64 * 1024) {
      throw new Error("too large");
    }
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString("utf8");
}

async function approve(req, res) {
  const form = new URLSearchParams(await readBody(req));
  const redirectUri = form.get("redirect_uri") ?? "";
  if (form.get("client_id") !== clientId || !isWebUrl(redirectUri)) {
    page(res, 400, "en", `<h1>${escape(text.en.invalid)}</h1>`);
    return;
  }
  const target = new URL(redirectUri);
  if (form.get("state")) {
    target.searchParams.set("state", form.get("state"));
  }
  const person = people.find((p) => p.uuid === form.get("person"));
  if (form.get("cancel") || !person) {
    target.searchParams.set("error", "access_denied");
    target.searchParams.set("error_description", "User cancelled the authentication");
  } else {
    const code = randomBytes(16).toString("hex");
    codes.set(code, { person, redirectUri, clientId, expires: Date.now() + codeSeconds * 1000 });
    target.searchParams.set("code", code);
  }
  send(res, 302, "", { Location: target.href });
}

async function token(req, res, url) {
  const header = req.headers.authorization ?? "";
  if (!header.startsWith("Basic ")) {
    json(res, 401, { error: "invalid_client", error_description: "unsupportedAuthenticationScheme" });
    return;
  }
  const [id, ...rest] = Buffer.from(header.slice(6), "base64").toString("utf8").split(":");
  const secret = decodeURIComponent(rest.join(":"));
  if (decodeURIComponent(id) !== clientId || !secretMatches(secret, clientSecret)) {
    json(res, 401, { error: "invalid_client", error_description: "invalidCredentials" });
    return;
  }

  // UAE PASS's own examples send the parameters in the query string; standard clients send a form body.
  const params = new URLSearchParams(url.search);
  const type = req.headers["content-type"] ?? "";
  if (type.startsWith("application/x-www-form-urlencoded")) {
    for (const [k, v] of new URLSearchParams(await readBody(req))) {
      params.set(k, v);
    }
  }
  if (params.get("grant_type") !== "authorization_code") {
    json(res, 400, { error: "unsupported_grant_type" });
    return;
  }
  const code = params.get("code") ?? "";
  const issued = codes.get(code);
  codes.delete(code);
  if (!issued) {
    json(res, 400, { error: "invalid_grant", error_description: "codeNotFound" });
    return;
  }
  if (issued.expires < Date.now()) {
    json(res, 400, { error: "invalid_grant", error_description: "expiredCode" });
    return;
  }
  if (issued.redirectUri !== params.get("redirect_uri")) {
    json(res, 400, { error: "invalid_grant", error_description: "redirectUriMismatch" });
    return;
  }

  const accessToken = randomBytes(24).toString("hex");
  tokens.set(accessToken, { person: issued.person, expires: Date.now() + 3600 * 1000 });
  json(res, 200, { access_token: accessToken, scope: requiredScope, token_type: "Bearer", expires_in: 3600 });
}

function userinfo(req, res) {
  const header = req.headers.authorization ?? "";
  const issued = header.startsWith("Bearer ") ? tokens.get(header.slice(7)) : undefined;
  if (!issued || issued.expires < Date.now()) {
    json(res, 401, { error: "invalid_token" });
    return;
  }
  const { uuid, ...profile } = issued.person;
  json(res, 200, { sub: `UAEPASS/${uuid}`, uuid, ...profile });
}

function logout(req, res, url) {
  const redirectUri = url.searchParams.get("redirect_uri");
  if (redirectUri && isWebUrl(redirectUri)) {
    send(res, 302, "", { Location: redirectUri });
  } else {
    send(res, 200, "Signed out of the UAE PASS mock.", { "Content-Type": "text/plain; charset=utf-8" });
  }
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", "http://localhost");
  try {
    if (req.method === "GET" && url.pathname === "/healthz") {
      send(res, 200, "ok", { "Content-Type": "text/plain" });
    } else if (req.method === "GET" && url.pathname === "/idshub/authorize") {
      authorize(req, res, url);
    } else if (req.method === "POST" && url.pathname === "/idshub/authorize") {
      await approve(req, res);
    } else if (req.method === "POST" && url.pathname === "/idshub/token") {
      await token(req, res, url);
    } else if (req.method === "GET" && url.pathname === "/idshub/userinfo") {
      userinfo(req, res);
    } else if (req.method === "GET" && url.pathname === "/idshub/logout") {
      logout(req, res, url);
    } else {
      send(res, 404, "Not found", { "Content-Type": "text/plain" });
    }
  } catch {
    send(res, 400, "Bad request", { "Content-Type": "text/plain" });
  }
});

server.listen(port, () => console.log(`UAE PASS mock listening on ${port}`));
