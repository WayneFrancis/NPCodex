using Npgsql;

namespace NPCodex.Data;

/// <summary>
/// Single, pool-backed access point for the local NPCodex PostgreSQL database.
/// Reuse an instance throughout the lifetime of the application.
/// </summary>
public sealed class NPCodexDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NPCodexDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<DatabaseStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT current_setting('server_version'),
                   COALESCE((SELECT extversion
                             FROM pg_extension
                             WHERE extname = 'vector'), 'not installed'),
                   (SELECT COUNT(*)::integer
                    FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_type = 'BASE TABLE'),
                   COALESCE((SELECT TRUE
                             FROM information_schema.tables
                             WHERE table_schema = 'public'
                               AND table_name = 'schema_migrations'), FALSE)
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("PostgreSQL returned no database status.");

        var version = reader.GetString(0);
        var pgvector = reader.GetString(1);
        var count = reader.GetInt32(2);
        var hasMigrationsTable = reader.GetBoolean(3);

        if (!hasMigrationsTable)
            return new DatabaseStatus(version, pgvector, count, false);

        await reader.DisposeAsync();

        await using var migrationCommand = _dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = '0001_initial_schema')");
        var installed = (bool)(await migrationCommand.ExecuteScalarAsync(cancellationToken)
                               ?? false);

        return new DatabaseStatus(version, pgvector, count, installed);
    }

    public async Task<IReadOnlyList<Campaign>> GetCampaignsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT campaign_id, name, game_system, setting_name, description, created_at
            FROM campaigns
            ORDER BY campaign_id
            """;

        var campaigns = new List<Campaign>();
        await using var command = _dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            campaigns.Add(new Campaign(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5)));
        }

        return campaigns;
    }

    public async Task<long> CreateCampaignAsync(
        string name,
        string? gameSystem = null,
        string? settingName = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A campaign name is required.", nameof(name));

        const string sql = """
            INSERT INTO campaigns (name, game_system, setting_name, description)
            VALUES ($1, $2, $3, $4)
            RETURNING campaign_id
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = name.Trim() });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)gameSystem ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)settingName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)description ?? DBNull.Value });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id
            ? id
            : throw new InvalidOperationException("PostgreSQL did not return a campaign ID.");
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
