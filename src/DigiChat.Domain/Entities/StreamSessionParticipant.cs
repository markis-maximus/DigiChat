namespace DigiChat.Domain.Entities;

/// <summary>
/// A viewer admitted to one stream session by their first qualifying chat
/// message. Once admitted they remain visible for the whole session; there is
/// no inactivity timeout and no duplicate admission.
/// </summary>
public class StreamSessionParticipant
{
    public int Id { get; set; }

    public int StreamSessionId { get; set; }
    public StreamSession StreamSession { get; set; } = null!;

    public int ViewerId { get; set; }
    public Viewer Viewer { get; set; } = null!;

    public DateTime JoinedUtc { get; set; }

    /// <summary>
    /// The viewer first spoke while the current generation was dead. Persisted
    /// explicitly so restart/reload behavior never depends on equal timestamps.
    /// Cleared when death is undone and a lineage is assigned.
    /// </summary>
    public bool HeldForReincarnation { get; set; }
}
