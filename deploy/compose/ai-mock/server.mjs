// Development AI provider mock (Compose only). Speaks the OpenAI Chat Completions API, so
// Settings → AI can be tried and the end-to-end tests can run without a real provider or key:
// add an "OpenAI-compatible" provider with the endpoint http://ai-mock:8097/v1. It answers
// connection tests with "OK" and anything else with a fixed sentence, counts tokens roughly, and
// records what it received (GET /received) so tests can check that personal data was masked.
// Never part of an installation.
import { createServer } from "node:http";

const port = Number(process.env.PORT ?? 8097);
const received = [];
const maxKept = 100;
const tokens = (text) => Math.max(1, Math.ceil(text.length / 4));

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
      const answer = prompt.includes("connection test") ? "OK" : "This is an answer from the Zelvo development AI mock.";
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
