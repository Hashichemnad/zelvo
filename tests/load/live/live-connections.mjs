#!/usr/bin/env node
// Task F7: holds the live-update connections of 1,000 agents (task B4, SignalR over Server-Sent
// Events through the web app's backend-for-frontend), next to tests/load/k6/agents.js, which k6
// cannot do on its own. Node.js 24, no dependencies. See tests/load/README.md.
//
//   node tests/load/live/live-connections.mjs                 # 1,000 connections for 30 minutes
//   CONNECTIONS=200 DURATION_MINUTES=5 node tests/load/live/live-connections.mjs
//
// Each connection negotiates, opens the event stream, sends the SignalR handshake and counts the
// messages it hears (ticketChanged, notificationsChanged, …). A connection that ends is opened
// again after a pause, like the browser does. Every 30 seconds it prints open connections,
// messages, failures and the time to connect; at the end it exits non-zero when fewer than 99 %
// of the connections were open on average or connecting took 1 second or more at the 95th
// percentile.

import { readFileSync } from "node:fs";
import path from "node:path";

const credentialsFile = process.env.LOAD_CREDENTIALS ?? path.resolve(import.meta.dirname, "../.credentials/load.json");
const provisioned = JSON.parse(readFileSync(credentialsFile, "utf8"));
const webUrl = process.env.WEB_URL ?? provisioned.webUrl;
const total = Number(process.env.CONNECTIONS ?? 1000);
const durationMs = Number(process.env.DURATION_MINUTES ?? 30) * 60_000;
const rampMs = Number(process.env.RAMP_SECONDS ?? 300) * 1000;
const recordSeparator = "\u001e";

const stats = { open: 0, connects: 0, failures: 0, messages: 0, connectTimes: [], openSamples: [] };
const stopAt = Date.now() + rampMs + durationMs;
const controllers = new Set();

function headersFor(session, extra = {}) {
  return { Cookie: session.cookie, "X-Requested-With": "XMLHttpRequest", ...extra };
}

async function connectOnce(session) {
  const started = performance.now();
  const negotiate = await fetch(`${webUrl}/bff/api/v1/live/negotiate?negotiateVersion=1`, {
    method: "POST",
    headers: headersFor(session, { "X-CSRF-Token": session.csrf, Origin: webUrl, "Content-Type": "text/plain;charset=UTF-8" }),
  });
  if (!negotiate.ok) {
    throw new Error(`negotiate ${negotiate.status}`);
  }
  const { connectionToken, connectionId } = await negotiate.json();
  const id = encodeURIComponent(connectionToken ?? connectionId);
  const controller = new AbortController();
  controllers.add(controller);
  try {
    const stream = await fetch(`${webUrl}/bff/api/v1/live?id=${id}`, {
      headers: headersFor(session, { Accept: "text/event-stream" }),
      signal: controller.signal,
    });
    if (!stream.ok || !stream.body) {
      throw new Error(`stream ${stream.status}`);
    }
    const handshake = await fetch(`${webUrl}/bff/api/v1/live?id=${id}`, {
      method: "POST",
      headers: headersFor(session, { "X-CSRF-Token": session.csrf, Origin: webUrl, "Content-Type": "text/plain;charset=UTF-8" }),
      body: `${JSON.stringify({ protocol: "json", version: 1 })}${recordSeparator}`,
    });
    if (!handshake.ok) {
      throw new Error(`handshake ${handshake.status}`);
    }
    stats.connectTimes.push(performance.now() - started);
    stats.connects++;
    stats.open++;
    try {
      const decoder = new TextDecoder();
      for await (const chunk of stream.body) {
        for (const line of decoder.decode(chunk, { stream: true }).split("\n")) {
          if (!line.startsWith("data:")) {
            continue;
          }
          for (const message of line.slice(5).split(recordSeparator)) {
            // Type 1 is an invocation (a live update); 6 is the server's keep-alive ping.
            if (message.includes('"type":1')) {
              stats.messages++;
            }
          }
        }
      }
    } finally {
      stats.open--;
    }
  } finally {
    controllers.delete(controller);
  }
}

async function agent(index) {
  const session = provisioned.sessions[index % provisioned.sessions.length];
  await new Promise((resolve) => setTimeout(resolve, (rampMs * index) / total));
  while (Date.now() < stopAt) {
    try {
      await connectOnce(session);
    } catch (error) {
      if (Date.now() >= stopAt) {
        break;
      }
      stats.failures++;
      if (stats.failures <= 5) {
        console.error(`connection ${index}: ${error.message}`);
      }
    }
    // The browser waits before connecting again.
    await new Promise((resolve) => setTimeout(resolve, 2_000 + Math.random() * 3_000));
  }
}

function percentile(values, p) {
  if (values.length === 0) {
    return 0;
  }
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
}

const report = setInterval(() => {
  // Only the holding phase counts, after every connection had its turn to open.
  if (Date.now() >= stopAt - durationMs) {
    stats.openSamples.push(stats.open);
  }
  console.log(
    `${new Date().toISOString()} open=${stats.open}/${total} connects=${stats.connects} failures=${stats.failures} ` +
      `messages=${stats.messages} connect p95=${percentile(stats.connectTimes, 95).toFixed(0)} ms`,
  );
}, 30_000);

console.log(`Opening ${total} live connections to ${webUrl} over ${rampMs / 1000} s, then holding them for ${durationMs / 60_000} min.`);
const agents = Array.from({ length: total }, (_, i) => agent(i));
setTimeout(() => {
  for (const controller of controllers) {
    controller.abort();
  }
}, stopAt - Date.now());
await Promise.allSettled(agents);
clearInterval(report);

const averageOpen = stats.openSamples.length ? stats.openSamples.reduce((a, b) => a + b, 0) / stats.openSamples.length : 0;
const connectP95 = percentile(stats.connectTimes, 95);
console.log(
  `Done: average open while holding ${averageOpen.toFixed(0)}/${total}, connects ${stats.connects}, failures ${stats.failures}, ` +
    `messages ${stats.messages}, connect p95 ${connectP95.toFixed(0)} ms.`,
);
process.exit(averageOpen >= 0.99 * total && connectP95 < 1000 ? 0 : 1);
