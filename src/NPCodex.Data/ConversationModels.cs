namespace NPCodex.Data;

public sealed record ConversationSession(
    long Id, long CampaignId, long NpcId, long? PlayerCharacterId,
    DateTime StartedAtUtc);

public sealed record ConversationTurn(
    long Id, long InteractionId, string PlayerLine,
    string NpcReply, DateTime ApprovedAtUtc)
{
    public long? SpeakerCharacterId { get; init; }
    public int? WorldDay { get; init; }
    public TimeOnly? WorldTime { get; init; }
}
