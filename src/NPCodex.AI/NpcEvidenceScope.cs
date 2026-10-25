using System.Text.RegularExpressions;
using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>
/// A conservative relevance gate for questions naming a specific subject.
/// Vector proximity by itself does not establish that a memory is ABOUT the
/// subject: "map of the northern ruins" is not evidence for "Harbour Master".
/// The matcher intentionally handles only clear multi-word proper names/titles.
/// It does not claim to solve coreference or semantic entailment.
/// </summary>
internal static class NpcEvidenceScope
{
    private static readonly Regex NamedSubjectPattern = new(
        @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,4}\b",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.OrdinalIgnoreCase) { "The", "A", "An" };

    internal static IReadOnlyList<string> NamedSubjects(string question)
    {
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in NamedSubjectPattern.Matches(question))
        {
            var words = match.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var start = words.Length > 2 && LeadingArticles.Contains(words[0]) ? 1 : 0;
            if (words.Length - start < 2) continue;
            var name = string.Join(' ', words.Skip(start));
            subjects.Add(name);
        }
        return subjects.ToArray();
    }

    private static readonly Regex ContentWords = new(
        @"[a-zA-Z]{3,}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> QuestionFillers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "that", "this", "there", "their",
            "about", "anything", "something", "someone", "would", "could",
            "should", "what", "when", "where", "which", "who", "why", "how",
            "you", "your", "they", "them", "his", "her", "she", "him",
            "have", "has", "had", "been", "are", "was", "were", "from",
            "into", "over", "under", "after", "before", "said", "say",
            "tell", "told", "know", "think", "heard", "hear", "rumour",
            "rumours", "rumor", "rumors", "really", "just", "ive", "its",
            "can", "did", "does", "any", "all", "not", "might", "perhaps"
        };

    internal static IReadOnlyList<MemoryMatch> FilterNamedSubject(
        IReadOnlyList<MemoryMatch> candidates, IReadOnlyList<string> names,
        string question)
    {
        if (names.Count == 0) return candidates;

        // A name alone is not sufficient. "The Harbour Master ordered wine"
        // is not evidence that they possess a secret smuggling map.
        var strippedQuestion = Regex.Replace(question,
            @"^\s*[A-Z][a-z]+,\s*", "");
        foreach (var name in names)
            strippedQuestion = strippedQuestion.Replace(name, "",
                StringComparison.OrdinalIgnoreCase);
        var topicalWords = ContentWords.Matches(strippedQuestion.ToLowerInvariant())
            .Select(m => Simplify(m.Value))
            .Where(word => !QuestionFillers.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredOverlap = topicalWords.Count >= 3 ? 2 :
            topicalWords.Count > 0 ? 1 : 0;

        return candidates.Where(memory =>
        {
            if (!names.Any(name => memory.Content.Contains(
                    name, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (requiredOverlap == 0) return true;
            var words = ContentWords.Matches(memory.Content.ToLowerInvariant())
                .Select(m => Simplify(m.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return topicalWords.Count(words.Contains) >= requiredOverlap;
        }).ToArray();
    }

    private static string Simplify(string word) =>
        word.Length > 4 && word.EndsWith('s') ? word[..^1] : word;

    internal static IReadOnlyList<ConversationTurn>? ExcludeFutureTurns(
        IReadOnlyList<ConversationTurn>? history, WorldMoment? now)
    {
        if (history is null || now is null) return history;
        return history.Where(turn =>
            turn.WorldDay is not int day ||
            day < now.Day ||
            (day == now.Day &&
                (turn.WorldTime is not TimeOnly time || time <= now.Time)))
            .ToArray();
    }

    internal static bool HasPriorNpcStatementAbout(
        IReadOnlyList<ConversationTurn>? history, IReadOnlyList<string> names) =>
        history is not null &&
        history.Any(turn => names.Any(name =>
            turn.NpcReply.Contains(name, StringComparison.OrdinalIgnoreCase)));

    internal static string UnverifiedFallback(string style) => style switch
    {
        "Warm" => "I don't have anything reliable to tell you about that. " +
                  "What have you heard, and who told you? Perhaps we can make sense of it.",
        "Guarded" => "I can't confirm that.",
        _ => "I don't have any information I can confirm about that. " +
             "Where did you hear it?"
    };
}
