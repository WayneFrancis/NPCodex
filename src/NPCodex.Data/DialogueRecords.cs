namespace NPCodex.Data;

public sealed record NpcMemoryRecord(
    long Id, string Content, short Importance, bool IsSecret, string MemoryType,
    bool HasEmbedding, DateTime CreatedAtUtc)
{
    public int? WorldDay { get; init; }
    public TimeOnly? WorldTime { get; init; }
    public string KnowledgeStatus { get; init; } = MemoryKnowledgeStatus.Recollection;
    public string? KnowledgeSource { get; init; }
}

public sealed record ApprovedDialogue(long InteractionId, long? MemoryId, long SessionId);
