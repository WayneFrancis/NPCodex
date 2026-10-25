namespace NPCodex.Data;

public sealed record ApprovedScene(
    long InteractionId,
    long SessionId,
    long? ReplyNpcMemoryId,
    IReadOnlyList<long> AllMemoryIds);

/// <summary>
/// The game's clock is deliberately distinct from the computer's date/time.
/// Day numbers start at 1; null date/time on old records means unknown, not now.
/// </summary>
public sealed record WorldMoment(int Day, TimeOnly Time)
{
    public override string ToString() => $"Day {Day} at {Time:HH:mm}";
}
