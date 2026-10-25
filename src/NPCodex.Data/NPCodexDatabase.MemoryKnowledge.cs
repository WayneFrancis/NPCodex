namespace NPCodex.Data;

public sealed partial class NPCodexDatabase
{
    public async Task<bool> HasMemoryProvenanceSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM schema_migrations
                WHERE version = '0007_memory_provenance'
            )
            """);
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }
}
