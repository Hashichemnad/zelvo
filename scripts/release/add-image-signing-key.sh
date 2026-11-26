#!/usr/bin/env bash
# Adds Zelvo's cosign public key to both copies of the image-signing keys (task F6):
#
#   scripts/release/add-image-signing-key.sh <key id> cosign.pub
#
# Run it with the PUBLIC key only (cosign generate-key-pair writes cosign.key and cosign.pub on
# the product owner's computer; cosign.key and its password go into the GitHub environment
# "release" as COSIGN_PRIVATE_KEY and COSIGN_PASSWORD and are never committed).
set -euo pipefail
id=$1 pub=$2
if grep -q "PRIVATE KEY" "$pub"; then
  echo "add-image-signing-key: $pub is a private key; give the public key (cosign.pub)" >&2
  exit 1
fi
openssl pkey -pubin -in "$pub" -noout -text 2>/dev/null | grep -q "prime256v1\|P-256" || {
  echo "add-image-signing-key: $pub is not an ECDSA P-256 public key" >&2
  exit 1
}
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
for file in "$repo/src/api/Zelvo.Licensing/image-signing-keys.json" "$repo/src/web/src/lib/image-signing-keys.json"; do
  jq --arg id "$id" --rawfile key "$pub" '[.[] | select(.id != $id)] + [{id: $id, publicKey: $key}]' "$file" > "$file.new"
  mv "$file.new" "$file"
done
echo "Key $id added; commit both image-signing-keys.json files."
