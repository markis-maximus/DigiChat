using DigiChat.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DigiChat.Domain.Tests;

public class ApiRequestBoundaryTests
{
    private const string TrustedOrigin = "http://localhost:5170";

    [Fact]
    public async Task HostileFlood_RemainsForbidden_WithOneBoundedGlobalWarningBudget()
    {
        var warnings = 0;
        var downstreamCalls = 0;
        var boundary = new ApiRequestBoundary(
            origin => string.Equals(origin, TrustedOrigin, StringComparison.OrdinalIgnoreCase),
            new CrossSiteRejectionWarningGate(permitLimit: 8),
            (_, _, _, _) => Interlocked.Increment(ref warnings));

        var hostileRequests = Enumerable.Range(0, 1_000).Select(async index =>
        {
            var context = CreateContext(HttpMethods.Get, "/api/admin/status");
            // Every request gets a different attacker-controlled value. The
            // warning gate remains one global budget, not 1,000 partitions.
            context.Request.Headers.Origin = $"https://attacker-{index}.example";

            await boundary.InvokeAsync(context, _ =>
            {
                Interlocked.Increment(ref downstreamCalls);
                return Task.CompletedTask;
            });
            return context.Response.StatusCode;
        });

        var statuses = await Task.WhenAll(hostileRequests);

        Assert.All(statuses, status => Assert.Equal(StatusCodes.Status403Forbidden, status));
        Assert.Equal(8, warnings);
        Assert.Equal(0, downstreamCalls);

        // Rejected traffic never reaches (and therefore cannot spend permits
        // in) the trusted state-read limiter that follows this middleware.
        var trusted = CreateContext(HttpMethods.Get, "/api/admin/status");
        trusted.Request.Headers.Origin = TrustedOrigin;
        await boundary.InvokeAsync(trusted, _ =>
        {
            Interlocked.Increment(ref downstreamCalls);
            return Task.CompletedTask;
        });

        Assert.Equal(1, downstreamCalls);
        Assert.Equal(8, warnings);
    }

    /// <summary>
    /// The /hub carve-out is load-bearing in BOTH directions and easy to break
    /// while tidying: widening the marker rule to include /hub kills SignalR
    /// negotiate (overlay and admin both go dark, on stream, with a green
    /// suite), and dropping /hub from the origin check reopens the live viewer
    /// roster to any page — WebSocketOptions.AllowedOrigins only backstops the
    /// WebSocket transport, never negotiate, SSE or long-polling.
    /// </summary>
    [Fact]
    public async Task HubKeepsSignalRWorkingWhileStillRefusingForeignOrigins()
    {
        var allowed = 0;
        var boundary = new ApiRequestBoundary(
            origin => string.Equals(origin, TrustedOrigin, StringComparison.OrdinalIgnoreCase),
            new CrossSiteRejectionWarningGate(),
            (_, _, _, _) => { });

        // SignalR's own client never sends X-DigiChat-Command; negotiate must
        // still pass on our origin.
        var negotiate = CreateContext(HttpMethods.Post, "/hub/negotiate");
        negotiate.Request.Headers.Origin = TrustedOrigin;
        await boundary.InvokeAsync(negotiate, Allow);
        Assert.Equal(StatusCodes.Status200OK, negotiate.Response.StatusCode);

        var foreignHub = CreateContext(HttpMethods.Post, "/hub/negotiate");
        foreignHub.Request.Headers.Origin = "https://evil.example";
        await boundary.InvokeAsync(foreignHub, Allow);
        Assert.Equal(StatusCodes.Status403Forbidden, foreignHub.Response.StatusCode);

        // A sandboxed iframe sends the literal string "null".
        var nullOrigin = CreateContext(HttpMethods.Post, "/api/admin/kill");
        nullOrigin.Request.Headers.Origin = "null";
        await boundary.InvokeAsync(nullOrigin, Allow);
        Assert.Equal(StatusCodes.Status403Forbidden, nullOrigin.Response.StatusCode);

        // Paths outside /api and /hub are not the boundary's business.
        var staticFile = CreateContext(HttpMethods.Get, "/admin/index.html");
        staticFile.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        await boundary.InvokeAsync(staticFile, Allow);

        Assert.Equal(2, allowed);
        return;

        Task Allow(HttpContext _)
        {
            allowed++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CrossSiteFetchAndBrowserCommandRules_PreserveNativeRequests()
    {
        var warnings = 0;
        var downstreamCalls = 0;
        var boundary = new ApiRequestBoundary(
            origin => string.Equals(origin, TrustedOrigin, StringComparison.OrdinalIgnoreCase),
            new CrossSiteRejectionWarningGate(),
            (_, _, _, _) => warnings++);

        var crossSite = CreateContext(HttpMethods.Get, "/api/overlay/state");
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        await boundary.InvokeAsync(crossSite, CountDownstream);
        Assert.Equal(StatusCodes.Status403Forbidden, crossSite.Response.StatusCode);

        var missingMarker = CreateContext(HttpMethods.Post, "/api/admin/kill");
        missingMarker.Request.Headers.Origin = TrustedOrigin;
        await boundary.InvokeAsync(missingMarker, CountDownstream);
        Assert.Equal(StatusCodes.Status403Forbidden, missingMarker.Response.StatusCode);

        var trustedBrowser = CreateContext(HttpMethods.Post, "/api/admin/kill");
        trustedBrowser.Request.Headers.Origin = TrustedOrigin;
        trustedBrowser.Request.Headers["X-DigiChat-Command"] = "1";
        await boundary.InvokeAsync(trustedBrowser, CountDownstream);

        var native = CreateContext(HttpMethods.Post, "/api/admin/kill");
        await boundary.InvokeAsync(native, CountDownstream);

        Assert.Equal(1, warnings);
        Assert.Equal(2, downstreamCalls);
        return;

        Task CountDownstream(HttpContext _)
        {
            downstreamCalls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void WarningBudget_ResetsDeterministicallyAfterWindow()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var gate = new CrossSiteRejectionWarningGate(
            timeProvider: time,
            permitLimit: 2,
            window: TimeSpan.FromMinutes(1));

        Assert.True(gate.TryAcquire());
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(gate.TryAcquire());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.TryAcquire());
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
    }

    [Fact]
    public async Task WarningSinkFailure_DoesNotChangeTheForbiddenResponse()
    {
        var downstreamCalls = 0;
        var boundary = new ApiRequestBoundary(
            _ => false,
            new CrossSiteRejectionWarningGate(),
            (_, _, _, _) => throw new IOException("Injected log sink failure"));
        var context = CreateContext(HttpMethods.Get, "/api/admin/status");
        context.Request.Headers.Origin = "https://attacker.example";

        await boundary.InvokeAsync(context, _ =>
        {
            downstreamCalls++;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, downstreamCalls);
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = Stream.Null;
        return context;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan by) => utcNow += by;
    }
}
