using System.Text.RegularExpressions;
using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>
/// Resolves DM-approved aliases without conflating two campaign characters.
/// A bare name must be unique across every canonical name and alias in the
/// campaign before it can identify someone mentioned in player dialogue.
/// </summary>
internal static class CampaignNameMatcher
{
    internal static IReadOnlyList<string> NamesOf(
        CampaignCharacterNames index, long characterId)
    {
        var names = new List<string>();
        var canonical = index.Characters.FirstOrDefault(c => c.CharacterId == characterId)?.Name;
        if (canonical is null) return names;

        names.Add(canonical);
        foreach (var alias in index.Aliases.Where(a => a.CharacterId == characterId))
            if (!names.Contains(alias.Alias, StringComparer.OrdinalIgnoreCase))
                names.Add(alias.Alias);

        // Backward compatibility for the original seeded demo record.
        foreach (var demo in SpeakerReferenceCheck.GetKnownNames(canonical))
            if (!names.Contains(demo, StringComparer.OrdinalIgnoreCase))
                names.Add(demo);

        return names.Where(name => IsUnique(index, name, characterId)).ToArray();
    }

    private static bool IsUnique(
        CampaignCharacterNames index, string name, long ownerId)
    {
        return !index.Characters.Any(c =>
                   c.CharacterId != ownerId &&
                   string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            && !index.Aliases.Any(a =>
                   a.CharacterId != ownerId &&
                   string.Equals(a.Alias, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool Mentions(
        string playerLine, CampaignCharacterNames index, long characterId)
    {
        return NamesOf(index, characterId).Any(name => Regex.IsMatch(
            playerLine, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
}
