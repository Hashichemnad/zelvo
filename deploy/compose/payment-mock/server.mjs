// Development payment gateway mock (Compose only, task E8). A sandbox for Settings → Payments and
// the demo: no real money and no card data. Zelvo's generic connector maps it like any gateway:
//   POST /payments            { amount, currency, reference, returnUrl, callbackUrl } → { id, url }
//   GET  /payments/:id        → { status: PENDING | CAPTURED | DECLINED | CANCELLED, amount, reference }
//   POST /payments/:id/refund { amount } → { refundId, status: "REFUNDED" }
//   GET  /pay/:id             the payer's page: "Pay" or "Decline", then back to returnUrl
//   GET  /settlement.csv      today's transactions, as a settlement file
// When the payer decides, it also notifies callbackUrl directly (the address's origin replaced by
// CALLBACK_ORIGIN, because the browser's address is not the containers' one). Never part of an installation.
import { createServer } from "node:http";
import { randomUUID } from "node:crypto";

const port = Number(process.env.PORT ?? 8098);
const publicOrigin = process.env.PUBLIC_ORIGIN ?? `http://localhost:${port}`;
const callbackOrigin = process.env.CALLBACK_ORIGIN ?? "";
const payments = new Map();

const json = (res, status, body) => {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
};
const escape = (s) => String(s).replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
const read = (req) => new Promise((resolve) => {
  let body = "";
  req.on("data", (chunk) => { body += chunk; if (body.length > 65536) req.destroy(); });
  req.on("end", () => { try { resolve(body ? JSON.parse(body) : {}); } catch { resolve({}); } });
});

async function notify(payment) {
  if (!payment.callbackUrl) return;
  try {
    const target = new URL(payment.callbackUrl);
    const url = callbackOrigin ? new URL(target.pathname + target.search, callbackOrigin) : target;
    await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ reference: payment.reference, status: payment.status }) });
  } catch (error) {
    console.error("callback failed", error.message);
  }
}

createServer(async (req, res) => {
  const url = new URL(req.url, "http://mock");
  const parts = url.pathname.split("/").filter(Boolean);
  if (url.pathname === "/healthz") return json(res, 200, { ok: true });
  if (req.method === "POST" && url.pathname === "/payments") {
    const body = await read(req);
    const id = randomUUID();
    payments.set(id, { id, status: "PENDING", amount: Number(body.amount), reference: String(body.reference ?? ""), returnUrl: body.returnUrl, callbackUrl: body.callbackUrl, created: new Date().toISOString() });
    return json(res, 201, { id, url: `${publicOrigin}/pay/${id}` });
  }
  if (parts[0] === "payments" && parts[1] && payments.has(parts[1])) {
    const payment = payments.get(parts[1]);
    if (req.method === "GET" && parts.length === 2) return json(res, 200, { status: payment.status, amount: payment.amount, reference: payment.reference });
    if (req.method === "POST" && parts[2] === "refund") return json(res, 200, { refundId: randomUUID(), status: "REFUNDED" });
  }
  if (req.method === "GET" && parts[0] === "pay" && payments.has(parts[1])) {
    const payment = payments.get(parts[1]);
    const decision = url.searchParams.get("decision");
    if (decision === "pay" || decision === "decline") {
      payment.status = decision === "pay" ? "CAPTURED" : "DECLINED";
      await notify(payment);
      res.writeHead(303, { location: payment.returnUrl ?? "/" });
      return res.end();
    }
    res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    return res.end(`<!doctype html><html lang="en"><meta charset="utf-8"><title>Sandbox payment</title>
<body style="font-family:sans-serif;max-width:32rem;margin:3rem auto"><h1>Sandbox payment</h1>
<p>No real money. Amount: <strong>${escape(payment.amount.toFixed(2))} AED</strong></p><p>Reference: <code>${escape(payment.reference)}</code></p>
<p><a href="?decision=pay">Pay</a> &nbsp; <a href="?decision=decline">Decline</a></p></body></html>`);
  }
  if (req.method === "GET" && url.pathname === "/settlement.csv") {
    res.writeHead(200, { "content-type": "text/csv" });
    const rows = [...payments.values()].filter((p) => p.status === "CAPTURED").map((p) => `${p.reference},${p.id},${p.amount.toFixed(2)},${p.status}`);
    return res.end(["reference,transaction,amount,status", ...rows].join("\n") + "\n");
  }
  json(res, 404, { error: "not found" });
}).listen(port, () => console.log(`payment mock on ${port}`));
