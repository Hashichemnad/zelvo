-- Roles and databases Zelvo needs in a PostgreSQL it does not create itself (Kubernetes installs,
-- task F6); the same as deploy/compose/postgres/init for Compose installs. Run as a superuser:
--   psql -v app_user=zelvo_app -v app_password=... -v kc_user=keycloak -v kc_password=... \
--        -v db=zelvo -v owner=zelvo_owner -f database-roles.sql
-- The application role has no BYPASSRLS, so row-level security always applies to it.
CREATE ROLE :"app_user" LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT CONNECT ON DATABASE :"db" TO :"app_user";
\connect :"db"
GRANT USAGE ON SCHEMA public TO :"app_user";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app_user";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO :"app_user";
CREATE ROLE :"kc_user" LOGIN PASSWORD :'kc_password';
CREATE DATABASE keycloak OWNER :"kc_user";
