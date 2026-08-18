using Microsoft.AspNetCore.Http;

namespace DigiChat.Api;

/// <summary>
/// Rejects browser traffic that did not originate from DigiChat and enforces
/// the browser-only command marker. Native requests deliberately have no
/// Origin and continue to support local recovery tooling.
/// </summary>
internal sealed class ApiRequestBoundary(
    Func<string, bool> isTrustedOrigin,
    CrossSiteRejectionWarningGate warningGate,
    Action<string, string, string, string> logWarning)
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var isApi = context.Request.Path.StartsWithSegments("/api");
        var isHub = context.Request.Path.StartsWithSegments("/hub");
        if (isApi || isHub)
        {
            var requestOrigin = context.Request.Headers.Origin.ToString();
            var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
            var untrustedOrigin = !string.IsNullOrEmpty(requestOrigin)
                && !isTrustedOrigin(requestOrigin);
            var crossSiteWithoutOrigin = string.IsNullOrEmpty(requestOrigin)
                && string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase);
            if (untrustedOrigin || crossSiteWithoutOrigin)
            {
                // Logging is best-effort; rejection is unconditional.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;

                // One process-wide gate: attacker-controlled Origin values do
                // not allocate partitions or bypass the warning budget.
                if (warningGate.TryAcquire())
                {
                    try
                    {
                        logWarning(
                            context.Request.Method,
                            context.Request.Path.ToString(),
                            requestOrigin,
                            fetchSite);
                    }
                    catch
                    {
                        // A broken sink must not turn a refused request into a
                        // different response or bypass the cross-site guard.
                    }
                }

                await context.Response.WriteAsJsonAsync(new { error = "Cross-site request refused." });
                return;
            }

            // Browser-issued commands must come from our own UI code. Native
            // tools have no Origin header and remain usable for recovery.
            var isMutation = !HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method);
            if (isApi && isMutation
                && !string.IsNullOrEmpty(requestOrigin)
                && context.Request.Headers["X-DigiChat-Command"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Missing DigiChat command header." });
                return;
            }
        }

        await next(context);
    }
}

/// <summary>
/// A small global fixed-window budget for cross-site rejection warnings. It
/// controls only logging; it never changes the 403 response and is independent
/// of the trusted state-read rate limiter.
/// </summary>
internal sealed class CrossSiteRejectionWarningGate
{
    internal const int DefaultPermitLimit = 8;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private DateTimeOffset _windowStartedUtc;
    private int _issued;

    public CrossSiteRejectionWarningGate(
        TimeProvider? timeProvider = null,
        int permitLimit = DefaultPermitLimit,
        TimeSpan? window = null)
    {
        if (permitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(permitLimit));
        if (window is { } requestedWindow && requestedWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _permitLimit = permitLimit;
        _window = window ?? DefaultWindow;
        _windowStartedUtc = _timeProvider.GetUtcNow();
    }

    public bool TryAcquire()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            // Treat a wall-clock rollback as a new window rather than leaving
            // warning visibility suppressed for an unexpectedly long period.
            if (now < _windowStartedUtc || now - _windowStartedUtc >= _window)
            {
                _windowStartedUtc = now;
                _issued = 0;
            }

            if (_issued >= _permitLimit) return false;
            _issued++;
            return true;
        }
    }
}
