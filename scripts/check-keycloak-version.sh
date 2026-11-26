#!/usr/bin/env bash
# Checks that the Keycloak image in src/keycloak/Dockerfile and the org.keycloak libraries in
# src/keycloak/pom.xml are the same Keycloak version. An image rebuild suffix ("-2" in 26.8.2-2)
# is allowed: Keycloak republishes images with operating system fixes, the libraries stay 26.8.2.
set -euo pipefail
cd "$(dirname "$0")/../src/keycloak"

image_tag=$(sed -nE 's/^FROM keycloak\/keycloak:([^ ]+).*/\1/p' Dockerfile)
pom_version=$(sed -nE 's/.*<keycloak\.version>([^<]+)<\/keycloak\.version>.*/\1/p' pom.xml)

if [[ -z "$image_tag" || -z "$pom_version" ]]; then
  echo "Could not read the Keycloak image tag (Dockerfile) or keycloak.version (pom.xml)." >&2
  exit 1
fi

if [[ ! "$image_tag" =~ ^([0-9]+\.[0-9]+\.[0-9]+)(-[0-9]+)?$ ]]; then
  echo "Unexpected Keycloak image tag '$image_tag' (expected <major>.<minor>.<patch> or <major>.<minor>.<patch>-<rebuild>)." >&2
  exit 1
fi

if [[ "${BASH_REMATCH[1]}" != "$pom_version" ]]; then
  echo "Keycloak image $image_tag does not match keycloak.version $pom_version in pom.xml; upgrade both together." >&2
  exit 1
fi

# Task F7: the zelvo login theme carries a copy of Keycloak's login template (one accessibility
# fix); after an upgrade it is compared with the new parent theme and its header updated.
template=src/main/resources/theme/zelvo/login/template.ftl
if ! grep -q "Keycloak ${pom_version}'s keycloak.v2 login template" "$template"; then
  echo "$template is a copy of an older Keycloak login template; compare it with Keycloak $pom_version's keycloak.v2 template and update its header." >&2
  exit 1
fi

echo "Keycloak image $image_tag matches keycloak.version $pom_version."
