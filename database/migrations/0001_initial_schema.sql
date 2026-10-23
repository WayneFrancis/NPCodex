-- NPCodex: initial schema
-- Target: PostgreSQL 17 with pgvector 0.8.7
-- Run once against the "npcodex" database. Future changes go in new migrations.
-- This migration is atomic; errors roll back all changes.
BEGIN;

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE campaigns (
    campaign_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name TEXT NOT NULL CHECK (btrim(name) <> ''),
    game_system TEXT,
    setting_name TEXT,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Hierarchy examples: kingdom -> city -> tavern -> private room.
CREATE TABLE locations (
    location_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    parent_location_id BIGINT,
    name TEXT NOT NULL CHECK (btrim(name) <> ''),
    location_type TEXT NOT NULL DEFAULT 'other',
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, location_id),
    CONSTRAINT fk_locations_parent_same_campaign
        FOREIGN KEY (campaign_id, parent_location_id)
        REFERENCES locations (campaign_id, location_id),
    CHECK (parent_location_id IS NULL OR parent_location_id <> location_id)
);

CREATE INDEX ix_locations_parent
    ON locations (campaign_id, parent_location_id);

CREATE TABLE factions (
    faction_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    name TEXT NOT NULL CHECK (btrim(name) <> ''),
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, faction_id)
);

-- Both NPCs and player characters, enabling shared relationships/interactions.
CREATE TABLE characters (
    character_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    character_type TEXT NOT NULL CHECK (character_type IN ('npc', 'pc')),
    name TEXT NOT NULL CHECK (btrim(name) <> ''),
    occupation TEXT,
    biography TEXT,
    dm_notes TEXT,
    current_location_id BIGINT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, character_id),
    CONSTRAINT fk_character_location_same_campaign
        FOREIGN KEY (campaign_id, current_location_id)
        REFERENCES locations (campaign_id, location_id)
);

CREATE INDEX ix_characters_campaign_location
    ON characters (campaign_id, current_location_id);

-- An NPC can own, work at, or live in several locations.
CREATE TABLE character_location_roles (
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    character_id BIGINT NOT NULL,
    location_id BIGINT NOT NULL,
    role TEXT NOT NULL CHECK (btrim(role) <> ''),
    notes TEXT,
    PRIMARY KEY (campaign_id, character_id, location_id, role),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters (campaign_id, character_id),
    FOREIGN KEY (campaign_id, location_id)
        REFERENCES locations (campaign_id, location_id)
);

CREATE INDEX ix_character_location_roles_location
    ON character_location_roles (campaign_id, location_id);

CREATE TABLE character_factions (
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    character_id BIGINT NOT NULL,
    faction_id BIGINT NOT NULL,
    role TEXT,
    notes TEXT,
    PRIMARY KEY (campaign_id, character_id, faction_id),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters (campaign_id, character_id),
    FOREIGN KEY (campaign_id, faction_id)
        REFERENCES factions (campaign_id, faction_id)
);

CREATE INDEX ix_character_factions_faction
    ON character_factions (campaign_id, faction_id);

-- Directed relationship: A's opinion of B need not equal B's opinion of A.
CREATE TABLE character_relationships (
    relationship_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    source_character_id BIGINT NOT NULL,
    target_character_id BIGINT NOT NULL,
    relationship_type TEXT NOT NULL CHECK (btrim(relationship_type) <> ''),
    disposition SMALLINT CHECK (disposition BETWEEN -100 AND 100),
    description TEXT,
    is_secret BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, source_character_id, target_character_id, relationship_type),
    FOREIGN KEY (campaign_id, source_character_id)
        REFERENCES characters (campaign_id, character_id),
    FOREIGN KEY (campaign_id, target_character_id)
        REFERENCES characters (campaign_id, character_id),
    CHECK (source_character_id <> target_character_id)
);

CREATE INDEX ix_character_relationships_target
    ON character_relationships (campaign_id, target_character_id);

CREATE TABLE interactions (
    interaction_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    location_id BIGINT,
    campaign_day INTEGER CHECK (campaign_day >= 0),
    summary TEXT NOT NULL CHECK (btrim(summary) <> ''),
    transcript TEXT,
    happened_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (campaign_id, interaction_id),
    FOREIGN KEY (campaign_id, location_id)
        REFERENCES locations (campaign_id, location_id)
);

CREATE INDEX ix_interactions_campaign_day
    ON interactions (campaign_id, campaign_day, happened_at DESC);

CREATE TABLE interaction_participants (
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    interaction_id BIGINT NOT NULL,
    character_id BIGINT NOT NULL,
    participation_role TEXT NOT NULL DEFAULT 'present',
    PRIMARY KEY (campaign_id, interaction_id, character_id),
    FOREIGN KEY (campaign_id, interaction_id)
        REFERENCES interactions (campaign_id, interaction_id),
    FOREIGN KEY (campaign_id, character_id)
        REFERENCES characters (campaign_id, character_id)
);

CREATE INDEX ix_interaction_participants_character
    ON interaction_participants (campaign_id, character_id);

-- A memory belongs to the character who knows/believes it.
-- Embeddings are NULL until the embedding pipeline processes a memory.
CREATE TABLE memories (
    memory_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (campaign_id),
    owner_character_id BIGINT NOT NULL,
    source_interaction_id BIGINT,
    content TEXT NOT NULL CHECK (btrim(content) <> ''),
    memory_type TEXT NOT NULL DEFAULT 'experience',
    importance SMALLINT NOT NULL DEFAULT 3
        CHECK (importance BETWEEN 1 AND 5),
    is_secret BOOLEAN NOT NULL DEFAULT FALSE,
    embedding VECTOR(768),
    embedding_model TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY (campaign_id, owner_character_id)
        REFERENCES characters (campaign_id, character_id),
    FOREIGN KEY (campaign_id, source_interaction_id)
        REFERENCES interactions (campaign_id, interaction_id),
    CONSTRAINT ck_memories_embedding_model
        CHECK ((embedding IS NULL) = (embedding_model IS NULL))
);

CREATE INDEX ix_memories_owner_created
    ON memories (campaign_id, owner_character_id, created_at DESC);

CREATE INDEX ix_memories_unembedded
    ON memories (memory_id) WHERE embedding IS NULL;

-- campaign_id NULL = shared reference material usable across campaigns.
-- Application access checks still determine which sources may be retrieved.
CREATE TABLE knowledge_sources (
    source_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id BIGINT REFERENCES campaigns (campaign_id),
    title TEXT NOT NULL CHECK (btrim(title) <> ''),
    source_type TEXT NOT NULL DEFAULT 'notes',
    source_uri TEXT,
    content_hash TEXT,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_knowledge_sources_campaign
    ON knowledge_sources (campaign_id);

CREATE TABLE knowledge_chunks (
    chunk_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_id BIGINT NOT NULL REFERENCES knowledge_sources (source_id),
    chunk_index INTEGER NOT NULL CHECK (chunk_index >= 0),
    content TEXT NOT NULL CHECK (btrim(content) <> ''),
    page_number INTEGER CHECK (page_number > 0),
    embedding VECTOR(768),
    embedding_model TEXT,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (source_id, chunk_index),
    CONSTRAINT ck_knowledge_chunks_embedding_model
        CHECK ((embedding IS NULL) = (embedding_model IS NULL))
);

-- Exact (not HNSW) vector search initially. Add a vector index later
-- if the number of embedded memories/chunks justifies it.
INSERT INTO schema_migrations (version) VALUES ('0001_initial_schema');

COMMIT;
