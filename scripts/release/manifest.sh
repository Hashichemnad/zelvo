#!/usr/bin/env bash
# Writes the release manifest (format zelvo-release/1, task F6) from the components'
# descriptions made by component.sh:
#
#   scripts/release/manifest.sh <version> <components dir> > release-manifest.json
#
# The release pipeline then signs it with cosign (sign-blob, Zelvo's image-signing key).
set -euo pipefail
version=$1 dir=$2
jq -s --arg version "$version" --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '{format: "zelvo-release/1", version: $version, createdAt: $at, components: (sort_by(.name))}' "$dir"/*.json
