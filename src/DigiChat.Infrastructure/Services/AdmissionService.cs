using DigiChat.Domain;
using DigiChat.Domain.Entities;
using DigiChat.Domain.Views;
using DigiChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigiChat.Infrastructure.Services;

/// <summary>
/// Processes inbound chat messages (real EventSub or mock). The first
/// qualifying message of a stream session admits the viewer: participant row,
/// lineage assignment for the current generation (reused if it already
/// exists), and a spawn broadcast. Everything runs inside the transition gate,
/// so admissions arriving mid-reincarnation wait for the new generation.
/// </summary>
public class AdmissionService(
    IDbContextFactory<DigiChatDbContext> dbFactory,
    TransitionGate gate,
    IOverlayNotifier notifier,
    OverlayStateService stateService,
    IClock clock,
    IOptions<TwitchOptions> twitchOptions,
    IOptions<AdmissionOptions> admissionOptions,
    ILogger<AdmissionService> logger)
{
    private readonly Random _random = new();
    private DateTime _nextLedgerPruneUtc = DateTime.MinValue;
    private const int RecentMessageLimit = 16_384;
    private static readonly TimeSpan RecentMessageLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTime> _recentMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, DateTime SeenUtc)> _recentMessageOrder = new();

    public async Task<AdmissionResult> HandleAsync(ChatMessageEvent msg, CancellationToken ct = default)
    {
        if (!IsValid(msg))
        {
            logger.LogWarning("Ignoring malformed chat event (message/user/name lengths were invalid)");
            return AdmissionResult.IgnoredUser;
        }
        if (msg.IsFromOtherChannel)
        {
            logger.LogInformation("Ignoring shared-chat message from another channel (user {UserId})", msg.TwitchUserId);
            return AdmissionResult.IgnoredUser;
        }
        if (twitchOptions.Value.IgnoredUserIds.Contains(msg.TwitchUserId))
        {
            logger.LogDebug("Ignoring configured user {UserId}", msg.TwitchUserId);
            return AdmissionResult.IgnoredUser;
        }

        var result = await gate.RunExclusiveAsync(async () =>
        {
            // The caller may disappear after the gate admits this operation.
            // From that point onward, finish the atomic database mutation and
            // queue its reconciliation instead of leaving a committed result
            // without a matching notification.
            var operationCt = CancellationToken.None;
            if (!RememberMessage(msg.MessageId, clock.UtcNow))
                return new ProcessResult(AdmissionOutcome.DuplicateEvent, null, default);

            ProcessResult processed;
            try
            {
                processed = await ProcessAsync(msg, operationCt);
            }
            catch
            {
                // A failed attempt was not processed. Let an EventSub retry try
                // again; if the database commit actually won a cancellation
                // race, its durable unique ledger remains the backstop.
                _recentMessageIds.Remove(msg.MessageId);
                // The commit can land and the projection after it still throw.
                // Nothing else would ever tell the overlay, so queue a
                // reconciliation rather than leaving a committed participant
                // invisible until the next reconnect.
                _ = gate.QueueNotification(
                    $"reconcile after a failed admission of {msg.DisplayName}",
                    ReconcileClientsAsync);
                throw;
            }
            if (processed.Outcome != AdmissionOutcome.Admitted || processed.Participant is null)
                return processed;

            var reconcile = (CancellationToken notificationCt) =>
                ReconcileClientsAsync(notificationCt);
            // The admission is already committed by here. Projecting the admin
            // status is the last thing that can fail, and if it does there is
            // no queued job to carry the news — so fall back to a plain
            // reconciliation instead of letting the exception strand a viewer
            // who is in the database but not on screen.
            try
            {
                var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                if (!processed.Participant.AwaitingLineage && !processed.Participant.HeldForReincarnation)
                {
                    _ = gate.QueueSpawn(
                        new SpawnEventView(processed.Participant, processed.Stage),
                        notifier,
                        notificationCt => notifier.AdminStatusAsync(adminStatus, notificationCt),
                        reconcile);
                }
                else
                {
                    _ = gate.QueueNotification(
                        $"admin status after admitting {processed.Participant.DisplayName}",
                        notificationCt => notifier.AdminStatusAsync(adminStatus, notificationCt),
                        reconcile);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Admitted {DisplayName} but could not project the notification; reconciling instead",
                    processed.Participant.DisplayName);
                _ = gate.QueueNotification(
                    $"reconcile after admitting {processed.Participant.DisplayName}",
                    ReconcileClientsAsync);
            }
            return processed;
        }, ct);

        return result.ToAdmissionResult();
    }

    private sealed record ProcessResult(AdmissionOutcome Outcome, ParticipantView? Participant, DigivolutionStage Stage)
    {
        public AdmissionResult ToAdmissionResult() => new(Outcome, Participant);
    }

    private async Task<ProcessResult> ProcessAsync(ChatMessageEvent msg, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;

        var state = await OverlayStateService.GetAppStateAsync(db, ct);
        if (state.CurrentStreamSessionId is not int sessionId)
            return new(AdmissionOutcome.NoActiveSession, null, default);

        // Idempotency: EventSub may redeliver. Safe as check-then-insert because
        // the gate serializes us; the unique index is the concurrency backstop.
        if (await db.ProcessedChatEvents.AnyAsync(p => p.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("Duplicate EventSub message {MessageId} ignored", msg.MessageId);
            return new(AdmissionOutcome.DuplicateEvent, null, default);
        }

        var viewer = await db.Viewers.FirstOrDefaultAsync(v => v.TwitchUserId == msg.TwitchUserId, ct);
        var alreadyParticipant = viewer is not null &&
            await db.Participants.AnyAsync(
                p => p.StreamSessionId == sessionId && p.ViewerId == viewer.Id, ct);
        if (alreadyParticipant)
            return new(AdmissionOutcome.AlreadyParticipant, null, default);

        var participantCount = await db.Participants.CountAsync(p => p.StreamSessionId == sessionId, ct);
        if (participantCount >= admissionOptions.Value.MaxParticipantsPerSession)
        {
            logger.LogWarning(
                "Admission cap reached for session {Session}: refusing new viewer {UserId}",
                state.CurrentStreamSession!.Number, msg.TwitchUserId);
            return new(AdmissionOutcome.CapacityReached, null, default);
        }

        // Repeat chatter messages are no-ops and never enter this ledger. Prune
        // during a long-running stream as well as at startup so its size is
        // bounded even when DigiChat stays open for days.
        if (now >= _nextLedgerPruneUtc)
        {
            var cutoff = now.AddDays(-1);
            await db.ProcessedChatEvents.Where(p => p.ReceivedUtc < cutoff).ExecuteDeleteAsync(ct);
            _nextLedgerPruneUtc = now.AddHours(1);
        }
        db.ProcessedChatEvents.Add(new ProcessedChatEvent { MessageId = msg.MessageId, ReceivedUtc = now });

        if (viewer is null)
        {
            viewer = new Viewer
            {
                TwitchUserId = msg.TwitchUserId,
                Login = msg.Login,
                DisplayName = msg.DisplayName,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            db.Viewers.Add(viewer);
            logger.LogInformation("New viewer {DisplayName} ({UserId})", msg.DisplayName, msg.TwitchUserId);
        }
        else
        {
            // Identity is the Twitch user ID; names are just decoration to refresh.
            viewer.Login = msg.Login;
            viewer.DisplayName = msg.DisplayName;
            viewer.LastSeenUtc = now;
        }

        var deadGeneration = state.CurrentGeneration.DiedUtc is not null;
        db.Participants.Add(new StreamSessionParticipant
        {
            StreamSessionId = sessionId,
            Viewer = viewer,
            JoinedUtc = now,
            HeldForReincarnation = deadGeneration,
        });

        var assignment = viewer.Id == 0
            ? null
            : await db.Assignments.Include(a => a.Lineage)
                .FirstOrDefaultAsync(a => a.GenerationId == state.CurrentGenerationId && a.ViewerId == viewer.Id, ct);

        if (assignment is null)
        {
            assignment = new ViewerGenerationAssignment
            {
                Viewer = viewer,
                GenerationId = state.CurrentGenerationId,
                AssignedUtc = now,
            };
            db.Assignments.Add(assignment);
        }

        if (assignment.LineageId is null && !deadGeneration)
        {
            var lineage = await PickUnusedLineageAsync(db, state.CurrentGenerationId, ct);
            if (lineage is null)
            {
                logger.LogWarning(
                    "Lineage pool exhausted: {DisplayName} admitted in awaiting-lineage state", viewer.DisplayName);
            }
            else
            {
                assignment.Lineage = lineage;
                assignment.LineageId = lineage.Id;
                assignment.AssignedUtc = now;
                logger.LogInformation("Assigned lineage {Lineage} to {DisplayName} for generation {Gen}",
                    lineage.Slug, viewer.DisplayName, state.CurrentGeneration.Number);
            }
        }

        await db.SaveChangesAsync(ct);

        var form = assignment.LineageId is int lid
            ? await db.DigimonForms.FirstOrDefaultAsync(f => f.LineageId == lid && f.Stage == state.CurrentStage, ct)
            : null;

        var view = new ParticipantView(
            viewer.TwitchUserId,
            viewer.DisplayName,
            AwaitingLineage: form is null,
            assignment.Lineage?.Slug,
            assignment.Lineage?.Name,
            form?.Name,
            form?.AssetKey,
            now,
            HeldForReincarnation: deadGeneration);

        logger.LogInformation("Admitted {DisplayName} to session {Session} as {Form}",
            viewer.DisplayName, state.CurrentStreamSession!.Number,
            deadGeneration ? "(held — generation is dead)" : form?.Name ?? "(awaiting lineage)");
        return new(AdmissionOutcome.Admitted, view, state.CurrentStage);
    }

    private static bool IsValid(ChatMessageEvent msg) =>
        !string.IsNullOrWhiteSpace(msg.MessageId) && msg.MessageId.Length <= 64
        && !string.IsNullOrWhiteSpace(msg.TwitchUserId) && msg.TwitchUserId.Length <= 32
        && !string.IsNullOrWhiteSpace(msg.Login) && msg.Login.Length <= 64
        && !string.IsNullOrWhiteSpace(msg.DisplayName) && msg.DisplayName.Length <= 128;

    /// <summary>
    /// Bounded, in-process dedupe for messages that intentionally do not write
    /// the durable admission ledger (repeat chat, no-session, capacity, etc.).
    /// This prevents a redelivery from being reinterpreted after a new session
    /// without restoring a database write for every line of busy Twitch chat.
    /// Calls are serialized by <see cref="TransitionGate"/>.
    /// </summary>
    private bool RememberMessage(string messageId, DateTime now)
    {
        var cutoff = now - RecentMessageLifetime;
        while (_recentMessageOrder.Count > 0
               && (_recentMessageOrder.Peek().SeenUtc < cutoff
                   || _recentMessageIds.Count >= RecentMessageLimit))
        {
            var expired = _recentMessageOrder.Dequeue();
            if (_recentMessageIds.TryGetValue(expired.Id, out var current)
                && current == expired.SeenUtc)
                _recentMessageIds.Remove(expired.Id);
        }

        if (_recentMessageIds.ContainsKey(messageId)) return false;
        _recentMessageIds.Add(messageId, now);
        _recentMessageOrder.Enqueue((messageId, now));
        return true;
    }

    private async Task ReconcileClientsAsync(CancellationToken ct = default)
    {
        await notifier.StateResyncAsync(await stateService.GetOverlayStateAsync(ct), ct);
        await notifier.AdminStatusAsync(await stateService.GetAdminStatusAsync(ct), ct);
    }

    /// <summary>Random unused enabled lineage for the generation, or null when exhausted.</summary>
    private async Task<Lineage?> PickUnusedLineageAsync(DigiChatDbContext db, int generationId, CancellationToken ct)
    {
        var candidates = await db.Lineages
            .Where(l => l.Enabled && !db.Assignments.Any(a => a.GenerationId == generationId && a.LineageId == l.Id))
            .ToListAsync(ct);
        // Exclude picks pending in this unit of work (defensive; gate serializes us).
        var pendingIds = db.ChangeTracker.Entries<ViewerGenerationAssignment>()
            .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added && e.Entity.LineageId != null)
            .Select(e => e.Entity.LineageId!.Value)
            .ToHashSet();
        candidates.RemoveAll(l => pendingIds.Contains(l.Id));
        return candidates.Count == 0 ? null : candidates[_random.Next(candidates.Count)];
    }
}
