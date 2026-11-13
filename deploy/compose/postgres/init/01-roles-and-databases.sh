#!/bin/bash
# Runs once, when the PostgreSQL data volume is first created.
# Creates cluster-level roles and databases only; schema changes belong in migrations.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v db="$POSTGRES_DB" \
  -v app_user="$ZELVO_APP_DB_USER" -v app_password="$ZELVO_APP_DB_PASSWORD" \
  -v kc_user="$KEYCLOAK_DB_USER" -v kc_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
-- Application role: no superuser and no BYPASSRLS, so row-level security applies to it.
CREATE ROLE :"app_user" LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT CONNECT ON DATABASE :"db" TO :"app_user";
GRANT USAGE ON SCHEMA public TO :"app_user";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app_user";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :"app_user";

-- Keycloak keeps its own database.
CREATE ROLE :"kc_user" LOGIN PASSWORD :'kc_password';
CREATE DATABASE keycloak OWNER :"kc_user";
SQL
