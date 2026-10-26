using NPCodex.AI;
using NPCodex.Data;
using System.Data;

int passed=0,failed=0;
void Check(string name,Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine("PASS "+name);
    }
    catch(Exception ex)
    {
        failed++;
        Console.WriteLine("FAIL "+name+": "+ex.Message);
    }
}
void Assert(bool condition,string message)
{
    if(!condition) throw new Exception(message);
}

// Representative fixtures model how the user's dialogue actually stores
// "NPC's opinion / history": directed character_relationships, NOT vectors.
const string redHistory =
    "Mira finds him crass but needs his skills when working to get maps made " +
    "by Millard. She is responsible for hiring scouts to gather intel for " +
    "when she needs Millard to make a map for her client. " +
    "Red is her first pick as a scout for hire.";
const string question =
    "Don't you act as the middle man between clients and Millard and scouts " +
    "and Millard? What scout would you contract with to gather intel about the swamps?";
var names=new CampaignCharacterNames(
    [new CharacterNameEntry(1,"Mira"),
     new CharacterNameEntry(2,"Rowan"),
     new CharacterNameEntry(3,"Darrin Reed"),
     new CharacterNameEntry(4,"Millard"),
     new CharacterNameEntry(5,"Davin")],
    [new CharacterAlias(6,3,"Red")]);
var red=new NpcRelationship(3,"Darrin Reed","npc",-25,redHistory,false);
var millard=new NpcRelationship(4,"Millard","npc",20,
    "Millard draws maps when commissioned. Mira is his intermediary.",false);
var irrelevant=new NpcRelationship(5,"Davin","npc",25,
    "Davin bakes honeycakes and keeps a bakery by the south gate.",false);
var secret=new NpcRelationship(6,"Secret Contact","npc",50,
    "A secret scout knows the swamps intimately.",true);

Check("Mira's unnamed first-pick scout Red is retrieved by role",()=>
{
    var result=NpcRelationshipScope.Select(
        [red,millard,irrelevant],names,question);
    Assert(result.Any(r=>r.TargetId==3),
        "The non-secret Red history must reach the dialogue prompt.");
    Assert(result.Any(r=>r.TargetId==4),
        "The question explicitly names Millard.");
    Assert(!result.Any(r=>r.TargetId==5),
        "The baker's unrelated history must stay out.");
});
Check("Role question about scouts finds a contact without naming Red",()=>
{
    var found=NpcRelationshipScope.Select(
        [red,millard,irrelevant],names,
        "Which scout would you recommend?");
    Assert(found.Count==1 && found[0].TargetId==red.TargetId,
        "A specific occupation alone should retrieve Red.");
});
Check("Case-insensitive canonical and alias name resolution",()=>
{
    var a=NpcRelationshipScope.Select([red,millard],names,
        "would you choose red for this work?");
    var b=NpcRelationshipScope.Select([red,millard],names,
        "What about Darrin Reed?");
    Assert(a.Count==1 && a[0].TargetId==3,
        "red should match the alias Red.");
    Assert(b.Count==1 && b[0].TargetId==3,
        "Darrin Reed should match the same contact.");
});
Check("A general question doesn't dump unrelated opinions",()=>
{
    Assert(NpcRelationshipScope.Select([red,millard,irrelevant],names,
        "How are you today?").Count==0,
        "Generic conversation should not reveal the social graph.");
});
Check("Non-secret relationship rule excludes confidential scouts",()=>
{
    var scoped=NpcRelationshipScope.Select(
        [red,secret],names,"Who are your scouts?");
    Assert(scoped.Count==1 && scoped[0].TargetId==3,
        "Secret NPC relationship must be excluded even when relevant.");
});
Check("A low-quality broad keyword isn't enough for relationship disclosure",()=>
{
    var selected=NpcRelationshipScope.Select(
        [red,millard],names,"I drew a map.");
    Assert(selected.Count==0,"Map alone must not dump all contacts.");
});
Check("A user can explicitly name an unrelated contact",()=>
{
    var selected=NpcRelationshipScope.Select(
        [red,irrelevant],names,"Where is Davin?");
    Assert(selected.Count==1 && selected[0].TargetId==5,
        "Unambiguous explicit target should be included.");
});
Check("Invented scout Kael is rejected when Red is the only known pick",()=>
{
    var unsupported=NpcContactGroundingCheck.UnsupportedName(
        question,"For the swamps, I'd recommend Kael of the Iron Path. " +
                 "He's reliable and knows the terrain.",names,[redHistory]);
    Assert(unsupported=="Kael",
        $"Kael must be flagged, actually got {unsupported ?? "null"}.");
});
Check("Correct grounded advice about Red is not flagged",()=>
{
    var unsupported=NpcContactGroundingCheck.UnsupportedName(
        question,"I would start with Red. He's my first choice, " +
                 "though I would need to ask about the swamps.",names,
        [redHistory]);
    Assert(unsupported is null,
        $"Don't reject Red: {unsupported}.");
});
Check("Normal words at starts of sentences aren't mistaken for contacts",()=>
{
    var unsupported=NpcContactGroundingCheck.UnsupportedName(
        question,"I'd speak with Red. Perhaps we can arrange that.",names,
        [redHistory]);
    Assert(unsupported is null,$"Unexpected contact: {unsupported}.");
});
Check("Delegating the scout contract to Millard is a continuity error",()=>
{
    Assert(NpcContactGroundingCheck.ContradictsBrokerResponsibility(
        question,"You should go talk to Millard directly about hiring a scout.",
        [red,millard]),
        "Mira has established that she handles hiring.");
});
Check("Mira may still personally speak to the mapmaker",()=>
{
    Assert(!NpcContactGroundingCheck.ContradictsBrokerResponsibility(
        question,"I'll speak with Millard myself after I arrange a scout.",
        [red,millard]),
        "The middleman speaking to Millard is not delegating to Rowan.");
});
Check("No broker knowledge means no false contradiction",()=>
{
    Assert(!NpcContactGroundingCheck.ContradictsBrokerResponsibility(
        question,"You should contact Millard directly.",[irrelevant]),
        "Do not claim an unstated intermediary role.");
});
Check("Fallback uses stored nickname and never invents swamp expertise",()=>
{
    var reply=NpcContactGroundingCheck.CautiousBrokerReply(
        "Natural",[red,millard],names);
    Assert(reply.Contains("Red") &&
        !reply.Contains("Kael") &&
        reply.Contains("ask whether",StringComparison.OrdinalIgnoreCase),
        "Only the recorded preferred contact and an availability check.");
});
Check("Guarded fallback can withhold a contact without fabricating one",()=>
{
    var reply=NpcContactGroundingCheck.CautiousBrokerReply(
        "Guarded",[red,millard],names);
    Assert(!reply.Contains("Red") && !reply.Contains("Kael"),
        "A guarded NPC may withhold the scout's identity.");
});


