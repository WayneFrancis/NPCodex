-- Phase 11: distinguish world facts, recollections, beliefs, rumours and
-- explicitly DM-authored cover stories without changing historical text.
-- Apply once AFTER 0006_conversation_browser.sql.
BEGIN;

ALTER TABLE memories
    ADD COLUMN knowledge_status TEXT NOT NULL DEFAULT 'recollection'
        CONSTRAINT ck_memories_knowledge_status
        CHECK (knowledge_status IN (
            'verified', 'recollection', 'belief', 'rumour', 'cover_story'
        )),
    ADD COLUMN knowledge_source TEXT;

ALTER TABLE memories
    ADD CONSTRAINT ck_memories_knowledge_source_length
        CHECK (knowledge_source IS NULL OR length(knowledge_source) <= 300);

-- The historical records are memories, not automatically verified facts.
-- Existing NPC and witness memories retain their content and embeddings.
INSERT INTO schema_migrations(version)
    VALUES ('0007_memory_provenance');

COMMIT;
