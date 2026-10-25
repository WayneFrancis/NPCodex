namespace NPCodex.Data;

/// <summary>The unembedded text that an NPC personally knows or believes.</summary>
public sealed record PendingMemory(long Id, string Content);

/// <summary>
/// DM-side semantic search result. Secret memories must not be shown to players
/// without a separate, explicit disclosure policy.
/// </summary>
public sealed record MemoryMatch(
    long Id,
    string Content,
    short Importance,
    bool IsSecret,
    double CosineDistance)
{
    public int? WorldDay { get; init; }
    public TimeOnly? WorldTime { get; init; }
    public string KnowledgeStatus { get; init; } = MemoryKnowledgeStatus.Recollection;
    public string? KnowledgeSource { get; init; }
}
