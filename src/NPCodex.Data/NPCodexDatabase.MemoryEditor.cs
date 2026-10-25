using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<IReadOnlyList<NpcMemoryRecord>> GetNpcMemoriesAsync(
        long campaignId, long npcId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0) throw new ArgumentOutOfRangeException(nameof(npcId));

        const string sql = """
            SELECT m.memory_id, m.content, m.importance, m.is_secret,
                   m.memory_type, m.embedding IS NOT NULL, m.created_at,
                   m.world_day, m.world_time
            FROM memories m
            JOIN characters c ON c.campaign_id = m.campaign_id
                             AND c.character_id = m.owner_character_id
            WHERE m.campaign_id = $1 AND m.owner_character_id = $2
                  AND c.character_type = 'npc'
            ORDER BY m.created_at DESC, m.memory_id DESC
            """;
        var list = new List<NpcMemoryRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new NpcMemoryRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt16(2),
                reader.GetBoolean(3), reader.GetString(4),
                reader.GetBoolean(5), reader.GetDateTime(6))
            {
                WorldDay = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                WorldTime = reader.IsDBNull(8) ? null : reader.GetFieldValue<TimeOnly>(8)
            });
        return list;
    }

    /// <summary>
    /// Create or edit a memory. On edit, compare its original content to guard
    /// against clobbering changes from another DM window.
    /// Changed text invalidates the vector; unchanged text preserves it.
    /// </summary>
    public async Task<long> SaveNpcMemoryAsync(
        long campaignId, long npcId, long? memoryId, string content,
        short importance, bool isSecret, string? expectedContent = null,
        CancellationToken cancellationToken = default,
        WorldMoment? when = null)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0) throw new ArgumentOutOfRangeException(nameof(npcId));
        if (when is not null && when.Day < 1)
            throw new ArgumentException("Campaign day must be at least 1.");
        RequireText(content, nameof(content));
        if (importance is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(importance));
        content = content.Trim();

        if (memoryId is null)
        {
            // INSERT ... SELECT rejects invalid or non-NPC owners.
            const string insert = """
                INSERT INTO memories (campaign_id, owner_character_id, content,
                                     memory_type, importance, is_secret,
                                     world_day, world_time)
                SELECT $1, c.character_id, $3, 'manual', $4, $5, $6, $7
                FROM characters c
                WHERE c.campaign_id = $1 AND c.character_id = $2
                  AND c.character_type = 'npc'
                RETURNING memory_id
                """;
            await using var command = _dataSource.CreateCommand(insert);
            command.Parameters.AddWithValue(campaignId);
            command.Parameters.AddWithValue(npcId);
            command.Parameters.AddWithValue(content);
            command.Parameters.AddWithValue(importance);
            command.Parameters.AddWithValue(isSecret);
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                Value = (object?)when?.Day ?? DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Time,
                Value = (object?)when?.Time ?? DBNull.Value
            });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long id ? id
                : throw new InvalidOperationException("NPC not found in this campaign.");
        }

        if (memoryId <= 0 || expectedContent is null)
            throw new ArgumentException(
                "Existing memory edits require a valid ID and original content.");

        const string update = """
            UPDATE memories m
               SET content = $4,
                   importance = $5,
                   is_secret = $6,
                   world_day = $8,
                   world_time = $9,
                   embedding = CASE WHEN m.content = $4 THEN m.embedding ELSE NULL END,
                   embedding_model = CASE WHEN m.content = $4 THEN m.embedding_model ELSE NULL END
            FROM characters c
            WHERE m.campaign_id = $1 AND m.owner_character_id = $2
              AND m.memory_id = $3 AND m.content = $7
              AND c.campaign_id = m.campaign_id
              AND c.character_id = m.owner_character_id
              AND c.character_type = 'npc'
            RETURNING m.memory_id
            """;
        await using (var command = _dataSource.CreateCommand(update))
        {
            command.Parameters.AddWithValue(campaignId);
            command.Parameters.AddWithValue(npcId);
            command.Parameters.AddWithValue(memoryId.Value);
            command.Parameters.AddWithValue(content);
            command.Parameters.AddWithValue(importance);
            command.Parameters.AddWithValue(isSecret);
            command.Parameters.AddWithValue(expectedContent);
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                Value = (object?)when?.Day ?? DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Time,
                Value = (object?)when?.Time ?? DBNull.Value
            });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long id ? id
                : throw new InvalidOperationException(
                    "Memory was changed or deleted elsewhere. Reload before saving.");
        }
    }

    public async Task<bool> DeleteNpcMemoryAsync(
        long campaignId, long npcId, long memoryId, string expectedContent,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || memoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryId));
        const string sql = """
            DELETE FROM memories m
            USING characters c
            WHERE m.campaign_id = $1 AND m.owner_character_id = $2
              AND m.memory_id = $3 AND m.content = $4
              AND c.campaign_id = m.campaign_id
              AND c.character_id = m.owner_character_id
              AND c.character_type = 'npc'
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(expectedContent);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<PendingMemory?> GetMemoryToEmbedAsync(
        long campaignId, long memoryId, string model,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (memoryId <= 0) throw new ArgumentOutOfRangeException(nameof(memoryId));
        RequireText(model, nameof(model));
        const string sql = """
            SELECT memory_id, content
            FROM memories
            WHERE campaign_id = $1 AND memory_id = $2
              AND (embedding IS NULL OR embedding_model IS DISTINCT FROM $3)
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PendingMemory(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    /// <summary>
    /// Explicit approval writes a transcript and optional DM-authored memory
    /// atomically. An AI suggestion alone never becomes a historical fact.
    /// </summary>
    public async Task<ApprovedDialogue> RecordApprovedDialogueAsync(
        long campaignId, long npcId, long? playerCharacterId,
        string playerLine, string approvedNpcReply, string? memoryText = null,
        short importance = 3, bool memorySecret = false,
        CancellationToken cancellationToken = default,
        long? conversationId = null)
    {
        RequireCampaignId(campaignId);
        if (conversationId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversationId));
        if (npcId <= 0 || playerCharacterId is <= 0 || playerCharacterId == npcId)
            throw new ArgumentException("Invalid or duplicate participant ID.");
        RequireText(playerLine, nameof(playerLine));
        RequireText(approvedNpcReply, nameof(approvedNpcReply));
        if (importance is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(importance));
        memoryText = string.IsNullOrWhiteSpace(memoryText) ? null : memoryText.Trim();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string npcName;
        long? locationId;
        await using (var npcCmd = BuildCommand("""
            SELECT name, current_location_id FROM characters
            WHERE campaign_id = $1 AND character_id = $2 AND character_type = 'npc'
            """, connection, transaction, campaignId, npcId))
        await using (var reader = await npcCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("NPC not found in the selected campaign.");
            npcName = reader.GetString(0);
            locationId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (playerCharacterId is long pcId)
        {
            await using var pcCmd = BuildCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM characters
                    WHERE campaign_id = $1 AND character_id = $2
                      AND character_type = 'pc')
                """, connection, transaction, campaignId, pcId);
            if (await pcCmd.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("Player character not found in the selected campaign.");
        }

        // Either resume a validated open session (locked to serialize turns),
        // or start one with the first DM-approved exchange.
        long sessionId;
        if (conversationId is long selectedSession)
        {
            await using var sessionCmd = BuildCommand("""
                SELECT session_id FROM conversation_sessions
                WHERE campaign_id = $1 AND session_id = $2 AND npc_id = $3
                  AND player_character_id IS NOT DISTINCT FROM $4::bigint
                  AND ended_at IS NULL
                FOR UPDATE
                """, connection, transaction,
                campaignId, selectedSession, npcId, playerCharacterId);
            if (await sessionCmd.ExecuteScalarAsync(cancellationToken) is not long foundId)
                throw new InvalidOperationException(
                    "Conversation changed or ended. Reload the current session.");
            sessionId = foundId;
        }
        else
        {
            sessionId = await InsertIdAsync("""
                INSERT INTO conversation_sessions (campaign_id, npc_id, player_character_id)
                VALUES ($1, $2, $3) RETURNING session_id
                """, connection, transaction, cancellationToken,
                campaignId, npcId, playerCharacterId);
        }

        // Plain-text transcript. Escape/encode when displaying in rich-text/html UIs.
        var transcript = $"Player: {playerLine.Trim()}\n{npcName}: {approvedNpcReply.Trim()}";
        var interactionId = await InsertIdAsync("""
            INSERT INTO interactions (campaign_id, location_id, summary, transcript)
            VALUES ($1, $2, $3, $4)
            RETURNING interaction_id
            """, connection, transaction, cancellationToken,
            campaignId, locationId, $"DM-approved conversation with {npcName}", transcript);

        await ExecuteSqlAsync("""
            INSERT INTO interaction_participants (campaign_id, interaction_id, character_id)
            VALUES ($1, $2, $3)
            """, connection, transaction, cancellationToken, campaignId, interactionId, npcId);

        if (playerCharacterId is long pc)
            await ExecuteSqlAsync("""
                INSERT INTO interaction_participants (campaign_id, interaction_id, character_id)
                VALUES ($1, $2, $3)
                """, connection, transaction, cancellationToken, campaignId, interactionId, pc);

        // Approved short-term conversational context. A separate long-term NPC
        // memory is only created when the DM supplies memoryText below.
        await ExecuteSqlAsync("""
            INSERT INTO conversation_turns
                (campaign_id, session_id, interaction_id, player_line, npc_reply)
            VALUES ($1, $2, $3, $4, $5)
            """, connection, transaction, cancellationToken,
            campaignId, sessionId, interactionId, playerLine.Trim(), approvedNpcReply.Trim());

        long? memoryId = null;
        if (memoryText is not null)
            memoryId = await InsertIdAsync("""
                INSERT INTO memories
                    (campaign_id, owner_character_id, source_interaction_id,
                     content, memory_type, importance, is_secret)
                VALUES ($1, $2, $3, $4, 'experience', $5, $6)
                RETURNING memory_id
                """, connection, transaction, cancellationToken,
                campaignId, npcId, interactionId, memoryText, importance, memorySecret);

        await transaction.CommitAsync(cancellationToken);
        return new ApprovedDialogue(interactionId, memoryId, sessionId);
    }
}
