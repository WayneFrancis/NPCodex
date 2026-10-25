using Npgsql;
using NpgsqlTypes;

namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasConversationBrowserSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = '0006_conversation_browser')");
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// When query is null, show statements spoken by OR heard by any roster
    /// member. With a query, search the whole current campaign and include
    /// relevance. No unapproved drafts appear in either view.
    /// </summary>
    public async Task<IReadOnlyList<StatementInfo>> GetStatementsAsync(
        long campaignId, IReadOnlyCollection<long> roster,
        string? query = null, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (limit is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var ids = roster.Distinct().Where(id => id > 0).ToArray();
        var searching = !string.IsNullOrWhiteSpace(query);
        if (!searching && ids.Length == 0)
            return Array.Empty<StatementInfo>();

        const string sql = """
            SELECT st.statement_id, st.world_day, st.world_time,
                   COALESCE(speaker.name, '(Unknown speaker)'),
                   COALESCE((
                       SELECT string_agg(person.name, ', ' ORDER BY person.name, person.character_id)
                       FROM characters person
                       WHERE person.campaign_id = st.campaign_id
                         AND (
                           person.character_id = st.speaker_character_id
                           OR EXISTS (
                             SELECT 1 FROM statement_hearers sh
                             WHERE sh.campaign_id = st.campaign_id
                               AND sh.statement_id = st.statement_id
                               AND sh.character_id = person.character_id
                           )
                         )
                   ), '(Unknown participants)'),
                   st.content,
                   CASE WHEN $3::text IS NOT NULL
                       THEN ts_rank_cd(to_tsvector('simple', st.content),
                           websearch_to_tsquery('simple', $3::text))
                       ELSE 0.0 END AS relevance
            FROM conversation_statements st
            LEFT JOIN characters speaker
              ON speaker.campaign_id = st.campaign_id
             AND speaker.character_id = st.speaker_character_id
            WHERE st.campaign_id = $1
              AND (
                ($3::text IS NULL AND (
                    st.speaker_character_id = ANY($2::bigint[])
                    OR EXISTS (
                        SELECT 1 FROM statement_hearers sh
                        WHERE sh.campaign_id = st.campaign_id
                          AND sh.statement_id = st.statement_id
                          AND sh.character_id = ANY($2::bigint[])
                    )))
                OR
                ($3::text IS NOT NULL AND (
                    to_tsvector('simple', st.content)
                         @@ websearch_to_tsquery('simple', $3::text)
                    OR st.content ILIKE ('%' || $3::text || '%')
                ))
              )
            ORDER BY
                CASE WHEN $3::text IS NOT NULL
                     THEN ts_rank_cd(to_tsvector('simple', st.content),
                          websearch_to_tsquery('simple', $3::text))
                     ELSE 0.0 END DESC,
                st.world_day DESC NULLS LAST,
                st.world_time DESC NULLS LAST,
                st.statement_id DESC
            LIMIT $4
            """;

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = searching ? query!.Trim() : DBNull.Value
        });
        cmd.Parameters.AddWithValue(limit);
        var results = new List<StatementInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new StatementInfo(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeOnly>(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                Convert.ToDouble(reader.GetValue(6))));
        return results;
    }

    public async Task<DndCharacterSheet> GetDndSheetAsync(
        long campaignId, long characterId, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        const string sql = """
            SELECT species, character_class, level, background, alignment,
                   strength, dexterity, constitution, intelligence, wisdom,
                   charisma, armor_class, hit_points, speed_feet,
                   features, equipment, notes
            FROM dnd_character_sheets
            WHERE campaign_id = $1 AND character_id = $2
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(characterId);
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await rd.ReadAsync(cancellationToken)) return new DndCharacterSheet();
        return new DndCharacterSheet(
            rd.GetString(0), rd.GetString(1), rd.GetInt32(2),
            rd.GetString(3), rd.GetString(4),
            rd.GetInt16(5), rd.GetInt16(6), rd.GetInt16(7),
            rd.GetInt16(8), rd.GetInt16(9), rd.GetInt16(10),
            rd.GetInt32(11), rd.GetInt32(12), rd.GetInt32(13),
            rd.GetString(14), rd.GetString(15), rd.GetString(16));
    }

    public async Task SaveDndSheetAsync(long campaignId, long characterId,
        DndCharacterSheet sheet, CancellationToken cancellationToken = default)
    {
        RequireCampaignId(campaignId);
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        ArgumentNullException.ThrowIfNull(sheet);
        const string sql = """
            INSERT INTO dnd_character_sheets (
                campaign_id, character_id,
                species, character_class, level, background, alignment,
                strength, dexterity, constitution, intelligence, wisdom, charisma,
                armor_class, hit_points, speed_feet, features, equipment, notes)
            SELECT $1, c.character_id,
                   $3, $4, $5, $6, $7,
                   $8, $9, $10, $11, $12, $13,
                   $14, $15, $16, $17, $18, $19
            FROM characters c
            WHERE c.campaign_id = $1 AND c.character_id = $2
            ON CONFLICT (campaign_id, character_id) DO UPDATE
            SET species = EXCLUDED.species,
                character_class = EXCLUDED.character_class,
                level = EXCLUDED.level,
                background = EXCLUDED.background,
                alignment = EXCLUDED.alignment,
                strength = EXCLUDED.strength,
                dexterity = EXCLUDED.dexterity,
                constitution = EXCLUDED.constitution,
                intelligence = EXCLUDED.intelligence,
                wisdom = EXCLUDED.wisdom,
                charisma = EXCLUDED.charisma,
                armor_class = EXCLUDED.armor_class,
                hit_points = EXCLUDED.hit_points,
                speed_feet = EXCLUDED.speed_feet,
                features = EXCLUDED.features,
                equipment = EXCLUDED.equipment,
                notes = EXCLUDED.notes
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        object?[] values = [
            campaignId, characterId, sheet.Species, sheet.CharacterClass,
            sheet.Level, sheet.Background, sheet.Alignment,
            sheet.Strength, sheet.Dexterity, sheet.Constitution,
            sheet.Intelligence, sheet.Wisdom, sheet.Charisma,
            sheet.ArmorClass, sheet.HitPoints, sheet.SpeedFeet,
            sheet.Features, sheet.Equipment, sheet.Notes
        ];
        foreach (var value in values)
            cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Character not found in this campaign.");
    }
}
