using Npgsql;
using NpgsqlTypes;

namespace NPCodex.Data;

public sealed record LocationDeletionImpact(int Locations, int AssignedCharacters, int Interactions);

public sealed partial class NPCodexDatabase
{
    /// <summary>
    /// Reparent one location within the same campaign; reject cycles (including
    /// moving a location into its own descendants).
    /// </summary>
    public async Task MoveLocationAsync(
        long campaignId, long locationId, long? parentLocationId,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (locationId <= 0 || parentLocationId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(locationId));
        if (locationId == parentLocationId)
            throw new InvalidOperationException("A location cannot be its own parent.");

        const string sql = """
            WITH RECURSIVE descendants(id) AS (
                SELECT location_id FROM locations
                WHERE campaign_id = $1 AND location_id = $2
                UNION
                SELECT child.location_id FROM locations child
                JOIN descendants d ON child.parent_location_id = d.id
                WHERE child.campaign_id = $1
            )
            UPDATE locations
            SET parent_location_id = $3
            WHERE campaign_id = $1 AND location_id = $2
              AND ($3::bigint IS NULL OR EXISTS (
                  SELECT 1 FROM locations parent
                  WHERE parent.campaign_id = $1 AND parent.location_id = $3
              ))
              AND NOT EXISTS (
                  SELECT 1 FROM descendants WHERE id = $3
              )
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(locationId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = (object?)parentLocationId ?? DBNull.Value
        });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                "Cannot move location: the target does not exist in this campaign " +
                "or is a child of the location being moved.");
    }

    public async Task UpdateLocationAsync(
        long campaignId, long locationId, string name,
        string type, string? description,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (locationId <= 0) throw new ArgumentOutOfRangeException(nameof(locationId));
        RequireText(name, nameof(name));
        RequireText(type, nameof(type));
        if (name.Trim().Length > 200 || type.Trim().Length > 100)
            throw new ArgumentException("Location name and type must be reasonably short.");
        const string sql = """
            UPDATE locations
            SET name = $3, location_type = $4, description = $5
            WHERE campaign_id = $1 AND location_id = $2
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(locationId);
        cmd.Parameters.AddWithValue(name.Trim());
        cmd.Parameters.AddWithValue(type.Trim());
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)description?.Trim() ?? DBNull.Value
        });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Location not found in this campaign.");
    }

    /// <summary>
    /// Counts only the subtree actually being deleted. No other campaign can
    /// be affected because every recursive step is campaign-scoped.
    /// </summary>
    public async Task<LocationDeletionImpact> GetLocationDeletionImpactAsync(
        long campaignId, long locationId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (locationId <= 0) throw new ArgumentOutOfRangeException(nameof(locationId));
        const string sql = """
            WITH RECURSIVE subtree(id) AS (
                SELECT location_id FROM locations
                WHERE campaign_id = $1 AND location_id = $2
                UNION
                SELECT l.location_id FROM locations l
                JOIN subtree s ON l.parent_location_id = s.id
                WHERE l.campaign_id = $1
            )
            SELECT (SELECT count(*) FROM subtree),
                   (SELECT count(*) FROM characters c
                    WHERE c.campaign_id = $1 AND c.current_location_id IN (SELECT id FROM subtree)),
                   (SELECT count(*) FROM interactions i
                    WHERE i.campaign_id = $1 AND i.location_id IN (SELECT id FROM subtree))
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(locationId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new LocationDeletionImpact(0, 0, 0);
        return new LocationDeletionImpact(
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)));
    }

    /// <summary>
    /// Permanent recursive deletion. Preserve characters, interactions and
    /// memories; clear their location links before removing the subtree.
    /// Delete location-specific roles, since those assignments no longer exist.
    /// </summary>
    public async Task<int> DeleteLocationSubtreeAsync(
        long campaignId, long locationId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (locationId <= 0) throw new ArgumentOutOfRangeException(nameof(locationId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        async Task<int> ExecuteAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            if (sql.Contains("$1", StringComparison.Ordinal))
                cmd.Parameters.AddWithValue(campaignId);
            if (sql.Contains("$2", StringComparison.Ordinal))
                cmd.Parameters.AddWithValue(locationId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Use a temporary ID table so every operation acts on exactly the same
        // subtree within this one database transaction.
        await ExecuteAsync("""
            CREATE TEMP TABLE location_delete_ids (
                id BIGINT PRIMARY KEY
            ) ON COMMIT DROP
            """);
        await ExecuteAsync("""
            WITH RECURSIVE subtree(id) AS (
                SELECT location_id FROM locations
                WHERE campaign_id = $1 AND location_id = $2
                UNION
                SELECT l.location_id FROM locations l
                JOIN subtree s ON l.parent_location_id = s.id
                WHERE l.campaign_id = $1
            )
            INSERT INTO location_delete_ids(id)
            SELECT id FROM subtree
            """);

        await ExecuteAsync("""
            UPDATE characters SET current_location_id = NULL
            WHERE campaign_id = $1
              AND current_location_id IN (SELECT id FROM location_delete_ids)
            """);
        await ExecuteAsync("""
            UPDATE interactions SET location_id = NULL
            WHERE campaign_id = $1
              AND location_id IN (SELECT id FROM location_delete_ids)
            """);
        await ExecuteAsync("""
            DELETE FROM character_location_roles
            WHERE campaign_id = $1
              AND location_id IN (SELECT id FROM location_delete_ids)
            """);
        var deleted = await ExecuteAsync("""
            DELETE FROM locations
            WHERE campaign_id = $1 AND location_id IN (SELECT id FROM location_delete_ids)
            """);
        if (deleted == 0)
            throw new InvalidOperationException("The selected location no longer exists.");
        await tx.CommitAsync(cancellationToken);
        return deleted;
    }
}
