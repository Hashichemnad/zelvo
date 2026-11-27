#!/usr/bin/env bash
# Imports ClamAV malware definitions into an installation without internet access (task B1,
# product owner decision 2026-11-22). Since task M1 administrators import them in Settings →
# Updates → Malware definitions, which Zelvo serves to ClamAV as its private mirror; this script
# stays for operators (for example when no administrator can sign in yet).
#
# 1. On a computer with internet access, download the current definitions, for example with
#    `freshclam --datadir=<folder>` or ClamAV's cvdupdate tool. You get main.cvd, daily.cvd
#    (or .cld) and bytecode.cvd, each with a .sign file.
# 2. Copy that folder to the Zelvo server and run:
#      scripts/clamav-import-definitions.sh <folder>
#
# Every file is checked with ClamAV's sigtool before it replaces the current definitions; then
# ClamAV reloads them without stopping. Files copied here are kept: freshclam replaces them only
# with newer ones (imported in Settings, or downloaded with "Download definitions automatically").
set -euo pipefail

source_dir="${1:-}"
if [[ -z "$source_dir" || ! -d "$source_dir" ]]; then
  echo "Usage: $0 <folder with main.cvd, daily.cvd or daily.cld, bytecode.cvd>" >&2
  exit 2
fi

cd "$(dirname "$0")/../deploy/compose"
compose=(docker compose)
[[ -n "${COMPOSE_FILE:-}" ]] || compose+=(-f compose.yaml)

shopt -s nullglob
files=("$source_dir"/*.cvd "$source_dir"/*.cld "$source_dir"/*.cdiff "$source_dir"/*.sign)
if [[ ${#files[@]} -eq 0 ]]; then
  echo "No definition files (.cvd, .cld, .cdiff, .sign) in $source_dir." >&2
  exit 1
fi
for required in main bytecode; do
  if [[ ! -f "$source_dir/$required.cvd" && ! -f "$source_dir/$required.cld" ]]; then
    echo "$required.cvd (or .cld) is missing in $source_dir." >&2
    exit 1
  fi
done

staging=/tmp/zelvo-definitions
"${compose[@]}" exec -T clamav sh -c "rm -rf $staging && mkdir -p $staging"
for file in "${files[@]}"; do
  "${compose[@]}" cp "$file" "clamav:$staging/"
done

# Check every database file, then move them into place and reload.
"${compose[@]}" exec -T clamav sh -euc "
  for db in $staging/*.cvd $staging/*.cld; do
    [ -e \"\$db\" ] || continue
    sigtool --info \"\$db\" > /dev/null || { echo \"\$db is not a valid ClamAV database.\" >&2; exit 1; }
  done
  chown clamav:clamav $staging/*
  mv $staging/* /var/lib/clamav/
  rm -rf $staging
  clamdscan --reload
"
echo "Imported ${#files[@]} files; ClamAV reloaded its definitions."
"${compose[@]}" exec -T clamav sh -c "sigtool --info /var/lib/clamav/daily.c[lv]d | grep -E '^(Build time|Version)'"
