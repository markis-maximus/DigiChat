namespace DigiChat.Domain.Views;

/// <summary>
/// Wire-level shapes shared by the SignalR hub, REST admin API, and (mirrored
/// in TypeScript) the overlay/admin frontends. These are projections of
/// authoritative backend state — the overlay never computes its own truth.
/// </summary>
public sealed record ParticipantView(
    string TwitchUserId,
    string DisplayName,
    bool AwaitingLineage,
    string? LineageSlug,
    string? LineageName,
    string? FormName,
    string? AssetKey,
    DateTime JoinedUtc,
    /// <summary>
    /// Chatted while the generation was dead, so they are recorded but not on
    /// screen. They join the next reincarnation like everyone else.
    /// </summary>
    bool HeldForReincarnation = false);

public sealed record OverlayStateView(
    DigivolutionStage Stage,
    string StageName,
    int GenerationNumber,
    int? SessionNumber,
    bool LabelsEnabled,
    IReadOnlyList<ParticipantView> Participants,
    /// <summary>Everyone is dead and waiting to be reincarnated.</summary>
    bool IsDead = false);

/// <summary>Pushed to the overlay when a new participant is admitted mid-session.</summary>
public sealed record SpawnEventView(ParticipantView Participant, DigivolutionStage Stage);

/// <summary>Pushed to the overlay when the global stage changes (synchronized digivolve).</summary>
public sealed record StageChangeView(
    DigivolutionStage FromStage,
    DigivolutionStage ToStage,
    IReadOnlyList<ParticipantView> Participants);

/// <summary>Pushed when everyone is killed; they settle into a dead silhouette and stay.</summary>
public sealed record DeathView(IReadOnlyList<ParticipantView> Participants);

/// <summary>Pushed to the overlay for the reincarnation sequence (egg → hatch).</summary>
public sealed record ReincarnationView(
    int NewGenerationNumber,
    IReadOnlyList<ParticipantView> Participants);

public sealed record AdminStatusView(
    int GenerationNumber,
    int? SessionNumber,
    DigivolutionStage Stage,
    string StageName,
    string TwitchStatus,
    bool TransitionActive,
    int ParticipantCount,
    int AssignedLineages,
    int TotalLineages,
    int AwaitingLineageCount,
    string? LastUndoableAction,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ParticipantView> Participants,
    /// <summary>Dead: Kill is spent, Reincarnate is unlocked, Undo revives.</summary>
    bool IsDead = false,
    /// <summary>Chatters recorded during the death, waiting for the next egg.</summary>
    int HeldForReincarnationCount = 0,
    /// <summary>
    /// Optional optimistic token for Undo. A stale browser tab cannot consume
    /// the transition that became newest after its status snapshot.
    /// </summary>
    int? LastUndoableTransitionId = null,
    /// <summary>
    /// Monotonic per-process stamp, assigned after this snapshot's reads
    /// complete. Pushed broadcasts and explicit pulls race, so a delayed older
    /// push can arrive after a newer pull; the admin panel drops anything not
    /// newer than what it has already rendered.
    ///
    /// This orders whole snapshots against each other; it does NOT make one
    /// snapshot atomic. The projection issues several queries outside a
    /// transaction, so a slow read can interleave with a commit and produce a
    /// torn — but higher-stamped — snapshot. Self-heals on the next projection.
    /// Do not treat this as a consistency guarantee.
    /// </summary>
    long Revision = 0);
