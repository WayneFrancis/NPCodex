-- Phase 10: statement-level hearing and D&D 5e-inspired character sheets.
-- Apply once after 0005_world_events.
BEGIN;

CREATE TABLE conversation_statements (
    statement_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns(campaign_id),
    interaction_id BIGINT NOT NULL,
    speaker_character_id BIGINT,
    world_day INTEGER CHECK (world_day >= 1),
    world_time TIME WITHOUT TIME ZONE,
    content TEXT NOT NULL CHECK (btrim(content) <> ''),
    is_private BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, statement_id),
    FOREIGN KEY (campaign_id, interaction_id)
        REFERENCES interactions(campaign_id, interaction_id),
    FOREIGN KEY (campaign_id, speaker_character_id)
        REFERENCES characters(campaign_id, character_id)
);
CREATE INDEX ix_statements_campaign_time ON conversation_statements
    (campaign_id, world_day DESC, world_time DESC, statement_id DESC);
CREATE INDEX ix_statements_search ON conversation_statements
    USING GIN (to_tsvector('simple', content));

CREATE TABLE statement_hearers (
    campaign_id BIGINT NOT NULL,
    statement_id BIGINT NOT NULL,
    character_id BIGINT NOT NULL,
    PRIMARY KEY (campaign_id, statement_id, character_id),
    FOREIGN KEY (campaign_id, statement_id)
        REFERENCES conversation_statements(campaign_id, statement_id),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters(campaign_id, character_id)
);
CREATE INDEX ix_statement_hearers_character ON statement_hearers
    (campaign_id, character_id, statement_id);

-- Convert approved older two-line exchanges into individually searchable
-- statements. No text is generated and no event dates are guessed.
INSERT INTO conversation_statements
    (campaign_id, interaction_id, speaker_character_id,
     world_day, world_time, content)
SELECT t.campaign_id, t.interaction_id,
       COALESCE(t.speaker_character_id, s.player_character_id),
       i.world_day, i.world_time, t.player_line
FROM conversation_turns t
JOIN conversation_sessions s
  ON s.campaign_id = t.campaign_id AND s.session_id = t.session_id
JOIN interactions i
  ON i.campaign_id = t.campaign_id AND i.interaction_id = t.interaction_id
UNION ALL
SELECT t.campaign_id, t.interaction_id, s.npc_id,
       i.world_day, i.world_time, t.npc_reply
FROM conversation_turns t
JOIN conversation_sessions s
  ON s.campaign_id = t.campaign_id AND s.session_id = t.session_id
JOIN interactions i
  ON i.campaign_id = t.campaign_id AND i.interaction_id = t.interaction_id;

-- Backfilled lines are heard by recorded participants; older data cannot
-- tell apart whispers, so no unrecorded hearers are invented.
INSERT INTO statement_hearers (campaign_id, statement_id, character_id)
SELECT DISTINCT st.campaign_id, st.statement_id, p.character_id
FROM conversation_statements st
JOIN interaction_participants p
  ON p.campaign_id = st.campaign_id AND p.interaction_id = st.interaction_id
WHERE p.character_id <> st.speaker_character_id
   OR st.speaker_character_id IS NULL
ON CONFLICT DO NOTHING;

CREATE TABLE dnd_character_sheets (
    campaign_id BIGINT NOT NULL,
    character_id BIGINT NOT NULL,
    species TEXT NOT NULL DEFAULT '',
    character_class TEXT NOT NULL DEFAULT '',
    level INTEGER NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 20),
    background TEXT NOT NULL DEFAULT '',
    alignment TEXT NOT NULL DEFAULT '',
    strength SMALLINT NOT NULL DEFAULT 10 CHECK (strength BETWEEN 1 AND 30),
    dexterity SMALLINT NOT NULL DEFAULT 10 CHECK (dexterity BETWEEN 1 AND 30),
    constitution SMALLINT NOT NULL DEFAULT 10 CHECK (constitution BETWEEN 1 AND 30),
    intelligence SMALLINT NOT NULL DEFAULT 10 CHECK (intelligence BETWEEN 1 AND 30),
    wisdom SMALLINT NOT NULL DEFAULT 10 CHECK (wisdom BETWEEN 1 AND 30),
    charisma SMALLINT NOT NULL DEFAULT 10 CHECK (charisma BETWEEN 1 AND 30),
    armor_class INTEGER NOT NULL DEFAULT 10,
    hit_points INTEGER NOT NULL DEFAULT 1,
    speed_feet INTEGER NOT NULL DEFAULT 30,
    features TEXT NOT NULL DEFAULT '',
    equipment TEXT NOT NULL DEFAULT '',
    notes TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (campaign_id, character_id),
    FOREIGN KEY (campaign_id, character_id)
       REFERENCES characters(campaign_id, character_id)
);

INSERT INTO schema_migrations(version) VALUES ('0006_conversation_browser');

COMMIT;
