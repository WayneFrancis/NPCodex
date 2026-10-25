using System.Text.RegularExpressions;

namespace NPCodex.AI;

/// <summary>
/// Detects obviously repetitive dialogue candidates. A similarity score
/// doesn't prove disclosure differences, but does catch near-verbatim output
/// from small models before the DM reviews it.
/// </summary>
internal static class DialogueVariationCheck
{
    internal static bool TooSimilar(string first, string second)
    {
        var a = Tokens(first);
        var b = Tokens(second);
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Join(" ", a) == string.Join(" ", b)) return true;

        var aSet = a.ToHashSet(StringComparer.Ordinal);
        var bSet = b.ToHashSet(StringComparer.Ordinal);
        var shared = aSet.Count(t => bSet.Contains(t));
        var union = aSet.Count + bSet.Count - shared;
        if (union == 0) return false;

        var sizeRatio = Math.Min(a.Length, b.Length) /
                        (double)Math.Max(a.Length, b.Length);
        var jaccard = shared / (double)union;
        return sizeRatio >= 0.7 && jaccard >= 0.78;
    }

    private static string[] Tokens(string source) =>
        Regex.Matches(source.ToLowerInvariant(), @"[\p{L}\p{N}']+")
            .Select(m => m.Value)
            .ToArray();
}
