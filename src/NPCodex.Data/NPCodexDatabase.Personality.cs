using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasPersonalitySchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0003_npc_profiles')");
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Read saved personality; empty fields are valid for an unconfigured NPC.</summary>
    public async Task<NpcPersonality> GetNpcPersonalityAsync(
        long campaignId, long npcId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0) throw new ArgumentOutOfRangeException(nameof(npcId));

        const string sql = """
            SELECT p.personality_traits, p.speaking_style, p.mannerisms,
                   p.motivations, p.private_dm_notes
            FROM npc_profiles p
            JOIN characters c ON c.campaign_id = p.campaign_id AND c.character_id = p.npc_id
            WHERE p.campaign_id = $1 AND p.npc_id = $2 AND c.character_type = 'npc'
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(npcId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return NpcPersonality.Empty;
        return new NpcPersonality(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
    }

    /// <summary>Only an NPC in the chosen campaign can own a personality.</summary>
    public async Task SaveNpcPersonalityAsync(
        long campaignId, long npcId, NpcPersonality personality,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0) throw new ArgumentOutOfRangeException(nameof(npcId));
        ArgumentNullException.ThrowIfNull(personality);
        CheckLength(personality.PersonalityTraits, 1000, nameof(personality.PersonalityTraits));
        CheckLength(personality.SpeakingStyle, 1000, nameof(personality.SpeakingStyle));
        CheckLength(personality.Mannerisms, 1000, nameof(personality.Mannerisms));
        CheckLength(personality.Motivations, 1000, nameof(personality.Motivations));
        CheckLength(personality.PrivateDmNotes, 4000, nameof(personality.PrivateDmNotes));

        const string sql = """
            INSERT INTO npc_profiles
                (campaign_id, npc_id, personality_traits, speaking_style,
                 mannerisms, motivations, private_dm_notes)
            SELECT $1, c.character_id, $3, $4, $5, $6, $7
            FROM characters c
            WHERE c.campaign_id = $1 AND c.character_id = $2 AND c.character_type = 'npc'
            ON CONFLICT (campaign_id, npc_id) DO UPDATE
            SET personality_traits = EXCLUDED.personality_traits,
                speaking_style = EXCLUDED.speaking_style,
                mannerisms = EXCLUDED.mannerisms,
                motivations = EXCLUDED.motivations,
                private_dm_notes = EXCLUDED.private_dm_notes,
                updated_at = now()
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(npcId);
        cmd.Parameters.AddWithValue(personality.PersonalityTraits);
        cmd.Parameters.AddWithValue(personality.SpeakingStyle);
        cmd.Parameters.AddWithValue(personality.Mannerisms);
        cmd.Parameters.AddWithValue(personality.Motivations);
        cmd.Parameters.AddWithValue(personality.PrivateDmNotes);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("NPC not found in the selected campaign.");
    }

    /// <summary>
    /// An NPC's disposition toward another character. The reverse
    /// opinion is a separate record, and is never implicitly assumed.
    /// </summary>
    public async Task<NpcAttitude?> GetNpcAttitudeAsync(
        long campaignId, long npcId, long targetCharacterId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || targetCharacterId <= 0 || npcId == targetCharacterId)
            throw new ArgumentException("Select two different, valid characters.");

        const string sql = """
            SELECT t.character_id, t.name, r.disposition, r.description, r.is_secret
            FROM character_relationships r
            JOIN characters n ON n.campaign_id = r.campaign_id
                             AND n.character_id = r.source_character_id
                             AND n.character_type = 'npc'
            JOIN characters t ON t.campaign_id = r.campaign_id
                             AND t.character_id = r.target_character_id
                             AND t.character_type IN ('pc', 'npc')
            WHERE r.campaign_id = $1 AND r.source_character_id = $2
              AND r.target_character_id = $3 AND r.relationship_type = 'attitude'
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(npcId);
        cmd.Parameters.AddWithValue(targetCharacterId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new NpcAttitude(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3), reader.GetBoolean(4))
            : null;
    }

    public async Task SaveNpcAttitudeAsync(
        long campaignId, long npcId, long targetCharacterId,
        short disposition, string description, bool isSecret,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || targetCharacterId <= 0 || npcId == targetCharacterId)
            throw new ArgumentException("Select an NPC and a different player character.");
        if (disposition is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(disposition));
        CheckLength(description, 1200, nameof(description));

        const string sql = """
            INSERT INTO character_relationships
                (campaign_id, source_character_id, target_character_id,
                 relationship_type, disposition, description, is_secret)
            SELECT $1, ntarget.character_id, target.character_id,
                   'attitude', $4, $5, $6
            FROM characters npc
            JOIN characters target ON target.campaign_id = npc.campaign_id
            WHERE npc.campaign_id = $1 AND npc.character_id = $2
              AND npc.character_type = 'npc'
              AND target.character_id = $3 AND target.character_type IN ('npc', 'pc')
            ON CONFLICT (campaign_id, source_character_id, target_character_id,
                         relationship_type) DO UPDATE
            SET disposition = EXCLUDED.disposition,
                description = EXCLUDED.description,
                is_secret = EXCLUDED.is_secret
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(npcId);
        cmd.Parameters.AddWithValue(targetCharacterId);
        cmd.Parameters.AddWithValue(disposition);
        cmd.Parameters.AddWithValue(description.Trim());
        cmd.Parameters.AddWithValue(isSecret);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("NPC or target character missing from the selected campaign.");
    }

    private static void CheckLength(string text, int maximum, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > maximum)
            throw new ArgumentException($"The {name} field cannot exceed {maximum} characters.", name);
    }
}
