namespace NPCodex.Data;

public sealed record ApprovedScene(
    long InteractionId,
    long SessionId,
    long? ReplyNpcMemoryId,
    IReadOnlyList<long> AllMemoryIds);

/// <summary>
/// The game's clock is deliberately distinct from the computer's date/time.
/// Day 1 is the first campaign day; negative days and day 0 are valid
/// pre-campaign chronology. Null old dates mean unknown, not now.
/// </summary>
public sealed record WorldMoment(int Day, TimeOnly Time)
{
    public override string ToString() => $"Day {Day} at {Time:HH:mm}";
}
