using DigiChat.Domain;
using DigiChat.Domain.Entities;
using DigiChat.Domain.Views;
using DigiChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DigiChat.Infrastructure.Services;

/// <summary>Reports the current Twitch connection state to the admin UI.</summary>
public interface ITwitchStatusProvider
{
    string Status { get; }
}

/// <summary>
/// Projects authoritative database state into the view records consumed by the
/// overlay and admin UI. Read-only; never mutates.
/// </summary>
public class OverlayStateService(
    IDbContextFactory<DigiChatDbContext> dbFactory,
    ITwitchStatusProvider twitchStatus,
    TransitionGate gate,
    IOptions<OverlayOptions> overlayOptions)
{
    public async Task<OverlayStateView> GetOverlayStateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await GetAppStateAsync(db, ct);
        var participants = await GetParticipantViewsAsync(db, state, state.CurrentStage, ct);
        return new OverlayStateView(
            state.CurrentStage,
            state.CurrentStage.DisplayName(),
            state.CurrentGeneration.Number,
            state.CurrentStreamSession?.Number,
            overlayOptions.Value.LabelsEnabled,
            participants,
            IsDead: state.CurrentGeneration.DiedUtc is not null);
    }

    /// <summary>Orders admin snapshots so a delayed push cannot overwrite a newer pull.</summary>
    private static long _adminStatusRevision;

    public async Task<AdminStatusView> GetAdminStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await GetAppStateAsync(db, ct);
        var participants = await GetParticipantViewsAsync(db, state, state.CurrentStage, ct);

        var assignedCount = await db.Assignments
            .CountAsync(a => a.GenerationId == state.CurrentGenerationId && a.LineageId != null, ct);
        var totalLineages = await db.Lineages.CountAsync(l => l.Enabled, ct);
        var awaiting = participants.Count(p => p.AwaitingLineage && !p.HeldForReincarnation);

        var lastTransition = await db.Transitions
            .Where(t => t.UndoneUtc == null)
            .OrderByDescending(t => t.OccurredUtc).ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(ct);

        var warnings = new List<string>();
        if (assignedCount >= totalLineages)
            warnings.Add($"Lineage pool exhausted ({assignedCount}/{totalLineages} assigned this generation).");
        if (awaiting > 0)
            warnings.Add($"{awaiting} participant(s) awaiting a lineage assignment.");
        if (state.CurrentStreamSessionId is null)
            warnings.Add("No overlay session active — click \"Start New Overlay Session…\". " +
                         "Until then chat messages admit nobody.");

        var isDead = state.CurrentGeneration.DiedUtc is not null;
        var held = participants.Count(p => p.HeldForReincarnation);
        if (isDead)
            warnings.Add(held > 0
                ? $"Generation {state.CurrentGeneration.Number} is dead. {held} chatter(s) are waiting for the next egg."
                : $"Generation {state.CurrentGeneration.Number} is dead — reincarnate when ready.");

        return new AdminStatusView(
            state.CurrentGeneration.Number,
            state.CurrentStreamSession?.Number,
            state.CurrentStage,
            state.CurrentStage.DisplayName(),
            twitchStatus.Status,
            gate.VisualWindowActive,
            participants.Count,
            assignedCount,
            totalLineages,
            awaiting,
            DescribeUndo(lastTransition),
            warnings,
            participants,
            IsDead: isDead,
            HeldForReincarnationCount: held,
            LastUndoableTransitionId: DescribeUndo(lastTransition) is null
                ? null
                : lastTransition?.Id,
            Revision: Interlocked.Increment(ref _adminStatusRevision));
    }

    /// <summary>
    /// What Undo would reverse, or null when it is unavailable. Reincarnation is
    /// final and also seals everything before it: once a generation has been
    /// born, undoing into the one it replaced is not offered.
    /// </summary>
    internal static string? DescribeUndo(TransitionRecord? t) => t switch
    {
        null => null,
        { Type: TransitionType.StageChange } =>
            $"Stage change {t.FromStage.DisplayName()} → {t.ToStage.DisplayName()}",
        { Type: TransitionType.Death } => "Death (revives everyone with their current lineages)",
        _ => null,
    };

    internal static async Task<AppState> GetAppStateAsync(DigiChatDbContext db, CancellationToken ct) =>
        await db.AppStates
            .Include(s => s.CurrentGeneration)
            .Include(s => s.CurrentStreamSession)
            .SingleAsync(s => s.Id == AppState.SingletonId, ct);

    /// <summary>Participants of the current session with their form at <paramref name="stage"/>.</summary>
    internal static async Task<IReadOnlyList<ParticipantView>> GetParticipantViewsAsync(
        DigiChatDbContext db, AppState state, DigivolutionStage stage, CancellationToken ct)
    {
        if (state.CurrentStreamSessionId is not int sessionId)
            return [];

        var rows = await (
            from p in db.Participants
            where p.StreamSessionId == sessionId
            join aj in db.Assignments.Where(a => a.GenerationId == state.CurrentGenerationId)
                on p.ViewerId equals aj.ViewerId into assignments
            from a in assignments.DefaultIfEmpty()
            orderby p.JoinedUtc
            select new
            {
                p.Viewer.TwitchUserId,
                p.Viewer.DisplayName,
                p.JoinedUtc,
                p.HeldForReincarnation,
                LineageId = (int?)a!.LineageId,
                LineageSlug = a.Lineage!.Slug,
                LineageName = a.Lineage.Name,
            }).ToListAsync(ct);

        var lineageIds = rows.Where(r => r.LineageId.HasValue).Select(r => r.LineageId!.Value).Distinct().ToList();
        var forms = await db.DigimonForms
            .Where(f => lineageIds.Contains(f.LineageId) && f.Stage == stage)
            .ToDictionaryAsync(f => f.LineageId, ct);

        return rows.Select(r =>
        {
            var form = r.LineageId.HasValue ? forms.GetValueOrDefault(r.LineageId.Value) : null;
            return new ParticipantView(
                r.TwitchUserId,
                r.DisplayName,
                AwaitingLineage: form is null,
                r.LineageId.HasValue ? r.LineageSlug : null,
                r.LineageId.HasValue ? r.LineageName : null,
                form?.Name,
                form?.AssetKey,
                r.JoinedUtc,
                HeldForReincarnation: r.HeldForReincarnation);
        }).ToList();
    }
}
