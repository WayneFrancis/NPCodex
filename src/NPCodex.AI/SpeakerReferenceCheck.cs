using System.Text.RegularExpressions;

namespace NPCodex.AI;

/// <summary>
/// A limited quality check for mentioning the current conversation partner as
/// an absent third party. It is intentionally a signal for model-based revision,
/// not a text rewriter: replacing names with pronouns mechanically is unsafe.
/// </summary>
internal static class SpeakerReferenceCheck
{
    private const string DemoSuffix = " (example PC)";

    /// <summary>
    /// The seeded demo character is named "Rowan (example PC)" in the DB,
    /// while older memories correctly use "Rowan". Preserve both forms.
    /// Do not generically shorten other names: that could conflate two NPCs.
    /// </summary>
    internal static IReadOnlyList<string> GetKnownNames(string selectedCharacterName)
    {
        if (string.IsNullOrWhiteSpace(selectedCharacterName))
            return Array.Empty<string>();

        var exact = selectedCharacterName.Trim();
        if (exact.EndsWith(DemoSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var naturalName = exact[..^DemoSuffix.Length].Trim();
            if (naturalName.Length > 0)
                return [naturalName, exact];
        }

        return [exact];
    }

    /// <summary>
    /// Heuristic: "Rowan promised", "until Rowan returns", "for Rowan", or
    /// "Rowan's" are suspicious in a conversation directly WITH Rowan.
    /// Direct vocatives such as "Rowan, I kept it for you" are fine.
    /// These are prompts for revision, never proof of semantic correctness.
    /// </summary>
    internal static bool MentionsSpeakerAsThirdParty(
        string reply, IReadOnlyList<string> speakerNames)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        foreach (var name in speakerNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
            foreach (Match match in Regex.Matches(reply, pattern, RegexOptions.IgnoreCase))
            {
                var next = reply[(match.Index + match.Length)..].TrimStart();
                // A standalone or vocative name is not an obvious error.
                if (next.Length == 0 || next[0] is ',' or '!' or '?' or ':')
                    continue;

                // Detect "Rowan's ..." or "Rowan promised ...", etc.
                if (next.StartsWith("'s", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith("’s", StringComparison.OrdinalIgnoreCase) ||
                    char.IsLetterOrDigit(next[0]))
                    return true;

                // "I have a map for Rowan." may finish a sentence.
                var before = reply[..match.Index];
                if (Regex.IsMatch(before,
                    @"\b(?:for|about|with|to|from|until|because of)\s*$",
                    RegexOptions.IgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
