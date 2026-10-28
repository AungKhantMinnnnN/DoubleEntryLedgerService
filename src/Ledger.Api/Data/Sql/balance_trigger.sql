-- Applied by the BalanceAndAuditTriggers migration (embedded resource).
-- Debits must equal credits per entry and currency, checked at commit.
CREATE OR REPLACE FUNCTION check_entry_balanced() RETURNS trigger AS $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM "JournalLines"
    WHERE "EntryId" = NEW."EntryId"
    GROUP BY "Currency"
    HAVING SUM(CASE WHEN "Direction" = 'Debit' THEN "Amount" ELSE -"Amount" END) <> 0
  ) THEN
    RAISE EXCEPTION 'journal entry % is unbalanced', NEW."EntryId";
  END IF;
  RETURN NULL;
END $$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER journal_lines_balanced
  AFTER INSERT ON "JournalLines"
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION check_entry_balanced();

-- Append-only audit log.
CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'audit_log is append-only'; END $$ LANGUAGE plpgsql;

CREATE TRIGGER audit_log_no_change
  BEFORE UPDATE OR DELETE ON "AuditLog"
  FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
