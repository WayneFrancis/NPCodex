namespace NPCodex.Data;

/// <summary>Fields used to guide NPC dialogue, plus private notes never sent to Ollama.</summary>
public sealed record NpcPersonality(
    string PersonalityTraits,
    string SpeakingStyle,
    string Mannerisms,
    string Motivations,
    string PrivateDmNotes)
{
    public static NpcPersonality Empty { get; } = new("", "", "", "", "");
}

/// <summary>A directed opinion held by the NPC toward the selected PC.</summary>
public sealed record NpcAttitude(
    long TargetCharacterId,
    string TargetCharacterName,
    short Disposition,
    string Description,
    bool IsSecret);
