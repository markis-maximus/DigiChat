namespace DigiChat.Domain.Entities;

/// <summary>
/// A permanent Twitch person. Identity is the immutable Twitch user ID;
/// login and display name are mutable decoration updated whenever newer
/// Twitch data arrives.
/// </summary>
public class Viewer
{
    public int Id { get; set; }

    /// <summary>Immutable Twitch user ID — the only trusted identity.</summary>
    public string TwitchUserId { get; set; } = null!;

    /// <summary>Current Twitch login (lowercase). May change over time.</summary>
    public string Login { get; set; } = null!;

    /// <summary>Current Twitch display name. May change over time.</summary>
    public string DisplayName { get; set; } = null!;

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public ICollection<ViewerGenerationAssignment> Assignments { get; set; } = new List<ViewerGenerationAssignment>();
}
