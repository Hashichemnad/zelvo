// Fails CI on high or critical advisories in runtime dependencies (`npm audit --omit=dev`),
// except those listed in audit-exceptions.json with a reason and a review date. An exception
// past its review date, or one that no longer matches any advisory, also fails, so exceptions
// are revisited and removed once a fix is released.
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";

const failing = new Set(["high", "critical"]);
const { exceptions } = JSON.parse(readFileSync(new URL("../audit-exceptions.json", import.meta.url), "utf8"));
const today = new Date().toISOString().slice(0, 10);

const run = spawnSync("npm", ["audit", "--omit=dev", "--json"], { encoding: "utf8", shell: process.platform === "win32" });
let report;
try {
  report = JSON.parse(run.stdout);
} catch {
  console.error("npm audit did not return a report:\n", run.stdout, run.stderr);
  process.exit(1);
}

// Advisories are the `via` entries that are objects; string entries point at other packages.
const advisories = new Map();
for (const vulnerability of Object.values(report.vulnerabilities ?? {})) {
  for (const via of vulnerability.via) {
    if (typeof via === "object" && failing.has(via.severity)) {
      const id = via.url?.split("/").pop() ?? String(via.source);
      advisories.set(id, { id, package: via.name, severity: via.severity, title: via.title, url: via.url });
    }
  }
}

const problems = [];
for (const advisory of advisories.values()) {
  const exception = exceptions.find((e) => e.advisory === advisory.id && e.package === advisory.package);
  if (!exception) {
    problems.push(`${advisory.severity}: ${advisory.package} ${advisory.id} ${advisory.title} (${advisory.url})`);
  } else if (exception.reviewBy < today) {
    problems.push(`exception for ${advisory.id} needed review by ${exception.reviewBy}: check for a fix, then renew or remove it`);
  } else {
    console.log(`accepted until ${exception.reviewBy}: ${advisory.package} ${advisory.id} (${exception.reason})`);
  }
}
for (const exception of exceptions) {
  if (!advisories.has(exception.advisory)) {
    problems.push(`exception for ${exception.advisory} (${exception.package}) no longer matches an advisory: remove it from audit-exceptions.json`);
  }
}

if (problems.length > 0) {
  console.error(problems.join("\n"));
  process.exit(1);
}
console.log("No unaccepted high or critical advisories in runtime dependencies.");
