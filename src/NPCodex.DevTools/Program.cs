using NPCodex.AI;
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
    "seed-demo" or "show-world" or "embed-memories" =>
        args.Length == 2 && IsPositiveId(args[1]),
    "search-memories" or "npc-chat" => args.Length == 4 &&
        IsPositiveId(args[1]) && IsPositiveId(args[2]) &&
        !string.IsNullOrWhiteSpace(args[3]),
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

    var conversationsInstalled = status.InitialMigrationInstalled
        && await db.HasConversationSchemaAsync();
    Console.WriteLine($"Conversation migration: {(conversationsInstalled ? "installed" : "NOT installed")}");

    var personalityInstalled = conversationsInstalled && await db.HasPersonalitySchemaAsync();
    Console.WriteLine($"Personality migration: {(personalityInstalled ? "installed" : "NOT installed")}");

    var aliasesInstalled = personalityInstalled && await db.HasCharacterAliasSchemaAsync();
    Console.WriteLine($"Character alias migration: {(aliasesInstalled ? "installed" : "NOT installed")}");

    var worldInstalled = aliasesInstalled && await db.HasWorldEventsSchemaAsync();
    Console.WriteLine($"World event migration: {(worldInstalled ? "installed" : "NOT installed")}");

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
        case "embed-memories":
        {
            var campaignId = long.Parse(args[1]);
            using var ollama = new OllamaEmbeddingClient();
            var service = new NpcMemoryService(db, ollama);
            var result = await service.EmbedPendingAsync(campaignId);
            Console.WriteLine(
                $"Memories requiring embeddings: {result.Found}; stored: {result.Stored}; skipped: {result.Skipped}.");
            Console.WriteLine(
                $"Embedding model: {OllamaEmbeddingClient.ModelName} ({OllamaEmbeddingClient.Dimensions} dimensions).");
            break;
        }
        case "search-memories":
        {
            var campaignId = long.Parse(args[1]);
            var npcId = long.Parse(args[2]);
            var query = args[3];
            using var ollama = new OllamaEmbeddingClient();
            var service = new NpcMemoryService(db, ollama);
            var matches = await service.SearchAsync(campaignId, npcId, query);
            Console.WriteLine($"Semantic memory matches for NPC {npcId} (campaign {campaignId}):");
            if (matches.Count == 0)
                Console.WriteLine("  No embedded memories for this NPC and model. Run embed-memories first.");
            foreach (var memory in matches)
            {
                Console.WriteLine(
                    $"  Memory {memory.Id}: cosine distance={memory.CosineDistance:F4}, importance={memory.Importance}" +
                    (memory.IsSecret ? " [DM SECRET]" : ""));
                Console.WriteLine($"    {memory.Content}");
            }
            Console.WriteLine("DM-only search; results are NOT suitable for display to players.");
            break;
        }
        case "npc-chat":
        {
            var campaignId = long.Parse(args[1]);
            var npcId = long.Parse(args[2]);
            var playerSays = args[3];

            using var embedClient = new OllamaEmbeddingClient();
            using var chatClient = new OllamaChatClient();
            var memoryService = new NpcMemoryService(db, embedClient);
            var dialogueService = new NpcDialogueService(db, memoryService, chatClient);
            var preview = await dialogueService.PreviewReplyAsync(
                campaignId, npcId, playerSays);

            Console.WriteLine();
            Console.WriteLine($"NPC: {preview.Npc.Name} (#{preview.Npc.CharacterId})");
            Console.WriteLine($"Chat model: {preview.ModelName}");
            Console.WriteLine();
            Console.WriteLine("--- DM PREVIEW: NPC REPLY ---");
            Console.WriteLine(preview.Reply);
            Console.WriteLine("--- END PREVIEW ---");
            Console.WriteLine();
            Console.WriteLine($"Retrieved non-secret memories ({preview.RetrievedMemories.Count}):");
            foreach (var memory in preview.RetrievedMemories)
                Console.WriteLine(
                    $"  #{memory.Id} cosine distance {memory.CosineDistance:F4}: {memory.Content}");

            if (preview.RetrievedMemories.Count == 0)
                Console.WriteLine("  None: check that NPC memories were embedded.");
            Console.WriteLine("This preview is NOT saved to PostgreSQL. Review it before using it in play.");
            break;
        }
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
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- embed-memories <campaign-id>");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- search-memories <campaign-id> <npc-id> \"your question\"");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- npc-chat <campaign-id> <npc-id> \"player's spoken line\"");
    Console.WriteLine();
    Console.WriteLine("Connection: localhost:5432, database/user npcodex.");
    Console.WriteLine("Password: NPCodex root .env file (or NPCODEX_DB_PASSWORD override).");
    Console.WriteLine("seed-demo only adds fictional example data, and refuses to repeat for the same campaign.");
}
