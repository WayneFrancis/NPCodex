using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasWorldEventsSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0005_world_events')");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Explicitly approved scene, atomically recorded with its real speakers,
    /// optional witness memories, and fictional time. Both speakers may be NPCs.
    /// All saved memories state what was heard, not whether the words were true.
    /// </summary>
    public async Task<ApprovedScene> RecordApprovedSceneAsync(
        long campaignId, long replyingNpcId, long? initiatingCharacterId,
        string initiatingLine, string approvedNpcReply,
        WorldMoment when, string? replyNpcMemory,
        short importance, bool memorySecret,
        long? conversationId, IReadOnlyCollection<long> memoryRecipients,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (replyingNpcId <= 0 || initiatingCharacterId is <= 0 ||
            initiatingCharacterId == replyingNpcId)
            throw new ArgumentException("The two speakers must be different characters.");
        if (when is null || when.Day < 1)
            throw new ArgumentException("A campaign day of 1 or later is required.");
        if (importance is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(importance));
        if (conversationId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(conversationId));
        RequireText(initiatingLine, nameof(initiatingLine));
        RequireText(approvedNpcReply, nameof(approvedNpcReply));
        initiatingLine = initiatingLine.Trim();
        approvedNpcReply = approvedNpcReply.Trim();
        replyNpcMemory = string.IsNullOrWhiteSpace(replyNpcMemory)
            ? null : replyNpcMemory.Trim();

        var recipients = memoryRecipients.Distinct().ToArray();
        if (recipients.Any(id => id <= 0 || id == replyingNpcId))
            throw new ArgumentException("Witness recipients must be other valid characters.");
        if (recipients.Length > 50)
            throw new ArgumentException("Limit one scene to 50 memory recipients.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        async Task<string> CharacterNameAsync(long id, string? requiredType = null)
        {
            await using var cmd = BuildCommand("""
                SELECT name FROM characters
                WHERE campaign_id = $1 AND character_id = $2
                  AND ($3::text IS NULL OR character_type = $3)
                """, connection, transaction, campaignId, id, requiredType);
            return await cmd.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException(
                    $"Character #{id} is not a valid participant in this campaign.");
        }

        var npcName = await CharacterNameAsync(replyingNpcId, "npc");
        var initiatingName = initiatingCharacterId is long speakerId
            ? await CharacterNameAsync(speakerId)
            : "Unspecified speaker";
        foreach (var personId in recipients)
            _ = await CharacterNameAsync(personId);

        long sessionId;
        if (conversationId is long existingId)
        {
            await using var cmd = BuildCommand("""
                SELECT session_id FROM conversation_sessions
                WHERE campaign_id = $1 AND session_id = $2
                  AND npc_id = $3
                  AND player_character_id IS NOT DISTINCT FROM $4::bigint
                  AND ended_at IS NULL
                FOR UPDATE
                """, connection, transaction,
                campaignId, existingId, replyingNpcId, initiatingCharacterId);
            sessionId = await cmd.ExecuteScalarAsync(cancellationToken) is long found
                ? found
                : throw new InvalidOperationException(
                    "Conversation was changed or ended. Refresh before approving.");
        }
        else
        {
            sessionId = await InsertIdAsync("""
                INSERT INTO conversation_sessions
                    (campaign_id, npc_id, player_character_id)
                VALUES ($1, $2, $3)
                RETURNING session_id
                """, connection, transaction, cancellationToken,
                campaignId, replyingNpcId, initiatingCharacterId);
        }

        var transcript = $"{initiatingName}: {initiatingLine}\n{npcName}: {approvedNpcReply}";
        var interactionId = await InsertIdAsync("""
            INSERT INTO interactions
                (campaign_id, campaign_day, world_day, world_time, summary, transcript)
            VALUES ($1, $2, $2, $3, $4, $5)
            RETURNING interaction_id
            """, connection, transaction, cancellationToken,
            campaignId, when.Day, when.Time,
            $"Approved dialogue between {initiatingName} and {npcName}", transcript);

        await ExecuteSqlAsync("""
            INSERT INTO interaction_participants
                (campaign_id, interaction_id, character_id, participation_role)
            VALUES ($1, $2, $3, 'replying')
            """, connection, transaction, cancellationToken,
            campaignId, interactionId, replyingNpcId);

        if (initiatingCharacterId is long speaker)
            await ExecuteSqlAsync("""
                INSERT INTO interaction_participants
                    (campaign_id, interaction_id, character_id, participation_role)
                VALUES ($1, $2, $3, 'initiating')
                """, connection, transaction, cancellationToken,
                campaignId, interactionId, speaker);

        foreach (var recipient in recipients)
        {
            if (recipient == initiatingCharacterId) continue;
            await ExecuteSqlAsync("""
                INSERT INTO interaction_participants
                    (campaign_id, interaction_id, character_id, participation_role)
                VALUES ($1, $2, $3, 'witness')
                """, connection, transaction, cancellationToken,
                campaignId, interactionId, recipient);
        }

        await ExecuteSqlAsync("""
            INSERT INTO conversation_turns
                (campaign_id, session_id, interaction_id, speaker_character_id,
                 player_line, npc_reply)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, transaction, cancellationToken,
            campaignId, sessionId, interactionId,
            initiatingCharacterId, initiatingLine, approvedNpcReply);

        var newMemoryIds = new List<long>();
        long? replyMemoryId = null;

        async Task<long> CreateMemoryAsync(long owner, string content, string type)
        {
            return await InsertIdAsync("""
                INSERT INTO memories
                    (campaign_id, owner_character_id, source_interaction_id,
                     content, memory_type, importance, is_secret, world_day, world_time)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                RETURNING memory_id
                """, connection, transaction, cancellationToken,
                campaignId, owner, interactionId, content, type,
                importance, memorySecret, when.Day, when.Time);
        }

        if (replyNpcMemory is not null)
        {
            replyMemoryId = await CreateMemoryAsync(
                replyingNpcId, replyNpcMemory, "experience");
            newMemoryIds.Add(replyMemoryId.Value);
        }

        foreach (var personId in recipients)
        {
            var ownerName = await CharacterNameAsync(personId);
            // Intentionally encode heard dialogue rather than assert its truth.
            var witnessed = personId == initiatingCharacterId
                ? $"On {when}, I ({ownerName}) said to {npcName}: " +
                  $"\"{initiatingLine}\". {npcName} replied: \"{approvedNpcReply}\"."
                : $"On {when}, I ({ownerName}) overheard {initiatingName} " +
                  $"say to {npcName}: \"{initiatingLine}\". " +
                  $"{npcName} replied: \"{approvedNpcReply}\".";
            newMemoryIds.Add(await CreateMemoryAsync(
                personId, witnessed, "overheard"));
        }

        await transaction.CommitAsync(cancellationToken);
        return new ApprovedScene(interactionId, sessionId, replyMemoryId, newMemoryIds);
    }
}
