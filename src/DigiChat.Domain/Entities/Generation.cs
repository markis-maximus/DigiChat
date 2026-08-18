namespace DigiChat.Domain.Entities;

/// <summary>
/// One in-game Digimon lifetime. Reincarnation ends the current generation and
/// creates the next. Lineage uniqueness is scoped to a generation.
/// </summary>
public class Generation
{
    public int Id { get; set; }

    /// <summary>Monotonically increasing generation number (1-based).</summary>
    public int Number { get; set; }

    public DateTime StartedUtc { get; set; }

    /// <summary>Set when a later generation supersedes this one.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>
    /// Set when every Digimon of this generation has been killed. The generation
    /// stays dead — indefinitely, across restarts — until it is either undone or
    /// reincarnated into the next one. Reincarnation is only legal while dead.
    /// </summary>
    public DateTime? DiedUtc { get; set; }

    /// <summary>
    /// Legacy: set when a reincarnation was undone, back when that was allowed.
    /// Reincarnation is final now, so nothing writes this; it is kept so existing
    /// rows keep their meaning.
    /// </summary>
    public DateTime? UndoneUtc { get; set; }

    public ICollection<ViewerGenerationAssignment> Assignments { get; set; } = new List<ViewerGenerationAssignment>();
}
