namespace DigiChat.Domain.Entities;

/// <summary>
/// One explicit streaming session, started manually from the admin UI.
/// Participation is scoped to a session; lineage assignments are not.
/// A backend/OBS crash never creates a session — only the admin button does.
/// </summary>
public class StreamSession
{
    public int Id { get; set; }

    /// <summary>Monotonically increasing session number (1-based).</summary>
    public int Number { get; set; }

    public DateTime StartedUtc { get; set; }

    /// <summary>Set when a later session supersedes this one.</summary>
    public DateTime? EndedUtc { get; set; }

    public ICollection<StreamSessionParticipant> Participants { get; set; } = new List<StreamSessionParticipant>();
}
