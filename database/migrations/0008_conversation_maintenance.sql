-- Conversation timestamp corrections and optional semantic transcript search.
-- Apply once after 0007_memory_provenance.sql.
BEGIN;

ALTER TABLE conversation_statements
    ADD COLUMN embedding VECTOR(768),
    ADD COLUMN embedding_model TEXT,
    ADD CONSTRAINT ck_statement_embedding_model
        CHECK ((embedding IS NULL) = (embedding_model IS NULL));

CREATE INDEX ix_statements_need_embedding
    ON conversation_statements(campaign_id, statement_id)
    WHERE embedding IS NULL;

CREATE TABLE conversation_time_edits (
    edit_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL,
    interaction_id BIGINT NOT NULL,
    old_world_day INTEGER,
    old_world_time TIME WITHOUT TIME ZONE,
    new_world_day INTEGER NOT NULL CHECK (new_world_day >= 1),
    new_world_time TIME WITHOUT TIME ZONE NOT NULL,
    edited_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY (campaign_id, interaction_id)
        REFERENCES interactions(campaign_id, interaction_id)
);
CREATE INDEX ix_conversation_time_edits_interaction
    ON conversation_time_edits(campaign_id, interaction_id, edited_at DESC);

INSERT INTO schema_migrations(version)
VALUES ('0008_conversation_maintenance');

COMMIT;
