using DigiChat.Domain.Views;

namespace DigiChat.Infrastructure.Services;

/// <summary>
/// Outbound real-time notifications. Implemented over SignalR in the API
/// project; a no-op/recording fake is used in tests. Services call these only
/// after their database transaction has committed — the DB is authoritative,
/// notifications are best-effort mirrors.
/// </summary>
public interface IOverlayNotifier
{
    Task SpawnAsync(SpawnEventView spawn, CancellationToken ct = default);
    Task StageChangedAsync(StageChangeView change, CancellationToken ct = default);
    Task DiedAsync(DeathView death, CancellationToken ct = default);
    Task ReincarnationAsync(ReincarnationView reincarnation, CancellationToken ct = default);
    /// <summary>Full state push — session start, undo, or any "just resync" moment.</summary>
    Task StateResyncAsync(OverlayStateView state, CancellationToken ct = default);
    Task AdminStatusAsync(AdminStatusView status, CancellationToken ct = default);
}
