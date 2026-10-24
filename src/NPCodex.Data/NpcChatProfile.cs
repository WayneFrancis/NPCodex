namespace NPCodex.Data;

/// <summary>
/// Deliberately minimal NPC context intended for generating an in-character
/// DM preview. It omits dm_notes, biography, secrets and location descriptions.
/// </summary>
public sealed record NpcChatProfile(
    long CharacterId,
    string Name,
    string? Occupation,
    string? CurrentLocation,
    string? LocationRoles);
