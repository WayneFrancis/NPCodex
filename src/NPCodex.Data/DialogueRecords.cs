namespace NPCodex.Data;

public sealed record NpcMemoryRecord(
    long Id, string Content, short Importance, bool IsSecret, string MemoryType,
    bool HasEmbedding, DateTime CreatedAtUtc);

public sealed record ApprovedDialogue(long InteractionId, long? MemoryId, long SessionId);
