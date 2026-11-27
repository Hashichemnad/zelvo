#!/usr/bin/env bash
# Assembles the offline install bundle of a release (task F6):
#
#   scripts/release/bundle.sh <version> <tag> <artifacts dir> <bundle name>
#
# <artifacts dir> holds the release pipeline's artifacts (image-<name>/images/<name>.tar.gz and
# release-manifest/). Writes <bundle name>.tar with:
#   images/<name>.tar.gz        docker save archives (zelvo/<name>:<version>)
#   images/third-party/         the other images the Compose file uses (PostgreSQL's base, Valkey, …)
#   release/                    the signed release manifest (release-manifest.json and .sig)
#   compose/                    hardened Docker Compose for small installs (compose.production.yaml)
#   helm/zelvo-<version>.tgz    the Helm chart for medium and large installs
#   install.sh                  offline install or upgrade of a Compose installation
#   compose/add-tenant.sh       adds an organisation (tenant) with its host and first administrator
#   cosign.pub                  Zelvo's image-signing public key, to check images and the manifest
#   package-manifest.json       the input of zelvo-sign package for the update package (.zpkg)
#   SHA256SUMS
set -euo pipefail

version=$1 tag=$2 parts=$3 name=$4
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$PWD/$name"
rm -rf "$out" && mkdir -p "$out/images" "$out/release" "$out/helm"

images=()
for archive in "$parts"/image-*/images/*.tar.gz; do
  image=$(basename "$archive" .tar.gz)
  cp "$archive" "$out/images/"
  images+=("$image")
done
[[ ${#images[@]} -gt 0 ]] || { echo "bundle: no image archives in $parts" >&2; exit 1; }

cp "$parts/release-manifest/release-manifest.json" "$parts/release-manifest/release-manifest.json.sig" "$out/release/"
# The hardened Compose configuration and what it mounts.
mkdir -p "$out/compose"
cp "$repo/deploy/compose/compose.production.yaml" "$out/compose/compose.yaml"
cp "$repo/deploy/compose/.env.production.example" "$out/compose/.env.example"
for folder in caddy security postgres observability; do
  cp -r "$repo/deploy/compose/$folder" "$out/compose/$folder"
done
cp "$repo/deploy/compose/backup/restore.sh" "$repo/scripts/clamav-import-definitions.sh" "$out/compose/"

# Third-party images the Compose file uses, so air-gapped servers need no registry. The image
# list needs every required setting, so placeholder values stand in for them here.
mkdir -p "$out/images/third-party"
placeholders=$(mktemp)
sed -e 's#^ZELVO_COMPOSE_DIR=.*#ZELVO_COMPOSE_DIR=/opt/zelvo#' -e 's/=change-me$/=placeholder/' -e 's/^\([A-Z0-9_]*\)=$/\1=placeholder/' \
  "$out/compose/.env.example" > "$placeholders"
third_party=$(docker compose -f "$out/compose/compose.yaml" --env-file "$placeholders" --profile observability config --images \
  | { grep -v '^zelvo/' || true; } | sort -u)
rm -f "$placeholders"
for image in $third_party; do
  docker pull --quiet "$image" > /dev/null
  docker save "$image" | gzip -1 > "$out/images/third-party/$(echo "$image" | tr '/:' '__').tar.gz"
done
cp "$repo/scripts/release/install.sh" "$out/install.sh"
# Task M2: further organisations (tenants) on their own hosts, run in the installation folder.
cp "$repo/scripts/release/add-tenant.sh" "$out/compose/add-tenant.sh"
# Zelvo's image-signing public key; tests give a throwaway one in ZELVO_BUNDLE_PUBLIC_KEY.
if [[ -n "${ZELVO_BUNDLE_PUBLIC_KEY:-}" ]]; then
  cp "$ZELVO_BUNDLE_PUBLIC_KEY" "$out/cosign.pub"
else
  jq -er '.[0].publicKey' "$repo/src/api/Zelvo.Licensing/image-signing-keys.json" > "$out/cosign.pub"
fi

if command -v helm > /dev/null; then
  helm package "$repo/deploy/helm/zelvo" --version "$version" --app-version "$version" --destination "$out/helm" > /dev/null
else
  echo "bundle: helm not found, the bundle has no chart" >&2
fi

# The update package's manifest: the product owner fills in the type and the release notes,
# then signs on the signing computer: zelvo-sign package --manifest package-manifest.json --dir <bundle> --key ... --out zelvo-<version>.zpkg
jq -n --arg version "$version" '$ARGS.positional as $images | {
  version: $version,
  type: "Security",
  requiresVersion: null,
  releaseNotes: {en: "Describe the release in English.", ar: "صف الإصدار بالعربية."},
  expectedMinutes: 30,
  images: [$images[] | {reference: "zelvo/\(.):\($version)", file: "images/\(.).tar.gz"}]
}' --args "${images[@]}" > "$out/package-manifest.json"

(cd "$out" && find . -type f ! -name SHA256SUMS -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > SHA256SUMS)
tar -cf "$name.tar" -C "$(dirname "$out")" "$(basename "$out")"
echo "bundle: $name.tar (${#images[@]} images, release $version, tag $tag)"
