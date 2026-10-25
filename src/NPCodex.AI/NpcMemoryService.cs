using NPCodex.Data;

namespace NPCodex.AI;

public sealed record EmbeddingSyncResult(int Found, int Stored, int Skipped);

/// <summary>
/// Converts a campaign's unembedded NPC memories into pgvector embeddings.
/// This is a manually invoked first implementation; no background job yet.
/// </summary>
public sealed class NpcMemoryService
{
    private readonly NPCodexDatabase _database;
    private readonly OllamaEmbeddingClient _ollama;

    public NpcMemoryService(NPCodexDatabase database, OllamaEmbeddingClient ollama)
    {
        _database = database;
        _ollama = ollama;
    }

    public async Task<EmbeddingSyncResult> EmbedPendingAsync(
        long campaignId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var pending = await _database.GetMemoriesToEmbedAsync(
            campaignId, OllamaEmbeddingClient.ModelName, limit, cancellationToken);

        var stored = 0;
        var skipped = 0;
        foreach (var memory in pending)
        {
            var vector = await _ollama.EmbedDocumentAsync(memory.Content, cancellationToken);
            if (await _database.SaveMemoryEmbeddingAsync(
                campaignId, memory.Id, memory.Content,
                OllamaEmbeddingClient.ModelName, vector, cancellationToken))
                stored++;
            else
                skipped++; // Record changed or was embedded concurrently.
        }

        return new EmbeddingSyncResult(pending.Count, stored, skipped);
    }

    /// <summary>Embeds one newly saved or edited memory; no-op if current.</summary>
    public async Task<bool> EmbedMemoryAsync(
        long campaignId, long memoryId, CancellationToken cancellationToken = default)
    {
        var memory = await _database.GetMemoryToEmbedAsync(
            campaignId, memoryId, OllamaEmbeddingClient.ModelName, cancellationToken);
        if (memory is null)
            return false;

        var vector = await _ollama.EmbedDocumentAsync(memory.Content, cancellationToken);
        return await _database.SaveMemoryEmbeddingAsync(
            campaignId, memory.Id, memory.Content,
            OllamaEmbeddingClient.ModelName, vector, cancellationToken);
    }

    /// <summary>
    /// Retrieves a specific NPC's embedded memories within the given campaign.
    /// Returns a DM-facing result; caller must not expose secret memories to players.
    /// </summary>
    public async Task<IReadOnlyList<MemoryMatch>> SearchAsync(
        long campaignId, long npcId, string query,
        int maxResults = 5, CancellationToken cancellationToken = default,
        bool includeSecretMemories = true, int? currentWorldDay = null,
        TimeOnly? currentWorldTime = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search question cannot be empty.", nameof(query));

        var vector = await _ollama.EmbedQueryAsync(query, cancellationToken);
        return await _database.SearchNpcMemoriesAsync(
            campaignId, npcId, OllamaEmbeddingClient.ModelName,
            vector, maxResults, cancellationToken,
            includeSecretMemories: includeSecretMemories,
            currentWorldDay: currentWorldDay,
            currentWorldTime: currentWorldTime);
    }
}
