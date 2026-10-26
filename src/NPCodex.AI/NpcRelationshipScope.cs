using System.Text.RegularExpressions;
using NPCodex.Data;

namespace NPCodex.AI;

/// <summary>
/// A question can be ABOUT a job, contact or service without naming the
/// particular person who supplies it. Retrieve the speaker's own non-secret
/// NPC relationships by relevant history as well as explicit name mentions.
/// This is an evidence selector, not permission to reveal all relationships.
/// </summary>
internal static class NpcRelationshipScope
{
    private static readonly Regex Words = new(
        @"[\p{L}]{3,}", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Filler = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "the","and","was","were","this","that","than","then","here","there",
        "from","with","between","your","you","her","his","him","she","who",
        "what","which","where","when","why","how","would","could","should",
        "does","did","have","has","had","are","its","them","their","will",
        "for","about","act","like","some","one","few","ask","tell","said",
        "know","make","made","over","into","need","needs","looking"
    };

    private static readonly HashSet<string> RoleTerms = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "scout","cartographer","mapmaker","guide","healer",
        "guard","courier","messenger","smith","mercenary",
        "informant","broker","intermediary","hire"
    };

    internal static IReadOnlyList<NpcRelationship> Select(
        IReadOnlyList<NpcRelationship> relationships,
        CampaignCharacterNames names, string question, int maxResults = 5)
    {
        if(maxResults<1) return [];
        var words=Tokens(question);
        return relationships
            .Where(r=>r.TargetType=="npc" && !r.IsSecret)
            .Select(r=>
            {
                bool explicitName=CampaignNameMatcher.Mentions(
                    question,names,r.TargetId);
                var historyTokens=Tokens(r.Description);
                int overlap=words.Count(historyTokens.Contains);
                bool roleOverlap=words.Any(word=>
                    RoleTerms.Contains(word) && historyTokens.Contains(word));
                // A single broad word such as "map" or "client" does
                // not warrant exposing an otherwise unrelated opinion.
                // A highly specific occupational term (scout, guide,
                // etc.) can independently identify the desired contact.
                bool topical=overlap>=2 || roleOverlap;
                return new { Relationship=r, Explicit=explicitName,
                    Overlap=overlap, Topical=topical, Role=roleOverlap };
            })
            .Where(x=>x.Explicit || x.Topical)
            .OrderByDescending(x=>x.Explicit)
            .ThenByDescending(x=>x.Role)
            .ThenByDescending(x=>x.Overlap)
            .ThenBy(x=>x.Relationship.TargetId)
            .Take(maxResults)
            .Select(x=>x.Relationship)
            .ToArray();
    }

    private static HashSet<string> Tokens(string text)
    {
        var results=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(Match m in Words.Matches(text))
        {
            var word=m.Value.ToLowerInvariant();
            word=word switch
            {
                "scouts" or "scouting" or "scouted"=>"scout",
                "maps" or "mapping"=>"map",
                "clients"=>"client",
                "hiring" or "hires" or "hired" or "contracts"
                    or "contracting" or "contract"=>"hire",
                "intelligence"=>"intel",
                "middleman" or "middlemen" or "liaison" or "agent"
                    or "intermediary"=>"broker",
                "cartographers"=>"cartographer",
                "mapmakers"=>"mapmaker",
                "swamps"=>"swamp",
                "guards"=>"guard",
                "guides"=>"guide",
                "preferred"=>"prefer",
                _ when word.Length>5 && word.EndsWith('s')=>word[..^1],
                _=>word
            };
            if(!Filler.Contains(word)) results.Add(word);
        }
        return results;
    }
}
