namespace DigiChat.Domain.Entities;

/// <summary>
/// A curated five-stage Digimon family (Fresh → Ultimate). Lineages are data,
/// seeded from an editable JSON file — never application logic.
/// </summary>
public class Lineage
{
    public int Id { get; set; }

    /// <summary>Stable machine key, e.g. "agumon-line". Seed upserts match on this.</summary>
    public string Slug { get; set; } = null!;

    /// <summary>Human-facing name for admin UI, e.g. "Agumon line".</summary>
    public string Name { get; set; } = null!;

    /// <summary>Roster ordering for display; also the default assignment order.</summary>
    public int OrderIndex { get; set; }

    /// <summary>Disabled lineages are never assigned to new viewers.</summary>
    public bool Enabled { get; set; } = true;

    // Extensible metadata (spec §10): all optional, all data-driven.
    public string? SourceMedia { get; set; }
    public string? Canonicality { get; set; }
    public string? Notes { get; set; }
    public AssetReadiness AssetReadiness { get; set; } = AssetReadiness.Placeholder;

    public ICollection<DigimonForm> Forms { get; set; } = new List<DigimonForm>();
}

public enum AssetReadiness
{
    /// <summary>No real art; renderer uses generated placeholder sprites.</summary>
    Placeholder = 0,
    /// <summary>Some real animations present, fallbacks cover the rest.</summary>
    Partial = 1,
    /// <summary>Full animation set available.</summary>
    Complete = 2,
}
