using DigiChat.Domain;
using DigiChat.Domain.Entities;
using DigiChat.Domain.Views;
using DigiChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigiChat.Infrastructure.Services;

public sealed record TransitionResult(bool Success, string Message);

/// <summary>
/// Stage changes, reincarnation, and undo. All three are serialized through the
/// transition gate and recorded append-only in <see cref="TransitionRecord"/>;
/// undo marks records/generations as undone instead of deleting history.
/// </summary>
public class TransitionService(
    IDbContextFactory<DigiChatDbContext> dbFactory,
    TransitionGate gate,
    IOverlayNotifier notifier,
    OverlayStateService stateService,
    IClock clock,
    IOptions<TransitionOptions> options,
    ILogger<TransitionService> logger)
{
    private readonly Random _random = new();

    // ---------------------------------------------------------------- stage change

    public async Task<TransitionResult> ChangeStageAsync(DigivolutionStage target, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(target))
            return new(false, $"Unknown stage value '{(int)target}'.");

        var result = await gate.RunExclusiveAsync(async () =>
        {
            var operationCt = CancellationToken.None;
            if (gate.VisualWindowActive)
                return new TransitionResult(false, "A transition is already running.");

            await using var db = await dbFactory.CreateDbContextAsync(operationCt);
            var state = await OverlayStateService.GetAppStateAsync(db, operationCt);

            // The dead do not digivolve. Without this, a stage change unfreezes
            // every corpse — full colour, walking again — while the panel still
            // says dead, and it buries the death under a newer undo record.
            if (state.CurrentGeneration.DiedUtc is not null)
                return new TransitionResult(false,
                    "This generation is dead. Undo the death, or reincarnate.");

            // Clicking the already-selected stage is a no-op (spec §2).
            if (state.CurrentStage == target)
                return new TransitionResult(true, $"Stage is already {target.DisplayName()}; nothing to do.");

            var from = state.CurrentStage;
            db.Transitions.Add(new TransitionRecord
            {
                Type = TransitionType.StageChange,
                OccurredUtc = clock.UtcNow,
                FromStage = from,
                ToStage = target,
            });
            state.CurrentStage = target;
            state.UpdatedUtc = clock.UtcNow;
            await db.SaveChangesAsync(operationCt);

            try
            {
                var participants = await OverlayStateService.GetParticipantViewsAsync(
                    db, state, target, operationCt);
                var change = new StageChangeView(from, target, participants);
                logger.LogInformation("Stage change {From} → {To} ({Count} visible)",
                    from.DisplayName(), target.DisplayName(), participants.Count);

                gate.BeginVisualWindow(TimeSpan.FromSeconds(options.Value.StageChangeSeconds));
                var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                QueueNotification(
                    $"stage change {from.DisplayName()} to {target.DisplayName()}",
                    notificationCt => notifier.StageChangedAsync(change, notificationCt),
                    adminStatus);
            }
            catch (Exception ex)
            {
                QueueReconcileAfter($"stage change to {target.DisplayName()}", ex);
            }
            // The stage really did change, so report success either way.
            return new TransitionResult(true, $"Stage changed to {target.DisplayName()}.");
        }, ct);
        return result;
    }

    // ---------------------------------------------------------------- reincarnation

    // ---------------------------------------------------------------- death

    /// <summary>
    /// Kills every Digimon of the current generation. They stay dead until
    /// undone or reincarnated — through restarts and OBS reloads, because the
    /// death is recorded on the generation rather than being an animation.
    /// </summary>
    public async Task<TransitionResult> KillAsync(CancellationToken ct = default)
    {
        var result = await gate.RunExclusiveAsync(async () =>
        {
            var operationCt = CancellationToken.None;
            if (gate.VisualWindowActive)
                return new TransitionResult(false, "A transition is already running.");

            await using var db = await dbFactory.CreateDbContextAsync(operationCt);
            var now = clock.UtcNow;
            var state = await OverlayStateService.GetAppStateAsync(db, operationCt);
            if (state.CurrentGeneration.DiedUtc is not null)
                return new TransitionResult(false, "This generation is already dead.");

            state.CurrentGeneration.DiedUtc = now;
            db.Transitions.Add(new TransitionRecord
            {
                Type = TransitionType.Death,
                OccurredUtc = now,
                FromStage = state.CurrentStage,
                ToStage = state.CurrentStage,
                FromGenerationId = state.CurrentGenerationId,
            });
            state.UpdatedUtc = now;
            await db.SaveChangesAsync(operationCt);

            try
            {
                var participants = await OverlayStateService.GetParticipantViewsAsync(
                    db, state, state.CurrentStage, operationCt);
                var view = new DeathView(participants);
                logger.LogInformation("Generation {Number} died ({Count} on screen)",
                    state.CurrentGeneration.Number, participants.Count);

                gate.BeginVisualWindow(TimeSpan.FromSeconds(options.Value.DeathSeconds));
                var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                QueueNotification(
                    $"death of generation {state.CurrentGeneration.Number}",
                    notificationCt => notifier.DiedAsync(view, notificationCt),
                    adminStatus);
            }
            catch (Exception ex)
            {
                QueueReconcileAfter($"death of generation {state.CurrentGeneration.Number}", ex);
            }
            return new TransitionResult(true,
                $"Generation {state.CurrentGeneration.Number} is dead. Reincarnate when ready.");
        }, ct);
        return result;
    }

    // ---------------------------------------------------------------- reincarnation

    public async Task<TransitionResult> ReincarnateAsync(CancellationToken ct = default)
    {
        var result = await gate.RunExclusiveAsync(async () =>
        {
            var operationCt = CancellationToken.None;
            if (gate.VisualWindowActive)
                return new TransitionResult(false, "A transition is already running.");

            await using var db = await dbFactory.CreateDbContextAsync(operationCt);
            await using var transaction = await db.Database.BeginTransactionAsync(operationCt);
            var now = clock.UtcNow;
            var state = await OverlayStateService.GetAppStateAsync(db, operationCt);
            var oldGeneration = state.CurrentGeneration;
            var fromStage = state.CurrentStage;

            // Reincarnation follows a death — never interrupts a living generation.
            if (oldGeneration.DiedUtc is null)
                return new TransitionResult(false,
                    "Kill this generation first — reincarnation only follows a death.");

            oldGeneration.EndedUtc = now;
            var maxNumber = await db.Generations.MaxAsync(g => (int?)g.Number, operationCt) ?? 0;
            var newGeneration = new Generation { Number = maxNumber + 1, StartedUtc = now };
            db.Generations.Add(newGeneration);

            // Only currently visible participants consume new-generation lineages
            // now; absent viewers get theirs on their next first message (spec §12).
            var currentParticipants = state.CurrentStreamSessionId is int sessionId
                ? await db.Participants.Where(p => p.StreamSessionId == sessionId).ToListAsync(operationCt)
                : [];
            var participantViewerIds = currentParticipants.Select(p => p.ViewerId).ToList();

            // What each viewer is losing, so the redraw can avoid handing it back.
            var previous = await db.Assignments
                .Where(a => a.GenerationId == oldGeneration.Id && a.LineageId != null)
                .ToDictionaryAsync(a => a.ViewerId, a => a.LineageId!.Value, operationCt);

            var pool = await db.Lineages.Where(l => l.Enabled).OrderBy(l => l.OrderIndex)
                .ToListAsync(operationCt);
            var shuffled = pool.OrderBy(_ => _random.Next()).ToList();
            var picks = participantViewerIds
                .Select((viewerId, i) => (viewerId, lineage: i < shuffled.Count ? shuffled[i] : null))
                .ToList();

            bool KeepsOldLineage(int viewerId, Lineage? lineage) =>
                lineage is not null
                && previous.TryGetValue(viewerId, out var had)
                && had == lineage.Id;

            // A plain shuffle can deal a viewer the very lineage they just lost,
            // which makes reincarnation look broken rather than random. Trade the
            // collision away to any slot that can take it.
            for (var i = 0; i < picks.Count; i++)
            {
                if (!KeepsOldLineage(picks[i].viewerId, picks[i].lineage)) continue;
                for (var j = 0; j < picks.Count; j++)
                {
                    if (i == j) continue;
                    if (KeepsOldLineage(picks[i].viewerId, picks[j].lineage)) continue;
                    if (KeepsOldLineage(picks[j].viewerId, picks[i].lineage)) continue;
                    (picks[i], picks[j]) =
                        ((picks[i].viewerId, picks[j].lineage), (picks[j].viewerId, picks[i].lineage));
                    break;
                }
            }

            foreach (var (viewerId, pick) in picks)
            {
                if (pick is null)
                    logger.LogWarning("Lineage pool exhausted during reincarnation for viewer {ViewerId}", viewerId);
                else if (KeepsOldLineage(viewerId, pick))
                    logger.LogInformation(
                        "Viewer {ViewerId} keeps lineage {Lineage}: no swap available", viewerId, pick.Slug);

                db.Assignments.Add(new ViewerGenerationAssignment
                {
                    ViewerId = viewerId,
                    Generation = newGeneration,
                    Lineage = pick,
                    AssignedUtc = now,
                });
            }

            // Everyone now belongs to the new living generation. The persisted
            // flag is cleared in the same transaction as their new assignment.
            foreach (var participant in currentParticipants)
                participant.HeldForReincarnation = false;

            var record = new TransitionRecord
            {
                Type = TransitionType.Reincarnation,
                OccurredUtc = now,
                FromStage = fromStage,
                ToStage = DigivolutionStage.Fresh,
                FromGenerationId = oldGeneration.Id,
            };
            db.Transitions.Add(record);

            state.CurrentGeneration = newGeneration;
            state.CurrentStage = DigivolutionStage.Fresh;
            state.UpdatedUtc = now;
            await db.SaveChangesAsync(operationCt);

            // The new generation's Id exists after the save; backfill the record.
            record.ToGenerationId = newGeneration.Id;
            await db.SaveChangesAsync(operationCt);
            await transaction.CommitAsync(operationCt);

            // The highest-stakes site: the generation has already advanced and
            // reincarnation cannot be undone, so a stranded broadcast here
            // leaves the overlay showing a cast that no longer exists.
            try
            {
                var participants = await OverlayStateService.GetParticipantViewsAsync(
                    db, state, DigivolutionStage.Fresh, operationCt);
                var view = new ReincarnationView(newGeneration.Number, participants);
                logger.LogInformation("Reincarnation: generation {Old} → {New}, {Count} participants reassigned",
                    oldGeneration.Number, newGeneration.Number, participants.Count);

                gate.BeginVisualWindow(TimeSpan.FromSeconds(options.Value.ReincarnationSeconds));
                var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                QueueNotification(
                    $"reincarnation into generation {newGeneration.Number}",
                    notificationCt => notifier.ReincarnationAsync(view, notificationCt),
                    adminStatus);
            }
            catch (Exception ex)
            {
                QueueReconcileAfter($"reincarnation into generation {newGeneration.Number}", ex);
            }
            return new TransitionResult(true, $"Reincarnated into generation {newGeneration.Number}.");
        }, ct);
        return result;
    }

    // ---------------------------------------------------------------- undo

    public async Task<TransitionResult> UndoLastAsync(
        int? expectedTransitionId = null,
        CancellationToken ct = default)
    {
        var result = await gate.RunExclusiveAsync(async () =>
        {
            var operationCt = CancellationToken.None;
            if (gate.VisualWindowActive)
                return new TransitionResult(false, "A transition is already running.");

            await using var db = await dbFactory.CreateDbContextAsync(operationCt);
            var now = clock.UtcNow;
            var record = await db.Transitions
                .Where(t => t.UndoneUtc == null)
                .OrderByDescending(t => t.OccurredUtc).ThenByDescending(t => t.Id)
                .FirstOrDefaultAsync(operationCt);
            if (expectedTransitionId is int expected && record?.Id != expected)
                return new TransitionResult(
                    false,
                    "The latest undoable action changed after this status snapshot. " +
                    "Status was refreshed; review it before trying again.");
            if (record is null)
                return new TransitionResult(false, "Nothing to undo.");

            var state = await OverlayStateService.GetAppStateAsync(db, operationCt);

            if (record.Type == TransitionType.StageChange)
            {
                var from = state.CurrentStage;
                state.CurrentStage = record.FromStage;
                record.UndoneUtc = now;
                state.UpdatedUtc = now;
                await db.SaveChangesAsync(operationCt);

                try
                {
                    var participants = await OverlayStateService.GetParticipantViewsAsync(
                        db, state, record.FromStage, operationCt);
                    var stageRevert = new StageChangeView(from, record.FromStage, participants);
                    logger.LogInformation("Undo: stage restored to {Stage}", record.FromStage.DisplayName());

                    gate.BeginVisualWindow(TimeSpan.FromSeconds(options.Value.StageChangeSeconds));
                    var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                    QueueNotification(
                        $"undo stage change to {record.FromStage.DisplayName()}",
                        notificationCt => notifier.StageChangedAsync(stageRevert, notificationCt),
                        adminStatus);
                }
                catch (Exception ex)
                {
                    QueueReconcileAfter($"undo stage change to {record.FromStage.DisplayName()}", ex);
                }
                return new TransitionResult(true, $"Stage restored to {record.FromStage.DisplayName()}.");
            }

            if (record.Type == TransitionType.Death)
            {
                // Viewers who first spoke during the death were persisted with
                // no lineage. Undo revives the generation, so assign as many of
                // those held viewers as the remaining pool allows before the
                // resync. Otherwise they could never appear in this session.
                var held = state.CurrentStreamSessionId is int sessionId
                    ? await db.Participants
                        .Where(p => p.StreamSessionId == sessionId && p.HeldForReincarnation)
                        .OrderBy(p => p.JoinedUtc)
                        .ToListAsync(operationCt)
                    : [];
                var heldViewerIds = held.Select(p => p.ViewerId).ToList();
                var assignments = await db.Assignments
                    .Where(a => a.GenerationId == state.CurrentGenerationId
                                && heldViewerIds.Contains(a.ViewerId))
                    .ToDictionaryAsync(a => a.ViewerId, operationCt);
                var available = await db.Lineages
                    .Where(l => l.Enabled && !db.Assignments.Any(a =>
                        a.GenerationId == state.CurrentGenerationId && a.LineageId == l.Id))
                    .ToListAsync(operationCt);
                var shuffled = available.OrderBy(_ => _random.Next()).ToList();
                var nextLineage = 0;
                foreach (var participant in held)
                {
                    if (!assignments.TryGetValue(participant.ViewerId, out var assignment))
                    {
                        assignment = new ViewerGenerationAssignment
                        {
                            ViewerId = participant.ViewerId,
                            GenerationId = state.CurrentGenerationId,
                            AssignedUtc = now,
                        };
                        db.Assignments.Add(assignment);
                    }
                    if (assignment.LineageId is null && nextLineage < shuffled.Count)
                    {
                        assignment.Lineage = shuffled[nextLineage++];
                        assignment.AssignedUtc = now;
                    }
                    participant.HeldForReincarnation = false;
                }

                state.CurrentGeneration.DiedUtc = null;
                record.UndoneUtc = now;
                state.UpdatedUtc = now;
                await db.SaveChangesAsync(operationCt);

                logger.LogInformation("Undo: generation {Number} revived", state.CurrentGeneration.Number);
                // This branch opens no visual window, so the admin panel's
                // transition poll never fires — without a queued job it can sit
                // showing "dead" with Reincarnate enabled while the generation
                // is actually alive, until the next chatter happens to arrive.
                try
                {
                    var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                    QueueNotification(
                        $"undo death of generation {state.CurrentGeneration.Number}",
                        async notificationCt => await notifier.StateResyncAsync(
                            await stateService.GetOverlayStateAsync(notificationCt), notificationCt),
                        adminStatus);
                }
                catch (Exception ex)
                {
                    QueueReconcileAfter($"undo death of generation {state.CurrentGeneration.Number}", ex);
                }
                return new TransitionResult(true,
                    $"Death undone — generation {state.CurrentGeneration.Number} is alive again.");
            }

            // Reincarnation is final, and seals everything before it: undoing
            // into a generation that has already been replaced would resurrect
            // lineage assignments that new viewers may since have been given.
            return new TransitionResult(false,
                "Reincarnation is final — a new generation cannot be undone.");
        }, ct);

        return result;
    }

    private void QueueNotification(
        string description,
        Func<CancellationToken, Task> eventNotification,
        AdminStatusView adminStatus) =>
        gate.QueueNotification(
            description,
            async notificationCt =>
            {
                await eventNotification(notificationCt);
                await notifier.AdminStatusAsync(adminStatus, notificationCt);
            },
            ReconcileClientsAsync);

    /// <summary>
    /// Last resort after a commit succeeds. Projecting the broadcast opens a
    /// fresh connection, so a transient database failure there is realistic —
    /// and because <c>BeginVisualWindow</c> and the queueing both happen after
    /// the projection, an escape would leave durable state that no client is
    /// ever told about. Queue a plain reconciliation instead.
    /// </summary>
    private void QueueReconcileAfter(string description, Exception cause)
    {
        logger.LogError(cause,
            "Committed {Description} but could not project the broadcast; reconciling instead",
            description);
        _ = gate.QueueNotification($"reconcile after {description}", ReconcileClientsAsync);
    }

    private async Task ReconcileClientsAsync(CancellationToken ct = default)
    {
        await notifier.StateResyncAsync(await stateService.GetOverlayStateAsync(ct), ct);
        await notifier.AdminStatusAsync(await stateService.GetAdminStatusAsync(ct), ct);
    }
}
