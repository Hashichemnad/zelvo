#!/usr/bin/env bash
# Describes one built release image for the release manifest (task F6):
#
#   scripts/release/component.sh <name> <local image> <published image> <registry digest> [<app dir> [<exclude>...]]
#
# prints the component as JSON: its name, published image, registry digest, image id and, when an
# application directory is given, the hash of the files in it, computed like
# ReleaseIntegrity.HashDirectory (SHA-256 over "sha256  ./path" lines sorted by byte order), with
# the paths under <exclude> (relative to the app dir) left out. The services check that hash
# against their own files when they start.
set -euo pipefail

name=$1 local_image=$2 image=$3 digest=$4 app_dir=${5:-}
shift 4 || true
[[ $# -gt 0 ]] && shift
excludes=("$@")

image_id=$(docker image inspect --format '{{.Id}}' "$local_image")

if [[ -z "$app_dir" ]]; then
  jq -n --arg name "$name" --arg image "$image" --arg digest "$digest" --arg imageId "$image_id" \
    '{name: $name, image: $image, digest: $digest, imageId: $imageId}'
  exit 0
fi

work=$(mktemp -d)
container=$(docker create "$local_image")
trap 'docker rm -f "$container" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
docker cp "$container:$app_dir/." "$work/"

# Symbolic links would be counted differently by find and the services' own walk: refuse them.
if [[ -n "$(find "$work" -type l -print -quit)" ]]; then
  echo "component: $local_image has symbolic links under $app_dir:" >&2
  find "$work" -type l >&2
  exit 1
fi

for exclude in "${excludes[@]}"; do
  rm -rf "${work:?}/${exclude#/}"
done

files_sha=$(cd "$work" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum | sha256sum | cut -d' ' -f1)
jq -n --arg name "$name" --arg image "$image" --arg digest "$digest" --arg imageId "$image_id" \
  --arg files "$files_sha" --arg dir "$app_dir" '$ARGS.positional as $exclude
  | {name: $name, image: $image, digest: $digest, imageId: $imageId, filesSha256: $files, appDirectory: $dir}
  + (if ($exclude | length) > 0 then {exclude: $exclude} else {} end)' --args "${excludes[@]}"
