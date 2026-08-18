namespace DigiChat.Domain;

/// <summary>
/// The five Digimon World 1 stages shown on the overlay. Any stage may jump
/// directly to any other stage; there is no enforced ordering.
/// </summary>
public enum DigivolutionStage
{
    Fresh = 0,
    InTraining = 1,
    Rookie = 2,
    Champion = 3,
    Ultimate = 4,
}

public static class DigivolutionStageExtensions
{
    /// <summary>Human-readable name, matching Digimon World 1 terminology.</summary>
    public static string DisplayName(this DigivolutionStage stage) => stage switch
    {
        DigivolutionStage.Fresh => "Fresh",
        DigivolutionStage.InTraining => "In-Training",
        DigivolutionStage.Rookie => "Rookie",
        DigivolutionStage.Champion => "Champion",
        DigivolutionStage.Ultimate => "Ultimate",
        _ => stage.ToString(),
    };
}
