using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Finds only an NPC in the requested campaign, with basic role/location
    /// information. Deliberately excludes DM-only notes and unreviewed biography.
    /// </summary>
    public async Task<NpcChatProfile?> GetNpcChatProfileAsync(
        long campaignId, long npcId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0)
            throw new ArgumentOutOfRangeException(nameof(npcId));

        const string sql = """
            SELECT c.character_id,
                   c.name,
                   c.occupation,
                   l.name AS current_location,
                   (
                     SELECT string_agg(r.role || ' of ' || lr.name, '; ' ORDER BY lr.name, r.role)
                     FROM character_location_roles r
                     JOIN locations lr
                       ON lr.campaign_id = r.campaign_id
                      AND lr.location_id = r.location_id
                     WHERE r.campaign_id = c.campaign_id
                       AND r.character_id = c.character_id
                   ) AS location_roles
            FROM characters c
            LEFT JOIN locations l
              ON l.campaign_id = c.campaign_id
             AND l.location_id = c.current_location_id
            WHERE c.campaign_id = $1
              AND c.character_id = $2
              AND c.character_type = 'npc'
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new NpcChatProfile(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }
    /// <summary>
    /// Resolve the exact player-character name in the selected campaign.
    /// A saved relationship is optional; speaker identity is not.
    /// </summary>
    public async Task<string> GetPlayerCharacterNameAsync(
        long campaignId, long playerCharacterId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (playerCharacterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerCharacterId));

        const string sql = """
            SELECT name
            FROM characters
            WHERE campaign_id = $1 AND character_id = $2
              AND character_type = 'pc'
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(playerCharacterId);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException(
                $"Player character #{playerCharacterId} was not found in campaign #{campaignId}.");
    }

}
