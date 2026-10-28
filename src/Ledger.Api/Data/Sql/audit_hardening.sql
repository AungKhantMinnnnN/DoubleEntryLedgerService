-- Applied by the AuditHardening migration (embedded resource).
-- Row triggers don't fire for TRUNCATE, so block it separately.
CREATE TRIGGER audit_log_no_truncate
  BEFORE TRUNCATE ON "AuditLog"
  FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

-- The service connects as ledger_app (created outside migrations: db/init.sql, or by the DBA).
-- It may read and write everything except change history in the audit log.
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ledger_app') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ledger_app;
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ledger_app;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ledger_app;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO ledger_app;
    REVOKE UPDATE, DELETE, TRUNCATE ON "AuditLog" FROM ledger_app;
  END IF;
END $$;
