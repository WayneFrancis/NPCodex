namespace NPCodex.Data;

public sealed record DatabaseStatus(
    string PostgreSqlVersion,
    string PgvectorVersion,
    int PublicTableCount,
    bool InitialMigrationInstalled);
