using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>Explicit, local-only batch embedding of approved conversation statements.</summary>
public sealed class ConversationEmbeddingService
{
    private readonly NPCodexDatabase _database;
    private readonly OllamaEmbeddingClient _client;

    public ConversationEmbeddingService(
        NPCodexDatabase database, OllamaEmbeddingClient client)
    {
        _database = database;
        _client = client;
    }

    public async Task<EmbeddingSyncResult> EmbedPendingAsync(
        long campaignId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var pending = await _database.GetStatementsToEmbedAsync(
            campaignId, OllamaEmbeddingClient.ModelName, limit, cancellationToken);
        var stored = 0;
        var skipped = 0;
        foreach (var statement in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await _client.EmbedDocumentAsync(
                statement.Content, cancellationToken);
            if (await _database.SaveStatementEmbeddingAsync(
                campaignId, statement.Id, statement.Content,
                OllamaEmbeddingClient.ModelName, embedding, cancellationToken))
                stored++;
            else
                skipped++;
        }
        return new EmbeddingSyncResult(pending.Count, stored, skipped);
    }
}
