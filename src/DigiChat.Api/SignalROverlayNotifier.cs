using DigiChat.Api.Hubs;
using DigiChat.Domain.Views;
using DigiChat.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;

namespace DigiChat.Api;

/// <summary>
/// Broadcasts domain events to all connected SignalR clients. Local
/// single-streamer app: at most a couple of clients (overlay + admin tab), so
/// no groups — each client ignores events it doesn't care about.
/// </summary>
public class SignalROverlayNotifier(IHubContext<OverlayHub> hub) : IOverlayNotifier
{
    public Task SpawnAsync(SpawnEventView spawn, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("spawn", spawn, ct);

    public Task StageChangedAsync(StageChangeView change, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("stageChanged", change, ct);

    public Task DiedAsync(DeathView death, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("died", death, ct);

    public Task ReincarnationAsync(ReincarnationView reincarnation, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("reincarnation", reincarnation, ct);

    public Task StateResyncAsync(OverlayStateView state, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("stateResync", state, ct);

    public Task AdminStatusAsync(AdminStatusView status, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("adminStatus", status, ct);
}
