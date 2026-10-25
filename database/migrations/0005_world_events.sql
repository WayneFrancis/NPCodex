-- NPCodex Phase 9: in-world chronology, NPC speakers and witnesses.
-- Apply once, AFTER 0004_character_aliases, using Execute SQL Script in DBeaver.
-- World day is a campaign-relative day number. World time is optional 24-hour
-- local fictional clock time, not the PC's actual timestamp.
BEGIN;

ALTER TABLE interactions
    ADD COLUMN world_day INTEGER CHECK (world_day >= 1),
    ADD COLUMN world_time TIME WITHOUT TIME ZONE;

ALTER TABLE memories
    ADD COLUMN world_day INTEGER CHECK (world_day >= 1),
    ADD COLUMN world_time TIME WITHOUT TIME ZONE;

-- Carry forward existing explicit campaign-day data without guessing times.
-- This also dates demo memories already linked to a day-numbered event.
UPDATE interactions
SET world_day = campaign_day
WHERE campaign_day >= 1 AND world_day IS NULL;

UPDATE memories m
SET world_day = i.world_day
FROM interactions i
WHERE m.campaign_id = i.campaign_id
  AND m.source_interaction_id = i.interaction_id
  AND m.world_day IS NULL
  AND i.world_day IS NOT NULL;

-- Older approved interactions have an unspecified speaker. New NPC-to-NPC
-- turns record the first speaker's character ID explicitly.
ALTER TABLE conversation_turns
    ADD COLUMN speaker_character_id BIGINT,
    ADD CONSTRAINT fk_turn_speaker_character
        FOREIGN KEY (campaign_id, speaker_character_id)
        REFERENCES characters (campaign_id, character_id);

CREATE INDEX ix_memories_world_day
    ON memories(campaign_id, owner_character_id, world_day DESC, world_time DESC);

-- Existing participation_role supports multiple listeners. The application
-- distinguishes reply NPC, initiating speaker, and hearing-only witnesses.
INSERT INTO schema_migrations(version) VALUES ('0005_world_events');

COMMIT;
