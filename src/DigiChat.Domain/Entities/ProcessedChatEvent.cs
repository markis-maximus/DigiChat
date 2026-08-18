namespace DigiChat.Domain.Entities;

/// <summary>
/// EventSub idempotency ledger. Twitch may redeliver a notification; the
/// unique message ID constraint guarantees a duplicate can never admit a
/// participant or consume a lineage twice. Rows older than a day are pruned.
/// </summary>
public class ProcessedChatEvent
{
    public int Id { get; set; }

    /// <summary>EventSub message ID (Twitch-Eventsub-Message-Id / metadata.message_id).</summary>
    public string MessageId { get; set; } = null!;

    public DateTime ReceivedUtc { get; set; }
}
