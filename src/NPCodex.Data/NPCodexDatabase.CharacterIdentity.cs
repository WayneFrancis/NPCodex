using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasCharacterAliasSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0004_character_aliases')");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlyList<CharacterAlias>> GetCharacterAliasesAsync(
        long campaignId, long characterId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        const string sql = """
            SELECT alias_id, character_id, alias
            FROM character_aliases
            WHERE campaign_id = $1 AND character_id = $2
            ORDER BY lower(alias), alias_id
            """;
        var aliases = new List<CharacterAlias>();
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(characterId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            aliases.Add(new CharacterAlias(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        return aliases;
    }

    /// <summary>
    /// Add an alias only when it cannot impersonate a canonical name or
    /// another alias in the same campaign. Aliases are explicitly DM-managed.
    /// </summary>
    public async Task<long> AddCharacterAliasAsync(
        long campaignId, long characterId, string alias,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Enter a nickname or alias.", nameof(alias));
        alias = alias.Trim();
        if (alias.Length > 120)
            throw new ArgumentException("Aliases must be 120 characters or less.", nameof(alias));

        const string sql = """
            INSERT INTO character_aliases (campaign_id, character_id, alias)
            SELECT $1, c.character_id, $3
            FROM characters c
            WHERE c.campaign_id = $1 AND c.character_id = $2
              AND NOT EXISTS (
                SELECT 1 FROM characters existing
                WHERE existing.campaign_id = $1
                  AND lower(existing.name) = lower($3)
              )
            ON CONFLICT DO NOTHING
            RETURNING alias_id
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(alias);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is long newId ? newId
            : throw new InvalidOperationException(
                "Alias could not be saved. Check that the character exists and " +
                "the name is not already used as a character name or alias in this campaign.");
    }

    public async Task<bool> DeleteCharacterAliasAsync(
        long campaignId, long characterId, long aliasId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0 || aliasId <= 0)
            throw new ArgumentOutOfRangeException(nameof(aliasId));
        const string sql = """
            DELETE FROM character_aliases
            WHERE campaign_id = $1 AND character_id = $2 AND alias_id = $3
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.AddWithValue(aliasId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Names (including aliases) used solely for unambiguous reference
    /// matching. Never includes biographies, DM notes or private opinions.
    /// </summary>
    public async Task<CampaignCharacterNames> GetCampaignCharacterNamesAsync(
        long campaignId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        var characters = new List<CharacterNameEntry>();
        var aliases = new List<CharacterAlias>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
            "SELECT character_id, name FROM characters WHERE campaign_id = $1", connection))
        {
            command.Parameters.AddWithValue(campaignId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                characters.Add(new CharacterNameEntry(reader.GetInt64(0), reader.GetString(1)));
        }

        await using (var command = new NpgsqlCommand(
            "SELECT alias_id, character_id, alias FROM character_aliases WHERE campaign_id = $1",
            connection))
        {
            command.Parameters.AddWithValue(campaignId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                aliases.Add(new CharacterAlias(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return new CampaignCharacterNames(characters, aliases);
    }

    /// <summary>Directed attitudes held by one NPC, including NPC-to-NPC.</summary>
    public async Task<IReadOnlyList<NpcRelationship>> GetNpcRelationshipsAsync(
        long campaignId, long npcId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0) throw new ArgumentOutOfRangeException(nameof(npcId));
        const string sql = """
            SELECT t.character_id, t.name, t.character_type,
                   r.disposition, r.description, r.is_secret
            FROM character_relationships r
            JOIN characters n ON n.campaign_id = r.campaign_id
                AND n.character_id = r.source_character_id
                AND n.character_type = 'npc'
            JOIN characters t ON t.campaign_id = r.campaign_id
                AND t.character_id = r.target_character_id
            WHERE r.campaign_id = $1 AND r.source_character_id = $2
              AND r.relationship_type = 'attitude'
            ORDER BY t.name, t.character_id
            """;
        var result = new List<NpcRelationship>();
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(npcId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new NpcRelationship(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? (short)0 : reader.GetInt16(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetBoolean(5)));
        return result;
    }
}
