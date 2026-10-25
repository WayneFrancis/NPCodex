namespace NPCodex.Data;

/// <summary>
/// DM-only memory search result, including the owner. A relevance score is
/// only meaningful within the chosen search mode (full text vs embedding).
/// </summary>
public sealed record CampaignMemoryHit(
    long MemoryId, long OwnerCharacterId, string OwnerName, string OwnerType,
    string Content, string MemoryType, short Importance, bool IsSecret,
    int? WorldDay, TimeOnly? WorldTime, bool HasEmbedding, double Relevance);
