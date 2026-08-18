namespace DigiChat.Domain.Entities;

/// <summary>
/// The single authoritative application state row (Id is always 1).
/// The backend owns this; the overlay only ever mirrors it.
/// </summary>
public class AppState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public int CurrentGenerationId { get; set; }
    public Generation CurrentGeneration { get; set; } = null!;

    /// <summary>Null until the first "Start New Stream Session" is clicked.</summary>
    public int? CurrentStreamSessionId { get; set; }
    public StreamSession? CurrentStreamSession { get; set; }

    public DigivolutionStage CurrentStage { get; set; } = DigivolutionStage.Fresh;

    public DateTime UpdatedUtc { get; set; }
}
