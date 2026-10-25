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

    internal static IReadOnlyList<MemoryMatch> FilterNamedSubject(
        IReadOnlyList<MemoryMatch> candidates, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return candidates;

        // The query can name multiple people/places. Matching any one
        // subject keeps the memory eligible for the dialogue context.
        return candidates.Where(m => names.Any(name =>
            m.Content.Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

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
