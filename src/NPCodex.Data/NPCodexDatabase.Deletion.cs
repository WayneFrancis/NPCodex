using Npgsql;

namespace NPCodex.Data;

/// <summary>
/// DM-only destructive operations. A forgotten memory is not the same as an
/// event erased from campaign history. All deletions are campaign-scoped.
/// </summary>
public sealed record ApprovedStatementDeletionInfo(
    long StatementId, long InteractionId, string Speaker, string Content,
    int StatementCount, int LinkedMemoryCount);

public sealed record ApprovedDeletionResult(
    long InteractionId, int RemovedStatements, int RemovedMemories,
    int RemovedTurns, bool InteractionRemoved);

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Remove one character's exact memory, including its vector, while
    /// leaving everyone else's recollections and objective history intact.
    /// Expected text prevents deleting an entry changed by another window.
    /// </summary>
    public async Task<bool> ForgetCharacterMemoryAsync(
        long campaignId, long characterId, long memoryId, string expectedContent,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0 || memoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryId));
        RequireText(expectedContent, nameof(expectedContent));

        await using var command = _dataSource.CreateCommand("""
            DELETE FROM memories
            WHERE campaign_id = $1 AND owner_character_id = $2
              AND memory_id = $3 AND content = $4
            """);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(expectedContent);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ApprovedStatementDeletionInfo?> GetApprovedStatementDeletionInfoAsync(
        long campaignId, long statementId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (statementId <= 0) throw new ArgumentOutOfRangeException(nameof(statementId));
        await using var command = _dataSource.CreateCommand("""
            SELECT st.statement_id, st.interaction_id,
                   COALESCE(c.name, '(Unknown speaker)'), st.content,
                   (SELECT count(*)::integer FROM conversation_statements other
                    WHERE other.campaign_id = st.campaign_id
                      AND other.interaction_id = st.interaction_id),
                   (SELECT count(*)::integer FROM memories m
                    WHERE m.campaign_id = st.campaign_id
                      AND m.source_interaction_id = st.interaction_id)
            FROM conversation_statements st
            LEFT JOIN characters c ON c.campaign_id = st.campaign_id
                                  AND c.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1 AND st.statement_id = $2
            """);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(statementId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ApprovedStatementDeletionInfo(
            reader.GetInt64(0), reader.GetInt64(1),
            reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetInt32(5));
    }

    /// <summary>
    /// Erase either one spoken statement or every statement in its exchange.
    /// The turn containing it stores BOTH spoken lines and cannot retain a
    /// partial copy; it is therefore removed, even in single-line mode.
    /// Any memories sourced from the exchange are removed as they may quote
    /// the deleted words. Remaining statement records are preserved.
    /// </summary>
    public async Task<ApprovedDeletionResult> DeleteApprovedSpeechAsync(
        long campaignId, ApprovedStatementDeletionInfo expected,
        bool wholeExchange, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.StatementId <= 0 || expected.InteractionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expected));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        NpgsqlCommand Command(string sql)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            return cmd;
        }

        // Serialize editing/deletion for the same exchange through its parent.
        await using (var lockCommand = Command("""
            SELECT interaction_id FROM interactions
            WHERE campaign_id = $1 AND interaction_id = $2 FOR UPDATE
            """))
        {
            lockCommand.Parameters.AddWithValue(campaignId);
            lockCommand.Parameters.AddWithValue(expected.InteractionId);
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not long)
                throw new InvalidOperationException(
                    "The exchange has already been removed. Refresh history.");
        }

        await using (var verify = Command("""
            SELECT st.content, COALESCE(c.name, '(Unknown speaker)'),
                   (SELECT count(*)::int FROM conversation_statements siblings
                    WHERE siblings.campaign_id = st.campaign_id
                      AND siblings.interaction_id = st.interaction_id)
            FROM conversation_statements st
            LEFT JOIN characters c ON c.campaign_id = st.campaign_id
                                  AND c.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1 AND st.statement_id = $2
              AND st.interaction_id = $3
            FOR UPDATE OF st
            """))
        {
            verify.Parameters.AddWithValue(campaignId);
            verify.Parameters.AddWithValue(expected.StatementId);
            verify.Parameters.AddWithValue(expected.InteractionId);
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetString(0) != expected.Content ||
                reader.GetString(1) != expected.Speaker ||
                reader.GetInt32(2) != expected.StatementCount)
                throw new InvalidOperationException(
                    "The selected statement or exchange changed. Refresh before deleting.");
        }

        async Task<int> RunAsync(string sql, params object[] values)
        {
            await using var cmd = Command(sql);
            foreach (var value in values)
                cmd.Parameters.AddWithValue(value);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete source-linked memories FIRST. This includes witness memories
        // and deliberately drafted NPC memories derived from the exchange.
        // Standalone memories and memories of other interactions are untouched.
        var removedMemories = await RunAsync("""
            DELETE FROM memories
            WHERE campaign_id = $1 AND source_interaction_id = $2
            """, campaignId, expected.InteractionId);

        // A conversation_turn duplicates both sides of a scene. If one
        // statement is removed, that paired turn is no longer a valid verbatim
        // record, and must be deleted to prevent leaking the erased words.
        var removedTurns = await RunAsync("""
            DELETE FROM conversation_turns
            WHERE campaign_id = $1 AND interaction_id = $2
            """, campaignId, expected.InteractionId);

        int removedStatements;
        if (wholeExchange)
        {
            await RunAsync("""
                DELETE FROM statement_hearers
                WHERE campaign_id = $1
                  AND statement_id IN (
                    SELECT statement_id FROM conversation_statements
                    WHERE campaign_id = $1 AND interaction_id = $2)
                """, campaignId, expected.InteractionId);
            removedStatements = await RunAsync("""
                DELETE FROM conversation_statements
                WHERE campaign_id = $1 AND interaction_id = $2
                """, campaignId, expected.InteractionId);
        }
        else
        {
            await RunAsync("""
                DELETE FROM statement_hearers
                WHERE campaign_id = $1 AND statement_id = $2
                """, campaignId, expected.StatementId);
            removedStatements = await RunAsync("""
                DELETE FROM conversation_statements
                WHERE campaign_id = $1 AND statement_id = $2
                  AND interaction_id = $3
                """, campaignId, expected.StatementId, expected.InteractionId);
            if (removedStatements != 1)
                throw new InvalidOperationException(
                    "Could not remove the chosen statement. Changes were rolled back.");
        }

        // Rebuild the interaction transcript exclusively from surviving lines,
        // so a deleted statement cannot be discovered through the original text.
        var remaining = new List<string>();
        await using (var read = Command("""
            SELECT COALESCE(c.name, '(Unknown speaker)'), st.content
            FROM conversation_statements st
            LEFT JOIN characters c ON c.campaign_id = st.campaign_id
                                  AND c.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1 AND st.interaction_id = $2
            ORDER BY st.statement_id
            """))
        {
            read.Parameters.AddWithValue(campaignId);
            read.Parameters.AddWithValue(expected.InteractionId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                remaining.Add(reader.GetString(0) + ": " + reader.GetString(1));
        }

        if (remaining.Count == 0)
        {
            // No historical content survives. Clear all child references
            // before dropping the interaction itself.
            await RunAsync("""
                DELETE FROM conversation_time_edits
                WHERE campaign_id = $1 AND interaction_id = $2
                """, campaignId, expected.InteractionId);
            await RunAsync("""
                DELETE FROM interaction_participants
                WHERE campaign_id = $1 AND interaction_id = $2
                """, campaignId, expected.InteractionId);
            await RunAsync("""
                DELETE FROM interactions
                WHERE campaign_id = $1 AND interaction_id = $2
                """, campaignId, expected.InteractionId);
        }
        else
        {
            await using var update = Command("""
                UPDATE interactions
                SET transcript = $3, summary = 'Approved conversation (edited)'
                WHERE campaign_id = $1 AND interaction_id = $2
                """);
            update.Parameters.AddWithValue(campaignId);
            update.Parameters.AddWithValue(expected.InteractionId);
            update.Parameters.AddWithValue(string.Join("\n", remaining));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ApprovedDeletionResult(expected.InteractionId,
            removedStatements, removedMemories, removedTurns,
            remaining.Count == 0);
    }
}