Check("Negative world moments are ordinary signed dates",()=>
{
    var day=new WorldMoment(-240,new TimeOnly(9,30));
    Assert(day.Day==-240 &&
        day.ToString()=="Day -240 at 09:30",
        "Events centuries or months before the campaign stay negative.");
    Assert(new WorldMoment(0,new TimeOnly(0,0)).Day==0,
        "Campaign day zero is a valid prelude, not an unknown date.");
});

Check("Conversation chronology excludes future turns across negative days",()=>
{
    var turns=new[]
    {
        new ConversationTurn(1,1,"First","Earlier",DateTime.UtcNow)
            { WorldDay=-20, WorldTime=new TimeOnly(8,0) },
        new ConversationTurn(2,2,"Second","Today",DateTime.UtcNow)
            { WorldDay=-5, WorldTime=new TimeOnly(8,0) },
        new ConversationTurn(3,3,"Third","Later today",DateTime.UtcNow)
            { WorldDay=-5, WorldTime=new TimeOnly(16,0) },
        new ConversationTurn(4,4,"Fourth","Tomorrow",DateTime.UtcNow)
            { WorldDay=-4, WorldTime=new TimeOnly(8,0) }
    };
    var chosen=NpcEvidenceScope.ExcludeFutureTurns(
        turns,new WorldMoment(-5,new TimeOnly(9,0)));
    Assert(chosen is not null && chosen.Count==2 &&
        chosen.Select(t=>t.Id).SequenceEqual(new long[]{1,2}),
        "Negative dates must respect chronological ordering and time.");
});

Check("Column filters combine text, bool and numeric cells",()=>
{
    var data=new DataTable();
    data.Columns.Add("Character",typeof(string));
    data.Columns.Add("Secret",typeof(bool));
    data.Columns.Add("Score",typeof(double));
    data.Rows.Add("Mira",true,0.91);
    data.Rows.Add("Rowan",false,0.23);
    data.Rows.Add("Millard",false,0.19);
    data.DefaultView.RowFilter=GridTextFilter.Build(data.Columns,
        new Dictionary<int,string>{{0,"mi"},{1,"False"}});
    Assert(data.DefaultView.Count==1 &&
        (string)data.DefaultView[0]["Character"]=="Millard",
        "Both character-name and boolean column filters must work together.");
    data.DefaultView.RowFilter=GridTextFilter.Build(data.Columns,
        new Dictionary<int,string>{{2,"0.91"}});
    Assert(data.DefaultView.Count==1 &&
        (string)data.DefaultView[0]["Character"]=="Mira",
        "Numeric scores must also support typed text filters.");
});

Check("Statement and memory search literal filters escape punctuation",()=>
{
    var data=new DataTable();
    data.Columns.Add("What was said",typeof(string));
    data.Rows.Add("Red's [map] is 40% complete *");
    data.Rows.Add("Reds map is done");
    foreach(var literal in new[]{"Red's","[map]","40%","*"})
    {
        data.DefaultView.RowFilter=GridTextFilter.Build(data.Columns,
            new Dictionary<int,string>{{0,literal}});
        Assert(data.DefaultView.Count==1,
            $"Literal filter {literal} should not be interpreted as a wildcard.");
    }
    data.DefaultView.RowFilter=GridTextFilter.Build(data.Columns,
        new Dictionary<int,string>{{0,""}});
    Assert(data.DefaultView.Count==2,"Clearing the filter restores rows.");
});

Console.WriteLine($"{passed} passed; {failed} failed.");
if(failed>0) Environment.ExitCode=1;
