using Npgsql;
using NpgsqlTypes;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>Resume the latest unfinished conversation for this exact NPC/player pairing.</summary>
    public async Task<ConversationSession?> FindOpenConversationAsync(
        long campaignId, long npcId, long? playerCharacterId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || playerCharacterId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(npcId));

        const string sql = """
            SELECT session_id, campaign_id, npc_id, player_character_id, started_at
            FROM conversation_sessions
            WHERE campaign_id = $1 AND npc_id = $2
              AND player_character_id IS NOT DISTINCT FROM $3::bigint
              AND ended_at IS NULL
            ORDER BY started_at DESC, session_id DESC
            LIMIT 1
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        command.Parameters.Add(new NpgsqlParameter {
            NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)playerCharacterId ?? DBNull.Value
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ConversationSession(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetDateTime(4));
    }

    /// <summary>
    /// Loads an entire approved conversation for DM inspection; the AI caller
    /// should pass only a bounded tail of this history.
    /// </summary>
    public async Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(
        long campaignId, long npcId, long conversationId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || conversationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversationId));

        const string sql = """
            SELECT t.turn_id, t.interaction_id, t.player_line,
                   t.npc_reply, t.approved_at,
                   t.speaker_character_id, i.world_day, i.world_time
            FROM conversation_turns t
            JOIN interactions i ON i.campaign_id = t.campaign_id
                               AND i.interaction_id = t.interaction_id
            INNER JOIN conversation_sessions s
                ON s.campaign_id = t.campaign_id AND s.session_id = t.session_id
            WHERE t.campaign_id = $1 AND t.session_id = $2 AND s.npc_id = $3
            ORDER BY t.turn_id
            """;
        var result = new List<ConversationTurn>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(conversationId);
        command.Parameters.AddWithValue(npcId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ConversationTurn(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetDateTime(4))
            {
                SpeakerCharacterId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                WorldDay = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                WorldTime = reader.IsDBNull(7) ? null : reader.GetFieldValue<TimeOnly>(7)
            });
        return result;
    }

    /// <summary>Explicitly closes a session; its approved transcript is preserved.</summary>
    public async Task<bool> EndConversationAsync(
        long campaignId, long npcId, long conversationId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || conversationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversationId));

        const string sql = """
            UPDATE conversation_sessions
            SET ended_at = now()
            WHERE campaign_id = $1 AND npc_id = $2
              AND session_id = $3 AND ended_at IS NULL
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        command.Parameters.AddWithValue(conversationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> HasConversationSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS(SELECT 1 FROM schema_migrations
                          WHERE version = '0002_conversations')
            """;
        await using var command = _dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
