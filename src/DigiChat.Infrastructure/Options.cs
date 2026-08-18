namespace DigiChat.Infrastructure;

public class DataOptions
{
    public const string Section = "Data";
    /// <summary>Path to the editable lineage roster JSON (relative to content root).</summary>
    public string LineageFile { get; set; } = "data/lineages.json";
}

public class TwitchOptions
{
    public const string Section = "Twitch";

    /// <summary>Twitch application Client ID (public, safe in config).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Twitch user IDs whose messages never admit a participant (bots).</summary>
    public string[] IgnoredUserIds { get; set; } = [];

    /// <summary>When true, no Twitch connection is made; use the mock simulator.</summary>
    public bool MockMode { get; set; } = false;

    /// <summary>Where OAuth tokens are persisted (DPAPI-protected). Relative to content root.</summary>
    public string TokenFile { get; set; } = "twitch-tokens.json";
}

public class AdmissionOptions
{
    public const string Section = "Admissions";

    /// <summary>
    /// Bounds viewer rows and admin/SignalR projection size during a bot swarm.
    /// This is deliberately well above the 30 visible-lineage pool.
    /// </summary>
    public int MaxParticipantsPerSession { get; set; } = 500;
}

public class TransitionOptions
{
    public const string Section = "Transitions";

    /// <summary>How long the overlay's digivolution effect runs; admin controls and
    /// spawn broadcasts are held for this window.</summary>
    public double StageChangeSeconds { get; set; } = 4;

    /// <summary>How long the dying animation runs. They stay dead afterwards —
    /// this only covers the visual, so the controls unlock again for Reincarnate.</summary>
    public double DeathSeconds { get; set; } = 4;

    /// <summary>Egg → hatch sequence duration.</summary>
    public double ReincarnationSeconds { get; set; } = 12;
}

public class OverlayOptions
{
    public const string Section = "Overlay";
    public bool LabelsEnabled { get; set; } = true;
}
