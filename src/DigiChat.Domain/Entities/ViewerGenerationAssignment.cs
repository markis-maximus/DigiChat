namespace DigiChat.Domain.Entities;

/// <summary>
/// Binds one viewer to one lineage for one generation. A row with a null
/// <see cref="LineageId"/> represents the "awaiting lineage assignment"
/// overflow state (pool exhausted). Uniqueness within a generation is enforced
/// by the database: one row per (Generation, Viewer) and at most one viewer
/// per (Generation, Lineage).
/// </summary>
public class ViewerGenerationAssignment
{
    public int Id { get; set; }

    public int ViewerId { get; set; }
    public Viewer Viewer { get; set; } = null!;

    public int GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;

    /// <summary>Null while the viewer is awaiting a lineage (pool exhausted).</summary>
    public int? LineageId { get; set; }
    public Lineage? Lineage { get; set; }

    public DateTime AssignedUtc { get; set; }
}
