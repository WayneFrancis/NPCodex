using System.Text;
using NPCodex.Data;
using Npgsql;

if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
{
    ShowUsage();
    return;
}

if (args.Length > 0 && args[0] != "check" && args[0] != "create-campaign")
{
    ShowUsage();
    Environment.ExitCode = 2;
    return;
}

if (args.Length > 0 && args[0] == "create-campaign" &&
    (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])))
{
    ShowUsage();
    Environment.ExitCode = 2;
    return;
}

if (args.Length > 1 && args[0] == "check")
{
    ShowUsage();
    Environment.ExitCode = 2;
    return;
}

try
{
    // Environment variable is optional; when omitted the password is read
    // interactively without echoing it to the terminal or command history.
    var password = Environment.GetEnvironmentVariable("NPCODEX_DB_PASSWORD")
        ?? ReadPassword();

    var connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = 5432,
        Database = "npcodex",
        Username = "npcodex",
        Password = password,
        Timeout = 5
    }.ConnectionString;

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

    if (args.Length == 2 && args[0] == "create-campaign")
    {
        var id = await db.CreateCampaignAsync(args[1]);
        Console.WriteLine($"Created campaign {id}: {args[1]}");
    }

    var campaigns = await db.GetCampaignsAsync();
    Console.WriteLine($"Campaigns ({campaigns.Count}):");
    foreach (var campaign in campaigns)
        Console.WriteLine($"  {campaign.Id}: {campaign.Name}");

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

static void ShowUsage()
{
    Console.WriteLine("NPCodex developer tool");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- check");
    Console.WriteLine("  dotnet run --project src/NPCodex.DevTools -- create-campaign \"My Campaign\"");
    Console.WriteLine();
    Console.WriteLine("Connection: localhost:5432, database/user npcodex.");
    Console.WriteLine("Password: hidden interactive prompt (or NPCODEX_DB_PASSWORD).");
}

static string ReadPassword()
{
    if (Console.IsInputRedirected)
        throw new InvalidOperationException(
            "For noninteractive runs, set NPCODEX_DB_PASSWORD in the environment.");

    Console.Write("PostgreSQL password: ");
    var value = new StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return value.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            value.Length--;
        else if (!char.IsControl(key.KeyChar) && key.Key != ConsoleKey.Backspace)
            value.Append(key.KeyChar);
    }
}
