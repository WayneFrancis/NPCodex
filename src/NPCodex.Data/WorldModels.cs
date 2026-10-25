namespace NPCodex.Data;

public sealed record LocationInfo(
    long Id, long? ParentLocationId, string Name, string Type, string? Description);

public sealed record CharacterInfo(
    long Id, string Type, string Name, string? Occupation, long? CurrentLocationId);

public sealed record InteractionInfo(
    long Id, int? CampaignDay, string Summary, string Participants);

public sealed record MemoryInfo(
    long Id, string OwnerName, string Content, short Importance, bool IsSecret, bool HasEmbedding);

/// <summary>Lightweight tree listing; does not load campaign-wide memories or interactions.</summary>
public sealed record CampaignBrowser(
    long CampaignId,
    IReadOnlyList<LocationInfo> Locations,
    IReadOnlyList<CharacterInfo> Characters);

public sealed record CampaignWorld(
    long CampaignId,
    IReadOnlyList<LocationInfo> Locations,
    IReadOnlyList<CharacterInfo> Characters,
    IReadOnlyList<InteractionInfo> Interactions,
    IReadOnlyList<MemoryInfo> Memories);

public sealed record DemoWorldIds(
    long TownId, long ShopId, long MerchantId, long PlayerCharacterId, long InteractionId);
