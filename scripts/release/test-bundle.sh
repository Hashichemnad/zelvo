#!/usr/bin/env bash
# Builds an install bundle the way the release pipeline does, for CI tests of the hardened
# install (task F6): development builds of every image (obfuscated .NET), the release manifest
# signed with a throwaway key made here (never Zelvo's), and bundle.sh.
#
#   scripts/release/test-bundle.sh <version> <output dir>
set -euo pipefail
version=$1 out=$(mkdir -p "$2" && cd "$2" && pwd)
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
parts="$out/parts"
rm -rf "$parts" && mkdir -p "$parts/release-manifest"

build() { # name dockerfile context app-dir [exclude]
  local name=$1 dockerfile=$2 context=$3 app=${4:-} exclude=${5:-}
  docker build --quiet --build-arg ZELVO_OBFUSCATE=true -f "$repo/$dockerfile" -t "zelvo/$name:$version" "$repo/$context" > /dev/null
  mkdir -p "$parts/image-$name/components" "$parts/image-$name/images"
  "$repo/scripts/release/component.sh" "$name" "zelvo/$name:$version" "registry.test/zelvo-$name" "sha256:$(printf '%064d' 0)" ${app:+"$app"} ${exclude:+$exclude} \
    > "$parts/image-$name/components/$name.json"
  docker save "zelvo/$name:$version" | gzip -1 > "$parts/image-$name/images/$name.tar.gz"
}

build api src/api/Zelvo.Api/Dockerfile . /app
build worker src/worker/Zelvo.Worker/Dockerfile . /app
build updater src/updater/Zelvo.Updater/Dockerfile . /app
build web src/web/Dockerfile src/web /app .next/cache
build keycloak src/keycloak/Dockerfile src/keycloak
build opensearch deploy/opensearch/Dockerfile deploy/opensearch
build postgres deploy/postgres/Dockerfile deploy/postgres

mkdir -p "$out/components"
cp "$parts"/image-*/components/*.json "$out/components/"
"$repo/scripts/release/manifest.sh" "$version" "$out/components" > "$parts/release-manifest/release-manifest.json"
key="$out/test-signing.key"
openssl ecparam -name prime256v1 -genkey -noout -out "$key"
openssl ec -in "$key" -pubout -out "$out/test-signing.pub" 2> /dev/null
openssl dgst -sha256 -sign "$key" "$parts/release-manifest/release-manifest.json" | base64 -w0 > "$parts/release-manifest/release-manifest.json.sig"
rm -f "$key"

(cd "$out" && ZELVO_BUNDLE_PUBLIC_KEY="$out/test-signing.pub" "$repo/scripts/release/bundle.sh" "$version" "$version" "$parts" "zelvo-$version")
