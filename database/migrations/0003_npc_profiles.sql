-- NPCodex Phase 7: persona and speaking style.
-- Relationships already use the character_relationships table from 0001.
-- Apply once after 0002_conversations, as a complete script in DBeaver.
BEGIN;

CREATE TABLE npc_profiles (
    campaign_id BIGINT NOT NULL,
    npc_id BIGINT NOT NULL,
    personality_traits TEXT NOT NULL DEFAULT '',
    speaking_style TEXT NOT NULL DEFAULT '',
    mannerisms TEXT NOT NULL DEFAULT '',
    motivations TEXT NOT NULL DEFAULT '',
    private_dm_notes TEXT NOT NULL DEFAULT '',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (campaign_id, npc_id),
    FOREIGN KEY (campaign_id, npc_id)
        REFERENCES characters(campaign_id, character_id)
);

INSERT INTO schema_migrations(version) VALUES ('0003_npc_profiles');

COMMIT;
