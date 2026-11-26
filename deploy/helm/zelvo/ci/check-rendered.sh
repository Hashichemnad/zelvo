#!/usr/bin/env bash
# Checks the rendered chart (task F6): every pod but ClamAV's meets the "restricted" Pod Security
# Standard's main rules, no Zelvo container runs as root, and only the proxy's Service is exposed.
set -euo pipefail
rendered=$1
python3 - "$rendered" <<'PY'
import sys, yaml
docs = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d]
problems = []
for d in docs:
    kind, name = d["kind"], d["metadata"]["name"]
    if kind in ("Deployment", "Job"):
        pod = d["spec"]["template"]["spec"]
        if pod.get("automountServiceAccountToken") is not False:
            problems.append(f"{name}: mounts a service account token")
        if name.endswith("-clamav"):
            continue
        security = pod.get("securityContext", {})
        if not security.get("runAsNonRoot"):
            problems.append(f"{name}: not runAsNonRoot")
        if security.get("seccompProfile", {}).get("type") != "RuntimeDefault":
            problems.append(f"{name}: no RuntimeDefault seccomp profile")
        for c in pod["containers"]:
            cs = c.get("securityContext", {})
            if cs.get("allowPrivilegeEscalation") is not False:
                problems.append(f"{name}/{c['name']}: privilege escalation allowed")
            if cs.get("capabilities", {}).get("drop") != ["ALL"] or cs.get("capabilities", {}).get("add"):
                problems.append(f"{name}/{c['name']}: capabilities not all dropped")
            if not name.endswith("-keycloak") and cs.get("readOnlyRootFilesystem") is not True:
                problems.append(f"{name}/{c['name']}: root file system writable")
            if "resources" not in c or "limits" not in c["resources"]:
                problems.append(f"{name}/{c['name']}: no resource limits")
    if kind == "Service" and d["spec"].get("type", "ClusterIP") != "ClusterIP" and not name.endswith("-proxy"):
        problems.append(f"{name}: exposed outside the cluster")
kinds = {d["kind"] for d in docs}
for expected in ("NetworkPolicy", "PodDisruptionBudget", "HorizontalPodAutoscaler", "ClusterPolicy"):
    if expected not in kinds:
        problems.append(f"no {expected} rendered")
if problems:
    print("\n".join(problems), file=sys.stderr)
    sys.exit(1)
print(f"check-rendered: {len(docs)} resources meet the chart's security rules")
PY
