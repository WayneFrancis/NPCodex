using Npgsql;
using Pgvector;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Retrieves memories requiring embeddings, including memories embedded by
    /// a different model. Does not change the memory itself.
    /// </summary>
    public async Task<IReadOnlyList<PendingMemory>> GetMemoriesToEmbedAsync(
        long campaignId, string model, int maxCount = 50,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(model, nameof(model));
        if (maxCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Batch size must be 1-500.");

        const string sql = """
            SELECT memory_id, content
            FROM memories
            WHERE campaign_id = $1
              AND (embedding IS NULL OR embedding_model IS DISTINCT FROM $2)
            ORDER BY memory_id
            LIMIT $3
            """;

        var results = new List<PendingMemory>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(maxCount);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new PendingMemory(reader.GetInt64(0), reader.GetString(1)));

        return results;
    }

    /// <summary>
    /// Writes the vector only if the content is unchanged since it was read.
    /// Safe to repeat with the same model, without overwriting existing vectors.
    /// </summary>
    public async Task<bool> SaveMemoryEmbeddingAsync(
        long campaignId, long memoryId, string expectedContent,
        string model, float[] embedding, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (memoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryId));
        RequireText(expectedContent, nameof(expectedContent));
        RequireText(model, nameof(model));
        ValidateEmbedding(embedding);

        const string sql = """
            UPDATE memories
            SET embedding = $1,
                embedding_model = $2
            WHERE campaign_id = $3
              AND memory_id = $4
              AND content = $5
              AND (embedding IS NULL OR embedding_model IS DISTINCT FROM $2)
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(model);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(expectedContent);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Exact cosine-distance search scoped to one campaign and one character.
    /// Only vectors made with the requested embedding model are compared.
    /// Caller is a DM-only application at this stage; secrets may be included.
    /// </summary>
    public async Task<IReadOnlyList<MemoryMatch>> SearchNpcMemoriesAsync(
        long campaignId, long npcId, string embeddingModel, float[] queryEmbedding,
        int maxResults = 5, CancellationToken cancellationToken = default,
        bool includeSecretMemories = true, int? currentWorldDay = null,
        TimeOnly? currentWorldTime = null)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0)
            throw new ArgumentOutOfRangeException(nameof(npcId));
        RequireText(embeddingModel, nameof(embeddingModel));
        ValidateEmbedding(queryEmbedding);
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        const string sql = """
            SELECT m.memory_id, m.content, m.importance, m.is_secret,
                   m.embedding <=> $4 AS cosine_distance,
                   m.world_day, m.world_time
            FROM memories m
            JOIN characters c
              ON c.campaign_id = m.campaign_id
             AND c.character_id = m.owner_character_id
             AND c.character_type = 'npc'
            WHERE m.campaign_id = $1
              AND m.owner_character_id = $2
              AND m.embedding_model = $3
              AND m.embedding IS NOT NULL
              AND ($6 OR m.is_secret = false)
              AND ($7::integer IS NULL OR m.world_day IS NULL
                   OR m.world_day < $7
                   OR (m.world_day = $7
                       AND ($8::time IS NULL OR m.world_time IS NULL
                            OR m.world_time <= $8)))
            ORDER BY m.embedding <=> $4, m.memory_id
            LIMIT $5
            """;

        var matches = new List<MemoryMatch>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(campaignId);
        command.Parameters.AddWithValue(npcId);
        command.Parameters.AddWithValue(embeddingModel);
        command.Parameters.AddWithValue(new Vector(queryEmbedding));
        command.Parameters.AddWithValue(maxResults);
        command.Parameters.AddWithValue(includeSecretMemories);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
            Value = (object?)currentWorldDay ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Time,
            Value = (object?)currentWorldTime ?? DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            matches.Add(new MemoryMatch(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetBoolean(3),
                reader.GetDouble(4))
            {
                WorldDay = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                WorldTime = reader.IsDBNull(6) ? null : reader.GetFieldValue<TimeOnly>(6)
            });

        return matches;
    }

    private static void ValidateEmbedding(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length != 768)
            throw new ArgumentException(
                $"Expected 768-dimensional embedding; got {embedding.Length}.", nameof(embedding));

        var containsNonzero = false;
        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("Embedding has a non-finite value.", nameof(embedding));
            containsNonzero |= value != 0f;
        }

        if (!containsNonzero)
            throw new ArgumentException("An all-zero embedding is invalid for cosine search.", nameof(embedding));
    }
}
