-- Phase 8: editable character names and nicknames (PCs and NPCs).
-- Run once AFTER 0003_npc_profiles as a complete script.
BEGIN;

CREATE TABLE character_aliases (
    alias_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL,
    character_id BIGINT NOT NULL,
    alias TEXT NOT NULL CHECK (btrim(alias) <> '' AND length(alias) <= 120),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters(campaign_id, character_id),
    UNIQUE (campaign_id, alias_id)
);

-- Two different characters must not share an alias within one campaign,
-- irrespective of case. Canonical names are checked for ambiguity by the
-- application (the original characters table does not require unique names).
CREATE UNIQUE INDEX ux_character_aliases_campaign_name
    ON character_aliases(campaign_id, lower(alias));

CREATE INDEX ix_character_aliases_owner
    ON character_aliases(campaign_id, character_id);

INSERT INTO schema_migrations(version) VALUES ('0004_character_aliases');

COMMIT;
