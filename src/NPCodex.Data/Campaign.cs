namespace NPCodex.Data;

public sealed record Campaign(
    long Id,
    string Name,
    string? GameSystem,
    string? SettingName,
    string? Description,
    DateTime CreatedAtUtc);
