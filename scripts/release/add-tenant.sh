#!/usr/bin/env bash
# Adds an organisation (tenant) to a hardened Compose installation (task M2). Run it as root in
# the installation folder (default /opt/zelvo):
#
#   sudo ./add-tenant.sh --slug city --name-en "City Municipality" --name-ar "بلدية المدينة" \
#     --host support.city.gov.ae --admin it.admin@city.gov.ae
#
# It runs migrate once for the new tenant: the tenant and its Keycloak realm, its own host (served
# as a verified custom domain: point the host's DNS at this server; Caddy gets its certificate),
# and its first administrator, whose one-time setup link it prints. Running it again for the same
# slug before the link is used prints a new link. Integrations of this tenant call the API with
# the header X-Zelvo-Tenant: <slug>.
set -euo pipefail

dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
slug="" name_en="" name_ar="" host="" admin=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --slug) slug=$2; shift 2 ;;
    --name-en) name_en=$2; shift 2 ;;
    --name-ar) name_ar=$2; shift 2 ;;
    --host) host=$2; shift 2 ;;
    --admin) admin=$2; shift 2 ;;
    --dir) dir=$2; shift 2 ;;
    *) echo "usage: $0 --slug <slug> --name-en <name> --name-ar <name> --host <host> --admin <email> [--dir <folder>]" >&2; exit 2 ;;
  esac
done

fail() { echo "add-tenant: $*" >&2; exit 1; }
[[ "$slug" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] || fail "--slug: lower-case letters, digits and hyphens, for example city"
[[ -n "$name_en" && -n "$name_ar" ]] || fail "--name-en and --name-ar are required (the organisation's name in English and Arabic)"
[[ "$host" =~ ^[a-z0-9.-]+\.[a-z]{2,}$ ]] || fail "--host: the organisation's own host name, for example support.city.gov.ae"
[[ "$admin" == *@*.* ]] || fail "--admin: the first administrator's email address"
[[ -f "$dir/compose.yaml" && -f "$dir/.env" ]] || fail "$dir is not a Zelvo installation folder (compose.yaml and .env)"

source_tenant=$(grep -E '^ZELVO_DEFAULT_TENANT=' "$dir/.env" | cut -d= -f2- || true)
[[ "$slug" != "${source_tenant:-default}" ]] || fail "$slug is the installation's first organisation; it was set up by install.sh"

log=$(mktemp)
trap 'rm -f "$log"' EXIT
# The tenant's settings are given on migrate's command line, which overrides the first
# organisation's settings from .env for this run only.
if ! (cd "$dir" && docker compose run --rm --no-TTY migrate migrate \
  "--Tenancy:Bootstrap:Slug=$slug" "--Tenancy:Bootstrap:NameEn=$name_en" "--Tenancy:Bootstrap:NameAr=$name_ar" \
  "--Tenancy:Bootstrap:Host=$host" "--Tenancy:Bootstrap:FirstAdminEmail=$admin") > "$log" 2>&1; then
  tail -n 30 "$log" >&2
  fail "migrate refused the new organisation (see above)"
fi

link=$(grep -o 'ZELVO-SETUP-LINK [^ "]*' "$log" | tail -n 1 | cut -d' ' -f2 || true)
echo "Organisation $slug is ready at https://$host/ (point its DNS at this server)."
if [[ -n "$link" ]]; then
  echo "First administrator ($admin): one-time link to choose the password, valid for 72 hours:"
  echo "  $link"
else
  echo "The organisation already has its administrator (no new setup link)."
fi
