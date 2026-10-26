using System.Text.RegularExpressions;
using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>
/// The local model cannot read a whole campaign's transcript every turn.
/// Keep recent dialogue AND earlier approved turns related to the current
/// subject, with the latter clearly identified as statements (not world facts).
/// No vectors or migrations needed: works with already approved dialogue.
/// </summary>
internal static class ConversationContinuity
{
    internal sealed record Selection(
        IReadOnlyList<ConversationTurn> Turns,
        IReadOnlyList<ConversationTurn> EarlierRelevant,
        int RecentCount);

    private const int RecentLimit = 6;
    private const int EarlierLimit = 5;

    private static readonly HashSet<string> StopWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "are", "was", "were", "for", "from", "about",
        "what", "when", "where", "which", "who", "why", "how", "did",
        "does", "have", "has", "had", "you", "your", "yours", "we",
        "our", "they", "them", "their", "this", "that", "those",
        "there", "here", "with", "into", "over", "under", "she",
        "her", "him", "his", "its", "can", "could", "would", "should",
        "want", "like", "know", "heard", "said", "tell", "told",
        "please", "getting", "get", "got", "make", "made", "making",
        "someone", "anyone", "anything", "something", "between",
        "already", "just", "then", "than", "also", "been", "will",
        "isnt", "dont", "doesnt", "arent", "lets", "that", "it"
    };

    internal static Selection Select(
        IReadOnlyList<ConversationTurn>? eligibleHistory, string currentQuestion)
    {
        if (eligibleHistory is not { Count: > 0 })
            return new Selection([], [], 0);

        // Interaction IDs are approval-order, not real-world wall clock dates.
        // Chronological eligibility is already checked by NpcEvidenceScope.
        var ordered = eligibleHistory.OrderBy(t => t.Id).ToArray();
        var recent = ordered.TakeLast(RecentLimit).ToArray();
        var older = ordered.Take(ordered.Length - recent.Length).ToArray();

        var questionTerms = Tokens(currentQuestion);
        // For short follow-ups ("And Red?", "What about that?"), add keywords
        // from the immediate approved discussion without dragging in an
        // unrelated long-term topic from earlier in the campaign.
        if (questionTerms.Count < 2 && recent.Length > 0)
        {
            foreach (var token in Tokens(recent[^1].PlayerLine))
                questionTerms.Add(token);
            foreach (var token in Tokens(recent[^1].NpcReply))
                questionTerms.Add(token);
        }

        var relevant = older.Select(turn =>
            new { Turn = turn, Score = Score(turn, questionTerms) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Turn.Id)
            .Take(EarlierLimit)
            .Select(item => item.Turn)
            .ToArray();

        var chosen = recent.Concat(relevant)
            .OrderBy(t => t.Id).ToArray();
        return new Selection(chosen, relevant.OrderBy(t => t.Id).ToArray(),
            recent.Length);
    }

    private static int Score(ConversationTurn turn, HashSet<string> terms)
    {
        if (terms.Count == 0) return 0;

        // An NPC's earlier assertion that "I arrange the scouts and the
        // cartographer" is more relevant to their continuity than a claim
        // that the other speaker made about them.
        var answerTokens = Tokens(turn.NpcReply);
        var playerTokens = Tokens(turn.PlayerLine);
        var npcMatches = terms.Count(answerTokens.Contains);
        var playerMatches = terms.Count(playerTokens.Contains);
        return npcMatches * 3 + playerMatches;
    }

    private static HashSet<string> Tokens(string content)
    {
        // Treat common two-word spellings as one concept, so "middle man"
        // can match an earlier NPC claim to be an "intermediary", and
        // "map maker" can match "cartographer"/"mapmaker".
        content = Regex.Replace(content, @"\bmiddle[\s-]+m[ae]n\b", "middleman",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        content = Regex.Replace(content, @"\bmap[\s-]+makers?\b", "mapmaker",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(content.ToLowerInvariant(),
                     @"[\p{L}\p{N}]{3,}"))
        {
            var word = Normalize(match.Value);
            if (!StopWords.Contains(word))
                tokens.Add(word);
        }
        return tokens;
    }

    private static string Normalize(string word) => word switch
    {
        "cartographer" or "cartographers" or "mapmaker" or "mapmakers"
            or "mapping" or "maps" => "map",
        "scouts" or "scouting" or "scouted" => "scout",
        "middleman" or "middlemen" or "intermediary" or "intermediaries"
            or "broker" or "brokers" or "liaison" or "liaisons" => "agent",
        "commissions" or "commissioned" or "commissioning" => "commission",
        _ when word.Length > 4 && word.EndsWith('s') => word[..^1],
        _ => word
    };
}
