using Npgsql;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    /// <summary>All queries are scoped to exactly one campaign.</summary>
    public async Task<CampaignWorld> GetCampaignWorldAsync(
        long campaignId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        var locations = new List<LocationInfo>();
        var characters = new List<CharacterInfo>();
        var interactions = new List<InteractionInfo>();
        var memories = new List<MemoryInfo>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = BuildCommand("""
            SELECT location_id, parent_location_id, name, location_type, description
            FROM locations
            WHERE campaign_id = $1
            ORDER BY location_id
            """, connection, null, campaignId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                locations.Add(new LocationInfo(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        await using (var command = BuildCommand("""
            SELECT character_id, character_type, name, occupation, current_location_id
            FROM characters
            WHERE campaign_id = $1
            ORDER BY character_id
            """, connection, null, campaignId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                characters.Add(new CharacterInfo(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        await using (var command = BuildCommand("""
            SELECT i.interaction_id, i.campaign_day, i.summary,
                   COALESCE(string_agg(c.name, ', ' ORDER BY c.name), '')
            FROM interactions i
            LEFT JOIN interaction_participants ip
                   ON ip.campaign_id = i.campaign_id
                  AND ip.interaction_id = i.interaction_id
            LEFT JOIN characters c
                   ON c.campaign_id = ip.campaign_id
                  AND c.character_id = ip.character_id
            WHERE i.campaign_id = $1
            GROUP BY i.interaction_id, i.campaign_day, i.summary
            ORDER BY i.interaction_id
            """, connection, null, campaignId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                interactions.Add(new InteractionInfo(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetString(2), reader.GetString(3)));
        }

        // DM-facing inspection only: includes secret memories. Not player-safe.
        await using (var command = BuildCommand("""
            SELECT m.memory_id, c.name, m.content,
                   m.importance, m.is_secret, m.embedding IS NOT NULL
            FROM memories m
            INNER JOIN characters c
                    ON c.campaign_id = m.campaign_id
                   AND c.character_id = m.owner_character_id
            WHERE m.campaign_id = $1
            ORDER BY m.memory_id
            """, connection, null, campaignId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                memories.Add(new MemoryInfo(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt16(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }

        return new CampaignWorld(campaignId, locations, characters, interactions, memories);
    }

    public async Task<long> CreateLocationAsync(
        long campaignId, string name, string type, long? parentLocationId = null,
        string? description = null, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(name, nameof(name));
        RequireText(type, nameof(type));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await InsertIdAsync("""
            INSERT INTO locations (campaign_id, parent_location_id, name, location_type, description)
            VALUES ($1, $2, $3, $4, $5) RETURNING location_id
            """, connection, null, cancellationToken,
            campaignId, parentLocationId, name.Trim(), type.Trim(), description);
    }

    public async Task<long> CreateCharacterAsync(
        long campaignId, string name, string characterType,
        long? currentLocationId = null, string? occupation = null,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        RequireText(name, nameof(name));
        if (characterType is not ("npc" or "pc"))
            throw new ArgumentException("Character type must be 'npc' or 'pc'.", nameof(characterType));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await InsertIdAsync("""
            INSERT INTO characters (campaign_id, character_type, name, occupation, current_location_id)
            VALUES ($1, $2, $3, $4, $5) RETURNING character_id
            """, connection, null, cancellationToken,
            campaignId, characterType, name.Trim(), occupation, currentLocationId);
    }

    /// <summary>
    /// Records an interaction and what one participating NPC learned, atomically.
    /// Optional playerCharacterId refers to an existing character of the same campaign.
    /// </summary>
    public async Task<long> RecordNpcInteractionAsync(
        long campaignId, long npcId, long? playerCharacterId,
        long? locationId, string summary, string npcMemory,
        int? campaignDay = null, short memoryImportance = 3,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (npcId <= 0 || playerCharacterId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(npcId), "Character IDs must be positive.");
        if (playerCharacterId == npcId)
            throw new ArgumentException("NPC and player character must be different.");
        if (campaignDay is < 0 || memoryImportance is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(campaignDay), "Invalid day or memory importance.");
        RequireText(summary, nameof(summary));
        RequireText(npcMemory, nameof(npcMemory));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var interactionId = await InsertIdAsync("""
            INSERT INTO interactions (campaign_id, location_id, campaign_day, summary)
            VALUES ($1, $2, $3, $4) RETURNING interaction_id
            """, connection, transaction, cancellationToken,
            campaignId, locationId, campaignDay, summary.Trim());

        await ExecuteSqlAsync("""
            INSERT INTO interaction_participants (campaign_id, interaction_id, character_id)
            VALUES ($1, $2, $3)
            """, connection, transaction, cancellationToken,
            campaignId, interactionId, npcId);

        if (playerCharacterId.HasValue)
            await ExecuteSqlAsync("""
                INSERT INTO interaction_participants (campaign_id, interaction_id, character_id)
                VALUES ($1, $2, $3)
                """, connection, transaction, cancellationToken,
                campaignId, interactionId, playerCharacterId.Value);

        await ExecuteSqlAsync("""
            INSERT INTO memories (campaign_id, owner_character_id, source_interaction_id,
                                  content, memory_type, importance)
            VALUES ($1, $2, $3, $4, 'experience', $5)
            """, connection, transaction, cancellationToken,
            campaignId, npcId, interactionId, npcMemory.Trim(), memoryImportance);

        await transaction.CommitAsync(cancellationToken);
        return interactionId;
    }

    /// <summary>
    /// Adds a small fictional town, shop, NPC, sample PC and one shared encounter.
    /// All data is inserted in one transaction. Refuses to run twice in the same campaign.
    /// Only invoked explicitly by the DevTools seed-demo command.
    /// </summary>
    public async Task<DemoWorldIds> SeedDemoAsync(
        long campaignId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize demo seeding for this campaign and check the campaign exists.
        await using (var lockCommand = BuildCommand(
            "SELECT campaign_id FROM campaigns WHERE campaign_id = $1 FOR UPDATE",
            connection, transaction, campaignId))
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException($"Campaign {campaignId} was not found.");
        }

        await using (var checkCommand = BuildCommand("""
            SELECT EXISTS (
                SELECT 1 FROM locations
                WHERE campaign_id = $1
                  AND name = 'Willowford'
                  AND location_type = 'town'
            )
            """, connection, transaction, campaignId))
        {
            if (await checkCommand.ExecuteScalarAsync(cancellationToken) is true)
                throw new InvalidOperationException(
                    "A demo town named Willowford already exists in this campaign. Nothing was added.");
        }

        var townId = await InsertIdAsync("""
            INSERT INTO locations (campaign_id, name, location_type, description)
            VALUES ($1, $2, 'town', $3) RETURNING location_id
            """, connection, transaction, cancellationToken,
            campaignId, "Willowford",
            "A small trade town beside a river, on the road to the northern ruins.");

        var shopId = await InsertIdAsync("""
            INSERT INTO locations (campaign_id, parent_location_id, name, location_type, description)
            VALUES ($1, $2, $3, 'shop', $4) RETURNING location_id
            """, connection, transaction, cancellationToken,
            campaignId, townId, "The Silver Lantern",
            "A provisions shop selling trail supplies, maps and assorted curiosities.");

        var merchantId = await InsertIdAsync("""
            INSERT INTO characters (campaign_id, character_type, name, occupation, biography,
                                    current_location_id)
            VALUES ($1, 'npc', $2, $3, $4, $5) RETURNING character_id
            """, connection, transaction, cancellationToken,
            campaignId, "Mira Vale", "Merchant",
            "Owner of the Silver Lantern; observant and careful about debts.", shopId);

        var playerId = await InsertIdAsync("""
            INSERT INTO characters (campaign_id, character_type, name, occupation, current_location_id)
            VALUES ($1, 'pc', $2, $3, $4) RETURNING character_id
            """, connection, transaction, cancellationToken,
            campaignId, "Rowan (example PC)", "Adventurer", shopId);

        await ExecuteSqlAsync("""
            INSERT INTO character_location_roles (campaign_id, character_id, location_id, role)
            VALUES ($1, $2, $3, 'owner')
            """, connection, transaction, cancellationToken,
            campaignId, merchantId, shopId);

        var interactionId = await InsertIdAsync("""
            INSERT INTO interactions (campaign_id, location_id, campaign_day, summary)
            VALUES ($1, $2, $3, $4) RETURNING interaction_id
            """, connection, transaction, cancellationToken,
            campaignId, shopId, 1,
            "Rowan asked Mira to reserve a map of the northern ruins; Mira agreed to hold it for 2 gold.");

        foreach (var characterId in new[] { merchantId, playerId })
            await ExecuteSqlAsync("""
                INSERT INTO interaction_participants (campaign_id, interaction_id, character_id)
                VALUES ($1, $2, $3)
                """, connection, transaction, cancellationToken,
                campaignId, interactionId, characterId);

        await ExecuteSqlAsync("""
            INSERT INTO memories (campaign_id, owner_character_id, source_interaction_id,
                                  content, memory_type, importance)
            VALUES ($1, $2, $3, $4, 'experience', $5)
            """, connection, transaction, cancellationToken,
            campaignId, merchantId, interactionId,
            "Rowan promised to return with 2 gold for a map of the northern ruins. I am keeping the map behind the counter.",
            (short)4);

        await transaction.CommitAsync(cancellationToken);
        return new DemoWorldIds(townId, shopId, merchantId, playerId, interactionId);
    }

    private static void RequireCampaignId(long campaignId)
    {
        if (campaignId <= 0)
            throw new ArgumentOutOfRangeException(nameof(campaignId), "Campaign ID must be positive.");
    }

    private static void RequireText(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", argumentName);
    }

    private static NpgsqlCommand BuildCommand(
        string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static async Task<long> InsertIdAsync(
        string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = BuildCommand(sql, connection, transaction, values);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id
            ? id
            : throw new InvalidOperationException("Expected a generated BIGINT ID from PostgreSQL.");
    }

    private static async Task ExecuteSqlAsync(
        string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = BuildCommand(sql, connection, transaction, values);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
