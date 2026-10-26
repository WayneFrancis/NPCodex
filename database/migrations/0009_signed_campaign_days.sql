-- NPCodex 0009: use signed campaign-relative days, including days before
-- the campaign began. Apply ONCE after 0008_conversation_maintenance.sql.
-- No timestamps, memories or dialogue are deleted or rewritten.
BEGIN;

DO $migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM schema_migrations
        WHERE version = '0008_conversation_maintenance'
    ) THEN
        RAISE EXCEPTION 'Apply 0008_conversation_maintenance first';
    END IF;
END
$migration$;

-- These legacy CHECK constraints were added by 0001/0005/0006/0008.
-- A negative day must be allowed for interactions, memories, individual
-- statements, and audit rows. Keep nullable/foreign-key rules unchanged.
ALTER TABLE interactions
    DROP CONSTRAINT IF EXISTS interactions_campaign_day_check,
    DROP CONSTRAINT IF EXISTS interactions_world_day_check;
ALTER TABLE memories
    DROP CONSTRAINT IF EXISTS memories_world_day_check;
ALTER TABLE conversation_statements
    DROP CONSTRAINT IF EXISTS conversation_statements_world_day_check;
ALTER TABLE conversation_time_edits
    DROP CONSTRAINT IF EXISTS conversation_time_edits_new_world_day_check;

-- Assert no single-column nonnegative day constraint remains under an
-- unexpected name; fail/ROLLBACK rather than claim the migration works.
DO $validation$
DECLARE restrictive RECORD;
BEGIN
    FOR restrictive IN
        SELECT cls.relname AS table_name, att.attname AS day_column,
               con.conname AS constraint_name
        FROM pg_constraint con
        JOIN pg_class cls ON cls.oid = con.conrelid
        JOIN pg_namespace ns ON ns.oid = cls.relnamespace
        JOIN pg_attribute att ON att.attrelid = cls.oid
          AND att.attnum = ANY(con.conkey)
        WHERE ns.nspname = current_schema()
          AND con.contype = 'c'
          AND (cls.relname, att.attname) IN (
            ('interactions','campaign_day'),
            ('interactions','world_day'),
            ('memories','world_day'),
            ('conversation_statements','world_day'),
            ('conversation_time_edits','new_world_day')
          )
          AND pg_get_constraintdef(con.oid) ~ '>= *[01][)]'
    LOOP
        RAISE EXCEPTION 'Remaining nonnegative-day constraint %.%: %',
            restrictive.table_name, restrictive.day_column,
            restrictive.constraint_name;
    END LOOP;
END
$validation$;

INSERT INTO schema_migrations(version)
VALUES ('0009_signed_campaign_days')
ON CONFLICT (version) DO NOTHING;

COMMIT;
