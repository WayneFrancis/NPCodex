using System.Text.RegularExpressions;
using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>
/// Cheap post-generation guard for the very common small-model failure of
/// inventing a NEW named contact in answer to "which scout should I hire?".
/// This flags candidates for one bounded retry; it never adds world facts.
/// </summary>
internal static class NpcContactGroundingCheck
{
    private static readonly Regex Capitalized = new(
        @"\b[A-Z][a-z]{2,}\b", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ProseWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "The","This","That","But","And","For","With","When","Where",
        "While","Then","Well","Yes","No","Now","So","Though","If",
        "Because","Perhaps","Maybe","Certainly","Actually","However",
        "Besides","Just","Still","First","Next","Otherwise","Instead",
        "Very","Sure","What","Who","Which","Why","How","Let",
        "I'd","I've","You're","We're","I'll","He's","She's"
    };

    internal static string? UnsupportedName(
        string question,string reply,CampaignCharacterNames index,
        IEnumerable<string> evidence)
    {
        // Do not impose a proper-noun policy on ordinary philosophical or
        // geographical discussion. Only person/agent referral questions.
        if(!Regex.IsMatch(question,
            @"\b(scout|scouts|hire|hiring|contract|recruit|recommend|whom|who|which|contact|send|guide|messenger)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;

        var sources=evidence.Append(question).ToArray();
        var knownNames=index.Characters.Select(c=>c.Name)
            .Concat(index.Aliases.Select(a=>a.Alias)).ToArray();
        foreach(Match m in Capitalized.Matches(reply))
        {
            if(ProseWords.Contains(m.Value)) continue;
            if(IsStartOfSentence(reply,m.Index)) continue;
            if(knownNames.Any(name=>ContainsWord(name,m.Value))) continue;
            if(sources.Any(text=>ContainsWord(text,m.Value))) continue;
            return m.Value;
        }
        return null;
    }

    private static bool ContainsWord(string text,string word)=>
        Regex.IsMatch(text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsStartOfSentence(string reply,int index)
    {
        for(int i=index-1;i>=0;i--)
        {
            if(char.IsWhiteSpace(reply[i])) continue;
            return reply[i] is '.' or '!' or '?' or '\n';
        }
        return true;
    }

    internal static bool ContradictsBrokerResponsibility(string question,
        string reply,IReadOnlyList<NpcRelationship> relevant)
    {
        if(!Regex.IsMatch(question,
            @"\b(scout|scouts|hiring|hire|contract|map|mapmaker|cartographer)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        bool broker=relevant.Any(r=>Regex.IsMatch(r.Description,
            @"\b(responsible for hiring|arranges? (?:the )?scouts?|middle[ -]?m[ae]n|intermediar|broker)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if(!broker) return false;

        // Only flag affirmative instructions that the listener, rather
        // than the speaker, must arrange another specialist directly.
        return Regex.IsMatch(reply,
            @"\b(?:you|your best bet)\s+(?:should|could|need to|have to|must|can|might|would)\s+(?:(?![.!?]).){0,55}\b(?:talk to|speak (?:to|with)|contact|visit|go to|ask)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(reply,
            @"\b(directly|yourself|mapmaker|cartographer)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static string CautiousBrokerReply(
        string style,IReadOnlyList<NpcRelationship> relevant,
        CampaignCharacterNames index)
    {
        var preferred=relevant.FirstOrDefault(r=>
            Regex.IsMatch(r.Description,
                @"\b(first pick|first choice|preferred|go-to)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(r.Description,@"\bscouts?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var knownName=preferred is null ? null :
            CampaignNameMatcher.NamesOf(index,preferred.TargetId)
                .FirstOrDefault(n=>Regex.IsMatch(preferred.Description,
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(n)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            ??preferred.TargetName;

        // Never attribute a commissioning role to an NPC unless the
        // supplied directed history or dialogue actually establishes it.
        bool broker=relevant.Any(r=>Regex.IsMatch(r.Description,
            @"\b(responsible for hiring|arranges? (?:the )?scouts?|middle[ -]?m[ae]n|broker)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if(!broker)
            return style=="Guarded"
                ? "I can't recommend anyone I can vouch for."
                : "I don't have a confirmed scout to recommend. " +
                  "I would need to check who has experience in the swamps.";
        // Never fabricate scouting experience with an unmentioned region.
        if(style=="Guarded")
            return "I arrange the scouts for that work. I can look into it.";
        return knownName is null
            ? "I handle the scouting arrangements for that work. " +
              "I'll need to check who can cover the swamps before making promises."
            : $"I handle the scouting arrangements. {knownName} is my first choice. " +
              "I can ask whether they're familiar with the swamps before committing them.";
    }
}
