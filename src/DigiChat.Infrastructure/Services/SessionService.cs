using DigiChat.Domain;
using DigiChat.Domain.Entities;
using DigiChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Services;

public sealed record SessionStartResult(bool Success, string Message, int? SessionNumber);

/// <summary>
/// Starts stream sessions. Only the explicit admin action creates a session —
/// never a crash, restart, or reconnect (spec §7). Starting a session hides
/// everyone (participation is per-session) but changes no generation state,
/// no assignments, and no history.
/// </summary>
public class SessionService(
    IDbContextFactory<DigiChatDbContext> dbFactory,
    TransitionGate gate,
    IOverlayNotifier notifier,
    OverlayStateService stateService,
    IClock clock,
    ILogger<SessionService> logger)
{
    public async Task<SessionStartResult> StartNewSessionAsync(
        int? expectedCurrentSessionNumber = null,
        CancellationToken ct = default)
    {
        var result = await gate.RunExclusiveAsync(async () =>
        {
            // Request cancellation is honored while waiting for the gate. Once
            // admitted, finish and enqueue the committed operation atomically.
            var operationCt = CancellationToken.None;
            if (gate.VisualWindowActive)
                return new SessionStartResult(
                    false, "A transition is currently running — try again in a moment.", null);

            await using var db = await dbFactory.CreateDbContextAsync(operationCt);
            var now = clock.UtcNow;
            var state = await OverlayStateService.GetAppStateAsync(db, operationCt);

            var currentNumber = state.CurrentStreamSession?.Number ?? 0;
            if (expectedCurrentSessionNumber is int expected && expected != currentNumber)
                return new SessionStartResult(
                    false,
                    $"Overlay session changed from the expected #{expected} to #{currentNumber}. " +
                    "Status was refreshed; review it before trying again.",
                    null);

            if (state.CurrentStreamSession is { } previous)
                previous.EndedUtc = now;

            var maxNumber = await db.StreamSessions.MaxAsync(s => (int?)s.Number, operationCt) ?? 0;
            var session = new StreamSession { Number = maxNumber + 1, StartedUtc = now };
            db.StreamSessions.Add(session);

            state.CurrentStreamSession = session;
            state.UpdatedUtc = now;
            await db.SaveChangesAsync(operationCt);

            logger.LogInformation("Stream session {Number} started", session.Number);

            async Task ReconcileAsync(CancellationToken notificationCt)
            {
                await notifier.StateResyncAsync(
                    await stateService.GetOverlayStateAsync(notificationCt), notificationCt);
                await notifier.AdminStatusAsync(
                    await stateService.GetAdminStatusAsync(notificationCt), notificationCt);
            }

            // The session is committed by here. If projecting it throws, no job
            // is queued and the overlay keeps showing the previous session's
            // cast — with nothing to correct it, since the periodic
            // reconciliation only compares what it was last told.
            try
            {
                var overlayState = await stateService.GetOverlayStateAsync(operationCt);
                var adminStatus = await stateService.GetAdminStatusAsync(operationCt);
                _ = gate.QueueNotification(
                    $"start overlay session {session.Number}",
                    async notificationCt =>
                    {
                        await notifier.StateResyncAsync(overlayState, notificationCt);
                        await notifier.AdminStatusAsync(adminStatus, notificationCt);
                    },
                    ReconcileAsync);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Started session {Number} but could not project it; reconciling instead", session.Number);
                _ = gate.QueueNotification(
                    $"reconcile after starting overlay session {session.Number}", ReconcileAsync);
            }
            return new SessionStartResult(true, $"Stream session {session.Number} started.", session.Number);
        }, ct);
        return result;
    }
}
