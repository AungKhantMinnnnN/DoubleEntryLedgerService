-- Runs once on a fresh Postgres volume (docker-entrypoint-initdb.d).
-- Migrations run as the owner (ledger); the service should run as ledger_app. Table grants come from the AuditHardening migration.
CREATE ROLE ledger_app LOGIN PASSWORD 'ledger_app';
