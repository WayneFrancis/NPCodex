using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Browse or keyword-search DM-visible campaign memories across any PC/NPC.
    /// Empty owner IDs mean all characters in the selected campaign.
    /// Includes secret memories only at the DM's explicit choice.
    /// </summary>
    public async Task<IReadOnlyList<CampaignMemoryHit>> FindCampaignMemoriesAsync(
        long campaignId, IReadOnlyCollection<long> ownerIds,
        string? searchText, bool includeSecrets = true, int limit = 300,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var ids = ownerIds.Where(id => id > 0).Distinct().ToArray();
        searchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        const string sql = """
            SELECT m.memory_id, m.owner_character_id, c.name, c.character_type,
                   m.content, m.memory_type, m.importance, m.is_secret,
                   m.world_day, m.world_time, m.embedding IS NOT NULL,
                   CASE WHEN $3::text IS NULL THEN 0.0
                        ELSE ts_rank_cd(to_tsvector('simple', m.content),
                          websearch_to_tsquery('simple', $3::text)) END AS relevance
            FROM memories m
            JOIN characters c ON c.campaign_id = m.campaign_id
                             AND c.character_id = m.owner_character_id
            WHERE m.campaign_id = $1
              AND (cardinality($2::bigint[]) = 0
                   OR m.owner_character_id = ANY($2::bigint[]))
              AND ($4 OR NOT m.is_secret)
              AND ($3::text IS NULL
                   OR to_tsvector('simple', m.content)
                      @@ websearch_to_tsquery('simple', $3::text)
                   OR m.content ILIKE ('%' || $3::text || '%'))
            ORDER BY relevance DESC,
                     m.world_day DESC NULLS LAST,
                     m.world_time DESC NULLS LAST, m.memory_id DESC
            LIMIT $5
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)searchText ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue(includeSecrets);
        cmd.Parameters.AddWithValue(limit);
        return await ReadMemoryHitsAsync(cmd, cancellationToken);
    }

    /// <summary>
    /// Semantic retrieval against existing 768-dimensional memory vectors.
    /// PC and NPC memories are eligible; never silently embed missing data.
    /// Embeddings with another model are deliberately excluded.
    /// </summary>
    public async Task<IReadOnlyList<CampaignMemoryHit>> SearchCampaignMemoriesByMeaningAsync(
        long campaignId, IReadOnlyCollection<long> ownerIds, string model,
        float[] queryEmbedding, bool includeSecrets = true, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(model, nameof(model));
        ValidateEmbedding(queryEmbedding);
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var ids = ownerIds.Where(id => id > 0).Distinct().ToArray();
        const string sql = """
            SELECT m.memory_id, m.owner_character_id, c.name, c.character_type,
                   m.content, m.memory_type, m.importance, m.is_secret,
                   m.world_day, m.world_time, true AS has_embedding,
                   (1.0 - (m.embedding <=> $3))::double precision AS relevance
            FROM memories m
            JOIN characters c ON c.campaign_id = m.campaign_id
                             AND c.character_id = m.owner_character_id
            WHERE m.campaign_id = $1
              AND (cardinality($2::bigint[]) = 0
                   OR m.owner_character_id = ANY($2::bigint[]))
              AND m.embedding IS NOT NULL AND m.embedding_model = $4
              AND ($5 OR NOT m.is_secret)
            ORDER BY m.embedding <=> $3, m.memory_id DESC
            LIMIT $6
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids
        });
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(includeSecrets);
        cmd.Parameters.AddWithValue(limit);
        return await ReadMemoryHitsAsync(cmd, cancellationToken);
    }

    private static async Task<IReadOnlyList<CampaignMemoryHit>> ReadMemoryHitsAsync(
        NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var results = new List<CampaignMemoryHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new CampaignMemoryHit(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt16(6), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<TimeOnly>(9),
                reader.GetBoolean(10), Convert.ToDouble(reader.GetValue(11))));
        return results;
    }
}
