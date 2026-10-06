#!/bin/sh
# Runs once, when PostgreSQL first makes its data volume: never again, and never on a volume made before this was here.
# Makes the role TalkWatch signs in as, and its database, owned by that role. The role is not a superuser, so a fault
# in TalkWatch can reach its own data and nothing else on the server: no other database, no files, no programs.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres -v password="$(cat /run/secrets/Database__Password)" <<'SQL'
CREATE ROLE talkwatch LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'password';
CREATE DATABASE talkwatch OWNER talkwatch;
SQL
