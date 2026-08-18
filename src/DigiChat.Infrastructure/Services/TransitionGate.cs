using DigiChat.Domain.Views;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Services;

/// <summary>
/// Serializes every state mutation (spec §14). Admissions, stage changes,
/// reincarnation, undo and session start all run through <see cref="RunExclusiveAsync"/>,
/// so a chat message arriving mid-reincarnation is processed only after the new
/// generation is committed — it then naturally receives a new-generation lineage.
///
/// Separately, a "visual window" tracks how long the overlay's transition
/// animation runs. While active, conflicting admin controls report disabled and
/// spawn notifications are deferred (the DB write already happened; only the
/// broadcast waits) so a new Digimon never drops into the middle of a
/// digivolution or hatch sequence.
/// </summary>
public class TransitionGate
{
    private readonly ILogger<TransitionGate> _logger;
    private readonly TimeSpan _notificationTimeout;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _sync = new();
    private readonly object _notificationSync = new();
    private DateTime _visualWindowEndsUtc = DateTime.MinValue;
    private Task _notificationTail = Task.CompletedTask;

    public TransitionGate(
        ILogger<TransitionGate> logger,
        TimeSpan? notificationTimeout = null)
    {
        _logger = logger;
        _notificationTimeout = notificationTimeout ?? TimeSpan.FromSeconds(10);
        if (_notificationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(notificationTimeout), "Notification timeout must be positive.");
    }

    public bool VisualWindowActive
    {
        get { lock (_sync) return DateTime.UtcNow < _visualWindowEndsUtc; }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try { return await action(); }
        finally { _mutex.Release(); }
    }

    public Task RunExclusiveAsync(Func<Task> action, CancellationToken ct = default) =>
        RunExclusiveAsync(async () => { await action(); return true; }, ct);

    /// <summary>
    /// Opens the visual window. Mutation services call this while they still
    /// hold the global mutex, after the database commit and before releasing
    /// the mutex, so another admin command cannot slip into the gap.
    /// </summary>
    public void BeginVisualWindow(TimeSpan duration)
    {
        lock (_sync)
        {
            _visualWindowEndsUtc = DateTime.UtcNow.Add(duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
        }
    }

    /// <summary>
    /// Queues post-commit work in the same order mutations acquired the gate.
    /// Failures are contained and optionally reconciled from authoritative
    /// state; they never turn an already-committed command into a misleading
    /// HTTP/EventSub failure.
    /// </summary>
    public Task QueueNotification(
        string description,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null,
        DateTime? notBeforeUtc = null)
    {
        lock (_notificationSync)
        {
            var predecessor = _notificationTail;
            var queued = RunNotificationAsync(predecessor, description, action, reconcile, notBeforeUtc);
            _notificationTail = queued;
            return queued;
        }
    }

    /// <summary>
    /// Enqueues a spawn while capturing the current visual-window deadline.
    /// A later transition cannot extend that deadline and overtake the older
    /// spawn because both jobs share the ordered notification queue.
    /// </summary>
    public Task QueueSpawn(
        SpawnEventView spawn,
        IOverlayNotifier notifier,
        Func<CancellationToken, Task> afterSend,
        Func<CancellationToken, Task> reconcile)
    {
        DateTime notBefore;
        lock (_sync) notBefore = _visualWindowEndsUtc;
        if (DateTime.UtcNow < notBefore)
            _logger.LogInformation("Spawn for {User} deferred until transition finishes",
                spawn.Participant.DisplayName);

        return QueueNotification(
            $"spawn for {spawn.Participant.DisplayName}",
            async notificationCt =>
            {
                await notifier.SpawnAsync(spawn, notificationCt);
                await afterSend(notificationCt);
            },
            reconcile,
            notBefore);
    }

    /// <summary>
    /// Returns a snapshot of the ordered notification tail. Tests and graceful
    /// shutdown code can await work already queued without exposing or
    /// mutating the queue itself.
    /// </summary>
    public Task DrainNotificationsAsync()
    {
        lock (_notificationSync) return _notificationTail;
    }

    private async Task RunNotificationAsync(
        Task predecessor,
        string description,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile,
        DateTime? notBeforeUtc)
    {
        try
        {
            // Inside the try on purpose. Predecessors contain their own
            // failures today, but if one ever faulted, awaiting it out here
            // would fault this job too, make IT the tail, and cascade — every
            // later spawn, stage change, death and reincarnation broadcast
            // silently dropped for the life of the process while the database
            // keeps committing. Structural rather than incidental.
            await predecessor.ConfigureAwait(false);

            if (notBeforeUtc is DateTime notBefore)
            {
                var delay = notBefore - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay).ConfigureAwait(false);
            }

            using var actionCts = new CancellationTokenSource(_notificationTimeout);
            await action(actionCts.Token).WaitAsync(actionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-commit notification failed: {Description}", description);
            if (reconcile is null) return;
            try
            {
                using var reconcileCts = new CancellationTokenSource(_notificationTimeout);
                await reconcile(reconcileCts.Token).WaitAsync(reconcileCts.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Authoritative state resync completed after {Description} failed", description);
            }
            catch (Exception reconcileError)
            {
                _logger.LogError(reconcileError,
                    "Authoritative state resync also failed after {Description}", description);
            }
        }
    }
}
