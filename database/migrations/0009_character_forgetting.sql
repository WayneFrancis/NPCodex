-- Phase 12: optional character-level forgetting of historical exchanges.
-- Apply after 0008_conversation_maintenance.sql.
-- Only IDs are stored here, never deleted memory text or embeddings.
BEGIN;

CREATE TABLE character_forgotten_exchanges (
    campaign_id BIGINT NOT NULL,
    character_id BIGINT NOT NULL,
    interaction_id BIGINT NOT NULL,
    forgotten_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (campaign_id, character_id, interaction_id),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters(campaign_id, character_id) ON DELETE CASCADE,
    FOREIGN KEY (campaign_id, interaction_id)
        REFERENCES interactions(campaign_id, interaction_id) ON DELETE CASCADE
);

CREATE INDEX ix_forgotten_exchanges_character
    ON character_forgotten_exchanges(campaign_id, character_id);

INSERT INTO schema_migrations(version)
VALUES ('0009_character_forgetting');

COMMIT;
