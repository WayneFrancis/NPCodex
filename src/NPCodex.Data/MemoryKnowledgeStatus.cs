namespace NPCodex.Data;

/// <summary>
/// Epistemic status is attached to an NPC's memory, never inferred by an LLM.
/// A verified fact is still only known by characters who have that memory.
/// </summary>
public static class MemoryKnowledgeStatus
{
    public const string Verified = "verified";
    public const string Recollection = "recollection";
    public const string Belief = "belief";
    public const string Rumour = "rumour";
    public const string CoverStory = "cover_story";

    public static readonly IReadOnlyList<(string Code, string Label)> Options =
    [
        (Recollection, "Recollection (may be imperfect)"),
        (Verified, "Verified fact (DM-confirmed)"),
        (Belief, "Belief (might be mistaken)"),
        (Rumour, "Rumour (heard secondhand)"),
        (CoverStory, "Prepared lie (DM-authored)")
    ];

    public static string Label(string? code) =>
        Options.FirstOrDefault(item => item.Code == code).Label
        ?? "Recollection (legacy)";

    public static void Validate(string code)
    {
        if (!Options.Any(item => item.Code == code))
            throw new ArgumentException("Unknown memory knowledge status.", nameof(code));
    }
}
