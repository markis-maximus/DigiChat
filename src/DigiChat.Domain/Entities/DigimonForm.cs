namespace DigiChat.Domain.Entities;

/// <summary>One stage of one lineage, e.g. the Rookie form "Agumon".</summary>
public class DigimonForm
{
    public int Id { get; set; }

    public int LineageId { get; set; }
    public Lineage Lineage { get; set; } = null!;

    public DigivolutionStage Stage { get; set; }

    /// <summary>Official/common English name shown on the overlay label.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Key into the overlay asset manifest (e.g. "agumon"). The renderer resolves
    /// this to a spritesheet + animations, falling back to placeholder art.
    /// </summary>
    public string AssetKey { get; set; } = null!;
}
