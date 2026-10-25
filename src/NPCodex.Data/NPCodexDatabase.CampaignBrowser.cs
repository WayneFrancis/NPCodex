using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Read only the location/character hierarchy, not every conversation,
    /// interaction or memory. Scales with the campaign cast rather than
    /// the amount of dialogue played over many sessions.
    /// </summary>
    public async Task<CampaignBrowser> GetCampaignBrowserAsync(
        long campaignId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        var locations = new List<LocationInfo>();
        var characters = new List<CharacterInfo>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            SELECT location_id, parent_location_id, name, location_type, description
            FROM locations WHERE campaign_id = $1
            ORDER BY name, location_id
            """, connection))
        {
            command.Parameters.AddWithValue(campaignId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                locations.Add(new LocationInfo(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT character_id, character_type, name, occupation, current_location_id
            FROM characters WHERE campaign_id = $1
            ORDER BY name, character_id
            """, connection))
        {
            command.Parameters.AddWithValue(campaignId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                characters.Add(new CharacterInfo(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return new CampaignBrowser(campaignId, locations, characters);
    }

    /// <summary>Update occupation independently of the character's D&D class.</summary>
    public async Task UpdateCharacterOccupationAsync(
        long campaignId, long characterId, string? occupation,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        occupation = string.IsNullOrWhiteSpace(occupation) ? null : occupation.Trim();
        if (occupation is { Length: > 200 })
            throw new ArgumentException("Occupation/role cannot exceed 200 characters.");

        const string sql = """
            UPDATE characters
            SET occupation = $3
            WHERE campaign_id = $1 AND character_id = $2
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)occupation ?? DBNull.Value
        });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                "Character not found in the selected campaign.");
    }

    public async Task MoveCharacterAsync(
        long campaignId, long characterId, long? locationId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        const string sql = """
            UPDATE characters
            SET current_location_id = $3
            WHERE campaign_id = $1 AND character_id = $2
              AND ($3::bigint IS NULL OR EXISTS (
                  SELECT 1 FROM locations WHERE campaign_id = $1
                    AND location_id = $3))
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(characterId);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
            Value = (object?)locationId ?? DBNull.Value
        });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Character or destination location was not found.");
    }
}
