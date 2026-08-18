namespace DigiChat.Domain.Entities;

public enum TransitionType
{
    StageChange = 0,
    Reincarnation = 1,
    /// <summary>Everyone killed. Undoable; the step reincarnation requires.</summary>
    Death = 2,
}

/// <summary>
/// Append-only history of stage changes and reincarnations. Undo marks the
/// newest un-undone record rather than deleting anything, so history is never
/// destroyed and deeper undo remains possible later.
/// </summary>
public class TransitionRecord
{
    public int Id { get; set; }

    public TransitionType Type { get; set; }
    public DateTime OccurredUtc { get; set; }

    public DigivolutionStage FromStage { get; set; }
    public DigivolutionStage ToStage { get; set; }

    /// <summary>Populated for reincarnations only.</summary>
    public int? FromGenerationId { get; set; }
    public int? ToGenerationId { get; set; }

    /// <summary>Set when this transition has been undone.</summary>
    public DateTime? UndoneUtc { get; set; }
}
