using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace NPCodex.Data;

public sealed record ConversationMomentInfo(
    long InteractionId, int? WorldDay, TimeOnly? WorldTime,
    string Speaker, string Content, int StatementCount, int LinkedMemoryCount);

public sealed record PendingStatement(long Id, string Content);

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasConversationMaintenanceSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM schema_migrations
                           WHERE version = '0008_conversation_maintenance')
            """);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<ConversationMomentInfo?> GetConversationMomentAsync(
        long campaignId, long statementId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (statementId <= 0) throw new ArgumentOutOfRangeException(nameof(statementId));
        await using var cmd = _dataSource.CreateCommand("""
            SELECT i.interaction_id, i.world_day, i.world_time,
                   COALESCE(c.name, '(Unknown speaker)'), st.content,
                   (SELECT COUNT(*)::int FROM conversation_statements other
                    WHERE other.campaign_id = i.campaign_id
                      AND other.interaction_id = i.interaction_id),
                   (SELECT COUNT(*)::int FROM memories m
                    WHERE m.campaign_id = i.campaign_id
                      AND m.source_interaction_id = i.interaction_id)
            FROM conversation_statements st
            JOIN interactions i ON i.campaign_id = st.campaign_id
                               AND i.interaction_id = st.interaction_id
            LEFT JOIN characters c ON c.campaign_id = st.campaign_id
                                  AND c.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1 AND st.statement_id = $2
            """);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(statementId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ConversationMomentInfo(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeOnly>(2),
            reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5), reader.GetInt32(6));
    }

    /// <summary>
    /// Correct the fictional moment of the WHOLE exchange, atomically.
    /// Statements and matching sourced memories follow the parent interaction.
    /// Witness memories explicitly starting "On Day ... at ..." get their
    /// date prefix updated; changed text has its embedding invalidated.
    /// A linked memory given a different time by the DM is left unchanged.
    /// An audit row records every change. No dialogue words are rewritten.
    /// </summary>
    public async Task<int> CorrectConversationMomentAsync(
        long campaignId, ConversationMomentInfo expected, WorldMoment moment,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (moment.Day < 1)
            throw new ArgumentOutOfRangeException(nameof(moment));
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.InteractionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expected));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        NpgsqlCommand Cmd(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }
        static void AddNullableDay(NpgsqlCommand cmd, int? value) =>
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Integer,
                Value = (object?)value ?? DBNull.Value
            });
        static void AddNullableTime(NpgsqlCommand cmd, TimeOnly? value) =>
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Time,
                Value = (object?)value ?? DBNull.Value
            });

        // Optimistic concurrency check. This must succeed before any child updates.
        await using (var cmd = Cmd("""
            UPDATE interactions
            SET world_day = $3, world_time = $4, campaign_day = $3
            WHERE campaign_id = $1 AND interaction_id = $2
              AND world_day IS NOT DISTINCT FROM $5::int
              AND world_time IS NOT DISTINCT FROM $6::time
            """))
        {
            cmd.Parameters.AddWithValue(campaignId);
            cmd.Parameters.AddWithValue(expected.InteractionId);
            cmd.Parameters.AddWithValue(moment.Day);
            cmd.Parameters.AddWithValue(moment.Time);
            AddNullableDay(cmd, expected.WorldDay);
            AddNullableTime(cmd, expected.WorldTime);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException(
                    "The exchange time has changed or the interaction no longer exists. Refresh.");
        }

        await using (var cmd = Cmd("""
            UPDATE conversation_statements
            SET world_day = $3, world_time = $4
            WHERE campaign_id = $1 AND interaction_id = $2
            """))
        {
            cmd.Parameters.AddWithValue(campaignId);
            cmd.Parameters.AddWithValue(expected.InteractionId);
            cmd.Parameters.AddWithValue(moment.Day);
            cmd.Parameters.AddWithValue(moment.Time);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 'On Day 3 at 12:00, I overheard...' is generated witness-memory
        // text, not free-form authored dialogue. Update only that prefix.
        var oldPrefix = expected.WorldDay is int day &&
                        expected.WorldTime is TimeOnly time
            ? $"On Day {day} at {time:HH:mm}" : null;
        var newPrefix = $"On {moment}";
        int changedMemories;
        await using (var cmd = Cmd("""
            UPDATE memories m SET
                world_day = $3, world_time = $4,
                content = CASE WHEN $7::text IS NOT NULL
                    AND m.memory_type = 'overheard'
                    AND starts_with(m.content, $7::text)
                    THEN $8::text || substr(m.content, length($7::text) + 1)
                    ELSE m.content END,
                embedding = CASE WHEN $7::text IS NOT NULL
                    AND m.memory_type = 'overheard'
                    AND starts_with(m.content, $7::text)
                    THEN NULL ELSE m.embedding END,
                embedding_model = CASE WHEN $7::text IS NOT NULL
                    AND m.memory_type = 'overheard'
                    AND starts_with(m.content, $7::text)
                    THEN NULL ELSE m.embedding_model END
            WHERE m.campaign_id = $1
              AND m.source_interaction_id = $2
              AND m.world_day IS NOT DISTINCT FROM $5::int
              AND m.world_time IS NOT DISTINCT FROM $6::time
            """))
        {
            cmd.Parameters.AddWithValue(campaignId);
            cmd.Parameters.AddWithValue(expected.InteractionId);
            cmd.Parameters.AddWithValue(moment.Day);
            cmd.Parameters.AddWithValue(moment.Time);
            AddNullableDay(cmd, expected.WorldDay);
            AddNullableTime(cmd, expected.WorldTime);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = (object?)oldPrefix ?? DBNull.Value
            });
            cmd.Parameters.AddWithValue(newPrefix);
            changedMemories = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = Cmd("""
            INSERT INTO conversation_time_edits
                (campaign_id, interaction_id, old_world_day, old_world_time,
                 new_world_day, new_world_time)
            VALUES ($1, $2, $3, $4, $5, $6)
            """))
        {
            cmd.Parameters.AddWithValue(campaignId);
            cmd.Parameters.AddWithValue(expected.InteractionId);
            AddNullableDay(cmd, expected.WorldDay);
            AddNullableTime(cmd, expected.WorldTime);
            cmd.Parameters.AddWithValue(moment.Day);
            cmd.Parameters.AddWithValue(moment.Time);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changedMemories;
    }

    public async Task<IReadOnlyList<PendingStatement>> GetStatementsToEmbedAsync(
        long campaignId, string model, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(model, nameof(model));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var cmd = _dataSource.CreateCommand("""
            SELECT statement_id, content FROM conversation_statements
            WHERE campaign_id = $1
              AND (embedding IS NULL OR embedding_model IS DISTINCT FROM $2)
            ORDER BY statement_id LIMIT $3
            """);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(limit);
        var found = new List<PendingStatement>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            found.Add(new PendingStatement(reader.GetInt64(0), reader.GetString(1)));
        return found;
    }

    public async Task<bool> SaveStatementEmbeddingAsync(
        long campaignId, long statementId, string expectedContent,
        string model, float[] embedding, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (statementId <= 0) throw new ArgumentOutOfRangeException(nameof(statementId));
        RequireText(model, nameof(model));
        ValidateEmbedding(embedding);
        await using var cmd = _dataSource.CreateCommand("""
            UPDATE conversation_statements
            SET embedding = $1, embedding_model = $2
            WHERE campaign_id = $3 AND statement_id = $4 AND content = $5
              AND (embedding IS NULL OR embedding_model IS DISTINCT FROM $2)
            """);
        cmd.Parameters.AddWithValue(new Vector(embedding));
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(statementId);
        cmd.Parameters.AddWithValue(expectedContent);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<StatementInfo>> SearchStatementsByMeaningAsync(
        long campaignId, string model, float[] queryEmbedding,
        int limit = 300, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(model, nameof(model));
        ValidateEmbedding(queryEmbedding);
        if (limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var cmd = _dataSource.CreateCommand("""
            SELECT st.statement_id, st.world_day, st.world_time,
                   COALESCE(speaker.name, '(Unknown speaker)'),
                   COALESCE((
                     SELECT string_agg(person.name, ', ' ORDER BY person.name, person.character_id)
                     FROM characters person
                     WHERE person.campaign_id = st.campaign_id AND
                       (person.character_id = st.speaker_character_id OR EXISTS (
                           SELECT 1 FROM statement_hearers h
                           WHERE h.campaign_id = st.campaign_id
                             AND h.statement_id = st.statement_id
                             AND h.character_id = person.character_id))
                   ), '(Unknown participants)'),
                   st.content,
                   (1.0 - (st.embedding <=> $3))::double precision AS relevance
            FROM conversation_statements st
            LEFT JOIN characters speaker ON speaker.campaign_id = st.campaign_id
              AND speaker.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1
              AND st.embedding IS NOT NULL AND st.embedding_model = $2
            ORDER BY st.embedding <=> $3, st.statement_id DESC
            LIMIT $4
            """);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(limit);
        var results = new List<StatementInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new StatementInfo(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeOnly>(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetDouble(6)));
        return results;
    }
}
