-- NPCodex 0002: resumable approved conversations (separate from NPC memories)
-- Apply once in DBeaver after 0001_initial_schema, as a complete script.
BEGIN;

CREATE TABLE conversation_sessions (
    session_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns(campaign_id),
    npc_id BIGINT NOT NULL,
    player_character_id BIGINT,
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at TIMESTAMPTZ,
    UNIQUE (campaign_id, session_id),
    FOREIGN KEY (campaign_id, npc_id)
        REFERENCES characters(campaign_id, character_id),
    FOREIGN KEY (campaign_id, player_character_id)
        REFERENCES characters(campaign_id, character_id),
    CHECK (player_character_id IS NULL OR player_character_id <> npc_id),
    CHECK (ended_at IS NULL OR ended_at >= started_at)
);

CREATE INDEX ix_conversation_sessions_open
    ON conversation_sessions (campaign_id, npc_id, started_at DESC, session_id DESC)
    WHERE ended_at IS NULL;

-- Keep approved conversational speech separate from a character's long-term
-- memories. Each turn is also linked to the existing audit/event record.
CREATE TABLE conversation_turns (
    turn_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns(campaign_id),
    session_id BIGINT NOT NULL,
    interaction_id BIGINT NOT NULL,
    player_line TEXT NOT NULL CHECK (btrim(player_line) <> ''),
    npc_reply TEXT NOT NULL CHECK (btrim(npc_reply) <> ''),
    approved_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, interaction_id),
    FOREIGN KEY (campaign_id, session_id)
        REFERENCES conversation_sessions(campaign_id, session_id),
    FOREIGN KEY (campaign_id, interaction_id)
        REFERENCES interactions(campaign_id, interaction_id)
);

CREATE INDEX ix_conversation_turns_order
    ON conversation_turns (campaign_id, session_id, turn_id DESC);

INSERT INTO schema_migrations(version) VALUES ('0002_conversations');

COMMIT;
