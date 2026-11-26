#!/usr/bin/env bash
# Obfuscates Zelvo's own assemblies in a published .NET application (task F6).
#
#   build/obfuscar/obfuscate.sh <publish directory> [mapping file]
#
# Runs Obfuscar (MIT; the local tool in dotnet-tools.json) over every Zelvo.*.dll in the
# directory with the rules in build/obfuscar/rules.xml, replaces the assemblies in place and
# removes their debug symbols. The mapping file (old name -> new name) is written only when a
# path is given: the release pipeline keeps it with the build so we can read stack traces from
# customers; it never goes into an image.
set -euo pipefail

if [[ $# -lt 1 || ! -d "$1" ]]; then
  echo "usage: $0 <publish directory> [mapping file]" >&2
  exit 2
fi

publish=$(cd "$1" && pwd)
mapping=${2:-}
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

shopt -s nullglob
modules=("$publish"/Zelvo.*.dll)
if [[ ${#modules[@]} -eq 0 ]]; then
  echo "obfuscate: no Zelvo assemblies in $publish" >&2
  exit 1
fi

# Obfuscar resolves references from these folders: the application itself and the newest
# version of each shared framework (Microsoft.NETCore.App, Microsoft.AspNetCore.App).
dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
search_paths=("$publish")
for framework in "$dotnet_root"/shared/*/; do
  newest=$(ls -d "$framework"*/ 2>/dev/null | sort -V | tail -n 1)
  [[ -n "$newest" ]] && search_paths+=("${newest%/}")
done

rules=$(grep '<Skip' "$here/rules.xml")

{
  echo '<?xml version="1.0"?>'
  echo '<Obfuscator>'
  echo "  <Var name=\"InPath\" value=\"$publish\" />"
  echo "  <Var name=\"OutPath\" value=\"$work/out\" />"
  # Public members of our own assemblies are not an API anybody else compiles against.
  echo '  <Var name="KeepPublicApi" value="false" />'
  echo '  <Var name="HidePrivateApi" value="true" />'
  echo '  <Var name="RenameProperties" value="false" />'
  echo '  <Var name="RenameEvents" value="false" />'
  echo '  <Var name="HideStrings" value="true" />'
  echo '  <Var name="SuppressIldasm" value="true" />'
  for path in "${search_paths[@]}"; do
    echo "  <AssemblySearchPath path=\"$path\" />"
  done
  for module in "${modules[@]}"; do
    echo "  <Module file=\"$module\">"
    echo "$rules"
    echo '  </Module>'
  done
  echo '</Obfuscator>'
} > "$work/obfuscar.xml"

(cd "$repo" && dotnet tool restore >/dev/null && dotnet tool run obfuscar.console -- "$work/obfuscar.xml") > "$work/obfuscar.log" 2>&1 || {
  cat "$work/obfuscar.log" >&2
  exit 1
}

for module in "${modules[@]}"; do
  name=$(basename "$module")
  [[ -f "$work/out/$name" ]] || { echo "obfuscate: Obfuscar wrote no $name" >&2; exit 1; }
  cp "$work/out/$name" "$module"
  rm -f "${module%.dll}.pdb"
done

if [[ -n "$mapping" ]]; then
  mkdir -p "$(dirname "$mapping")"
  cp "$work/out/Mapping.txt" "$mapping"
fi

echo "obfuscate: ${#modules[@]} assemblies obfuscated in $publish"
