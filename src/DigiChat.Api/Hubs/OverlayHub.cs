using DigiChat.Domain.Views;
using DigiChat.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;

namespace DigiChat.Api.Hubs;

/// <summary>
/// Single hub for both surfaces. The overlay and admin page connect, then pull
/// authoritative state (<see cref="GetOverlayState"/> / <see cref="GetAdminStatus"/>)
/// on connect and on every reconnect — the backend is the only source of truth,
/// so a Browser Source reload or OBS crash always reconstructs cleanly (spec §19).
/// </summary>
public class OverlayHub(OverlayStateService stateService, ILogger<OverlayHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("SignalR client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<OverlayStateView> GetOverlayState() => stateService.GetOverlayStateAsync();

    public Task<AdminStatusView> GetAdminStatus() => stateService.GetAdminStatusAsync();
}
