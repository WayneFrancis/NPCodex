namespace NPCodex.Data;

public sealed record CharacterAlias(long Id, long CharacterId, string Alias);

/// <summary>DM-readable directed relationship, sourced from the speaking NPC.</summary>
public sealed record NpcRelationship(
    long TargetId, string TargetName, string TargetType,
    short Disposition, string Description, bool IsSecret);

/// <summary>Names that the campaign uses for a character.</summary>
public sealed record CharacterNameEntry(long CharacterId, string Name);

/// <summary>Small campaign name index for unambiguous references only.</summary>
public sealed record CampaignCharacterNames(
    IReadOnlyList<CharacterNameEntry> Characters,
    IReadOnlyList<CharacterAlias> Aliases);
