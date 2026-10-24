using NPCodex.Data;
using Npgsql;

var verb = args.Length == 0 ? "check" : args[0];

if (verb is "--help" or "-h" or "help")
{
    ShowUsage();
    return;
}

var validArgs = verb switch
{
    "check" => args.Length <= 1,
    "create-campaign" => args.Length == 2 && !string.IsNullOrWhiteSpace(args[1]),
    "seed-demo" or "show-world" => args.Length == 2 && IsPositiveId(args[1]),
    _ => false
};

if (!validArgs)
{
    ShowUsage();
    Environment.ExitCode = 2;
    return;
}

try
{
    // Shared with future NPCodex desktop/services projects. Does not print secrets.
    var connectionString = NPCodexConnectionSettings.CreateLocalConnectionString();

    await using var db = new NPCodexDatabase(connectionString);
    var status = await db.CheckAsync();

    Console.WriteLine($"PostgreSQL: {status.PostgreSqlVersion}");
    Console.WriteLine($"pgvector: {status.PgvectorVersion}");
    Console.WriteLine($"Public tables: {status.PublicTableCount}");
    Console.WriteLine(
        $"Initial migration: {(status.InitialMigrationInstalled ? "installed" : "NOT installed")}");

    if (!status.InitialMigrationInstalled || status.PgvectorVersion == "not installed")
    {
        Console.Error.WriteLine(
            "Database is not ready. Check the migration in DBeaver before continuing.");
        Environment.ExitCode = 1;
        return;
    }

    switch (verb)
    {
        case "create-campaign":
        {
            var id = await db.CreateCampaignAsync(args[1]);
            Console.WriteLine($"Created campaign {id}: {args[1]}");
            await PrintCampaignsAsync(db);
            break;
        }
        case "seed-demo":
        {
            var campaignId = long.Parse(args[1]);
            var result = await db.SeedDemoAsync(campaignId);
            Console.WriteLine("Demo world created in one transaction:");
            Console.WriteLine($"  Town ID: {result.TownId}");
            Console.WriteLine($"  Shop ID: {result.ShopId}");
            Console.WriteLine($"  Merchant NPC ID: {result.MerchantId}");
            Console.WriteLine($"  Example PC ID: {result.PlayerCharacterId}");
            Console.WriteLine($"  Interaction ID: {result.InteractionId}");
            await PrintWorldAsync(db, campaignId);
            break;
        }
        case "show-world":
            await PrintWorldAsync(db, long.Parse(args[1]));
            break;
        default:
            await PrintCampaignsAsync(db);
            break;
    }

    Console.WriteLine("Database access succeeded.");
}
catch (PostgresException ex)
{
    Console.Error.WriteLine($"PostgreSQL error {ex.SqlState}: {ex.MessageText}");
    Environment.ExitCode = 1;
}
catch (NpgsqlException ex)
{
    Console.Error.WriteLine($"Could not connect to PostgreSQL: {ex.Message}");
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Environment.ExitCode = 1;
}

static bool IsPositiveId(string value) => long.TryParse(value, out var id) && id > 0;

static async Task PrintCampaignsAsync(NPCodexDatabase db)
{
    var campaigns = await db.GetCampaignsAsync();
    Console.WriteLine($"Campaigns ({campaigns.Count}):");
    foreach (var campaign in campaigns)
        Console.WriteLine($"  {campaign.Id}: {campaign.Name}");
}

static async Task PrintWorldAsync(NPCodexDatabase db, long campaignId)
{
    // DevTools is a DM-only utility: it prints secret memories too.
    var world = await db.GetCampaignWorldAsync(campaignId);
    Console.WriteLine($"World for campaign {campaignId}");
    Console.WriteLine($"Locations ({world.Locations.Count}):");
    foreach (var item in world.Locations)
        Console.WriteLine(
            $"  {item.Id}: {item.Name} [{item.Type}] parent={item.ParentLocationId?.ToString() ?? "none"}");

    Console.WriteLine($"Characters ({world.Characters.Count}):");
    foreach (var item in world.Characters)
        Console.WriteLine(
            $"  {item.Id}: {item.Name} [{item.Type}] occupation={item.Occupation ?? "-"} location={item.CurrentLocationId?.ToString() ?? "-"}");

    Console.WriteLine($"Interactions ({world.Interactions.Count}):");
    foreach (var item in world.Interactions)
        Console.WriteLine(
            $"  {item.Id}: day {item.CampaignDay?.ToString() ?? "?"}, {item.Participants}: {item.Summary}");

    Console.WriteLine($"Memories ({world.Memories.Count}) [DM VIEW — MAY CONTAIN SECRETS]:");
    foreach (var item in world.Memories)
        Console.WriteLine(
            $"  {item.Id}: {item.OwnerName}, importance={item.Importance}, embedding={(item.HasEmbedding ? "yes" : "pending")}{(item.IsSecret ? " [SECRET]" : "")}: {item.Content}");
}

static void ShowUsage()
{
    Console.WriteLine("NPCodex developer tool");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- check");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- create-campaign \"My Campaign\"");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- seed-demo <campaign-id>");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- show-world <campaign-id>");
    Console.WriteLine();
    Console.WriteLine("Connection: localhost:5432, database/user npcodex.");
    Console.WriteLine("Password: NPCodex root .env file (or NPCODEX_DB_PASSWORD override).");
    Console.WriteLine("seed-demo only adds fictional example data, and refuses to repeat for the same campaign.");
}
