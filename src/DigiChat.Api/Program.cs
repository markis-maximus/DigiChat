using DigiChat.Api;
using DigiChat.Api.Hubs;
using DigiChat.Domain;
using DigiChat.Infrastructure;
using DigiChat.Infrastructure.Persistence;
using DigiChat.Infrastructure.Services;
using DigiChat.Infrastructure.Twitch;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using System.Threading.RateLimiting;

// Port binding happens after migrations and seeding. Acquire a process guard
// first so a second launch cannot touch the database and only then discover
// that port 5170 is occupied.
using var singleInstance = new Mutex(
    initiallyOwned: true,
    name: @"Local\DigiChat.SingleInstance",
    createdNew: out var ownsSingleInstance);
if (!ownsSingleInstance)
{
    Console.Error.WriteLine("DigiChat is already running for this Windows session. Close the other copy first.");
    Environment.ExitCode = 2;
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Local overrides (gitignored) hold the Twitch Client ID etc.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
// Development is a safety boundary, not merely a default value. A local file
// copied from old setup instructions must never turn `dotnet run` into live
// Twitch mode; only the explicit Production launcher may do that.
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Twitch:MockMode"] = "true",
        // Mock mode and mock storage are one safety boundary. Reassert both
        // after local/env/command-line providers so stale live credentials or
        // a copied production connection string cannot touch real history.
        ["ConnectionStrings:DigiChat"] = DesignTimeDbContextFactory.DefaultMockConnectionString,
    });

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddDigiChatInfrastructure(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddSingleton<IOverlayNotifier, SignalROverlayNotifier>();
builder.Services.AddRateLimiter(rateLimits =>
{
    rateLimits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimits.AddFixedWindowLimiter("state-reads", limiter =>
    {
        limiter.PermitLimit = 240;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 8;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.AutoReplenishment = true;
    });
});

// The origins this app is served from. DigiChat is intentionally local-only;
// refuse a configuration override that would expose unauthenticated controls
// or viewer data on a LAN interface.
var configuredOrigins = (builder.Configuration["Urls"] ?? "http://localhost:5170")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => value.TrimEnd('/'))
    .ToArray();
if (configuredOrigins.Length == 0 || configuredOrigins.Any(value =>
        !Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || !uri.IsLoopback
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || uri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)))
    throw new InvalidOperationException(
        "DigiChat may only bind absolute loopback URLs (localhost, 127.0.0.1, or ::1).");

// `Urls` is not the only way to choose an address: a `Kestrel:Endpoints` section
// in appsettings.Local.json, an ASPNETCORE_KESTREL__ENDPOINTS__* variable, or a
// command-line override all bind Kestrel directly and WIN over `Urls`. Checking
// only `Urls` therefore left the documented "loopback-only" promise bypassable
// by the very config file the setup instructions tell you to create —
// and the loopback bind is the entire security boundary here, because
// everything downstream trusts Origin-less requests as local tooling.
var kestrelEndpointUrls = builder.Configuration.GetSection("Kestrel:Endpoints")
    .GetChildren()
    .Select(endpoint => endpoint["Url"])
    .Where(url => !string.IsNullOrWhiteSpace(url))
    .SelectMany(url => url!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .ToArray();
if (kestrelEndpointUrls.Any(value =>
        !Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri) || !uri.IsLoopback))
    throw new InvalidOperationException(
        "DigiChat may only bind loopback addresses. Remove the non-loopback Kestrel "
        + "endpoint from configuration (Kestrel:Endpoints). The admin API is "
        + "unauthenticated and viewer data is readable, so exposing it on a LAN "
        + "interface publishes both to every device on the network.");
var origin = configuredOrigins[0];
// A Kestrel endpoint overrides `Urls` for the actual bind, so the page really
// served from it must be trusted too. Deriving trust from `Urls` alone let the
// admin refuse its own commands while an origin nothing listens on stayed
// trusted — the checks above already guarantee every one of these is loopback.
var boundOrigins = kestrelEndpointUrls
    .Select(value => value.TrimEnd('/'))
    .Where(value => Uri.TryCreate(value, UriKind.Absolute, out _));
// Vite's dev server runs on a different loopback port, so frontend development
// needs the looser rule. Never in Production.
var trustedOrigins = new HashSet<string>(configuredOrigins, StringComparer.OrdinalIgnoreCase);
trustedOrigins.UnionWith(boundOrigins);
if (builder.Environment.IsDevelopment())
{
    // Only the two documented Vite development servers—not every process that
    // happens to bind an arbitrary loopback port.
    trustedOrigins.UnionWith([
        "http://localhost:5173", "http://127.0.0.1:5173",
        "http://localhost:5174", "http://127.0.0.1:5174",
    ]);
}

bool IsTrustedOrigin(string value) => trustedOrigins.Contains(value.TrimEnd('/'));

// Frontends are served by this same host in normal use; CORS only matters for
// `npm run dev` (Vite) during frontend development. Uri.TryCreate rather than
// new Uri: browsers send `Origin: null` from sandboxed iframes, which throws.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(IsTrustedOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

// Backstop for the config checks above: assert on what Kestrel ACTUALLY bound,
// whatever produced it. Configuration has more paths to an address than any
// allowlist of keys can enumerate, and this is the one boundary worth checking
// twice — shut down rather than serve unauthenticated controls to a network.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?
        .Addresses ?? [];
    var exposed = addresses
        .Where(address => !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        .ToArray();
    if (exposed.Length == 0) return;

    Log.Fatal(
        "DigiChat bound a non-loopback address ({Addresses}) and is shutting down. "
        + "The admin API is unauthenticated and viewer data is readable; it must "
        + "stay on loopback. Check Kestrel:Endpoints and ASPNETCORE_URLS.",
        string.Join(", ", exposed));
    app.Lifetime.StopApplication();
});

// The admin must never be framed: otherwise a hostile page can clickjack its
// one-click stage controls while the resulting POST still has our own origin.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        // Everything is served from this origin: Vite emits one external module
        // per page and no inline script, so `script-src 'self'` costs nothing
        // and would contain an injected script if one ever got in. Both pages
        // do carry an inline <style> block, and Phaser sets style attributes on
        // its canvas, hence 'unsafe-inline' for styles only. connect-src covers
        // the SignalR hub, including its WebSocket upgrade.
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; "
            + "script-src 'self'; "
            + "style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: blob:; "
            + "connect-src 'self' ws: wss:; "
            + "font-src 'self'; "
            + "frame-ancestors 'none'; base-uri 'none'; object-src 'none'";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next();
});

// ---------------------------------------------------------------- startup init
// Data files live in the repo-root /data folder during development and next to
// the executable when published; resolve whichever exists.
string ResolveDataFile(string configured)
{
    if (Path.IsPathRooted(configured)) return configured;
    string[] candidates =
    [
        Path.Combine(app.Environment.ContentRootPath, configured),
        Path.Combine(app.Environment.ContentRootPath, "..", "..", configured),
        Path.Combine(AppContext.BaseDirectory, configured),
    ];
    return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
}

var dataOptions = app.Services.GetRequiredService<IOptions<DataOptions>>().Value;
var lineageFile = ResolveDataFile(dataOptions.LineageFile);
{
    Log.Information("Database folder: {Directory}", DatabaseLocation.GetDatabaseDirectory());
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<DigiChatDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    try
    {
        await initializer.InitializeAsync(db, lineageFile);
    }
    catch (Exception ex)
    {
        Log.Fatal(ex,
            "Database initialization failed. Is SQL Server running and the ConnectionStrings:DigiChat value correct?");
        throw;
    }
}

// Twitch connection changes — a dropped EventSub socket, a token that needs the
// device-code dance again — otherwise reach the admin panel only on the next
// admission or transition. On stream that reads as "chatters stopped appearing"
// while the panel still says Connected.
{
    var twitchStatus = app.Services.GetRequiredService<TwitchStatus>();
    var statusNotifier = app.Services.GetRequiredService<IOverlayNotifier>();
    var statusProjection = app.Services.GetRequiredService<OverlayStateService>();
    var notificationGate = app.Services.GetRequiredService<TransitionGate>();
    twitchStatus.StatusChanged += _ => notificationGate.QueueNotification(
        "Twitch connection status change",
        async notificationCt => await statusNotifier.AdminStatusAsync(
            await statusProjection.GetAdminStatusAsync(notificationCt), notificationCt));
}

app.UseRouting();
app.UseCors();

// CORS decides what a page may *read*; it does not stop the request running.
// A POST with no body and no custom header is a "simple request" — any web page
// the streamer visits can fire one at localhost and kill the generation on
// stream. So mutating requests must carry an Origin we trust. Requests with no
// Origin at all (curl, native tools) are not browser-driven and are allowed.
var requestBoundary = new ApiRequestBoundary(
    IsTrustedOrigin,
    new CrossSiteRejectionWarningGate(),
    (method, path, requestOrigin, fetchSite) =>
        Log.Warning("Refused a cross-site {Method} to {Path} from {Origin} ({FetchSite})",
            method, path, requestOrigin, fetchSite));
app.Use((ctx, next) => requestBoundary.InvokeAsync(ctx, _ => next()));

// Rate limiting runs *after* the origin check on purpose. The other way round,
// a hostile page could spend the shared read budget with 240 <img> requests —
// each correctly refused, but only after taking a permit — and the admin panel
// would then 429 on its own status pulls for the rest of the window.
app.UseRateLimiter();

// The WebSocket handshake is exempt from CORS entirely, so the hub needs its
// own origin check or any page can subscribe to the live viewer roster.
var webSocketOptions = new WebSocketOptions();
foreach (var trustedOrigin in trustedOrigins)
    webSocketOptions.AllowedOrigins.Add(trustedOrigin);
app.UseWebSockets(webSocketOptions);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // HTML must never be cached (OBS Browser Sources cache hard); the hashed
    // JS/CSS bundles it references are immutable and cache themselves fine.
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.MapHub<OverlayHub>("/hub");
app.MapGet("/", () => Results.Redirect("/admin/"));

// ---------------------------------------------------------------- state (read)
app.MapGet("/api/overlay/state",
    (OverlayStateService s, CancellationToken ct) => s.GetOverlayStateAsync(ct))
    .RequireRateLimiting("state-reads");

app.MapGet("/api/admin/status",
    (OverlayStateService s, CancellationToken ct) => s.GetAdminStatusAsync(ct))
    .RequireRateLimiting("state-reads");

app.MapGet("/api/config/layout", (IWebHostEnvironment env) =>
{
    var path = ResolveDataFile("data/layout.json");
    return File.Exists(path)
        ? Results.Content(File.ReadAllText(path), "application/json")
        : Results.NotFound(new { error = $"layout file not found at {path}" });
});

// ---------------------------------------------------------------- admin (write)
app.MapPost("/api/admin/session/start", async (
    HttpRequest request,
    SessionService s,
    CancellationToken ct) =>
{
    if (!TryReadExpectedStateHeader(
            request, "X-DigiChat-Expected-Session", minimumValue: 0, out var expectedSession))
        return Results.BadRequest(new
        {
            error = "X-DigiChat-Expected-Session must be one non-negative integer.",
        });

    return Results.Ok(await s.StartNewSessionAsync(expectedSession, ct));
});

app.MapPost("/api/admin/stage/{stage}", async (string stage, TransitionService t, CancellationToken ct) =>
{
    var normalized = stage.Replace("-", "").Replace(" ", "");
    if (!Enum.TryParse<DigivolutionStage>(normalized, ignoreCase: true, out var target)
        || !Enum.IsDefined(target))
        return Results.BadRequest(new { error = $"Unknown stage '{stage}'." });
    return Results.Ok(await t.ChangeStageAsync(target, ct));
});

// Death and reincarnation are two deliberate steps: kill, hold the moment for
// as long as you like, then hatch. Reincarnate refuses while anyone is alive.
app.MapPost("/api/admin/kill",
    async (TransitionService t, CancellationToken ct) => Results.Ok(await t.KillAsync(ct)));

app.MapPost("/api/admin/reincarnate",
    async (TransitionService t, CancellationToken ct) => Results.Ok(await t.ReincarnateAsync(ct)));

app.MapPost("/api/admin/undo", async (
    HttpRequest request,
    TransitionService t,
    CancellationToken ct) =>
{
    if (!TryReadExpectedStateHeader(
            request, "X-DigiChat-Expected-Undo", minimumValue: 1, out var expectedTransition))
        return Results.BadRequest(new
        {
            error = "X-DigiChat-Expected-Undo must be one positive integer.",
        });

    return Results.Ok(await t.UndoLastAsync(expectedTransition, ct));
});

// ---------------------------------------------------------------- dev / mock
// Simulates chat without Twitch (spec §38). Development ONLY — deliberately not
// `MockMode || IsDevelopment()`.
//
// Mock mode and mock storage are one safety boundary, but only Development
// reasserts the mock connection string. Enabling these endpoints on MockMode
// alone meant that setting `Twitch:MockMode=true` in appsettings.Local.json —
// a knob that reads exactly like "let me test without Twitch" — and launching
// the Production bat file wrote invented chatters straight into the real
// stream history. That is the precise failure the separate mock database
// exists to prevent, and SECURITY.md promises cannot happen.
var devChatEnabled = app.Environment.IsDevelopment();

// The admin page hides its Dev/Mock panel when these endpoints are absent. In
// live mode they are not mapped, and its buttons used to just 404.
app.MapGet("/api/config/features", () => Results.Ok(new { devChat = devChatEnabled }));

if (devChatEnabled)
{
    app.MapPost("/api/dev/chat", async (MockChatRequest req, AdmissionService a, CancellationToken ct) =>
    {
        var userId = req.UserId ?? $"mock-{req.Login ?? "user"}";
        var login = req.Login ?? userId;
        var msg = new ChatMessageEvent(
            MessageId: req.MessageId ?? Guid.NewGuid().ToString(),
            TwitchUserId: userId,
            Login: login,
            DisplayName: req.DisplayName ?? login,
            IsFromOtherChannel: req.FromOtherChannel ?? false);
        var result = await a.HandleAsync(msg, ct);
        return Results.Ok(new { result.Outcome, outcomeName = result.Outcome.ToString(), result.Participant });
    });

    app.MapPost("/api/dev/chat/bulk", async (int? count, AdmissionService a, CancellationToken ct) =>
    {
        var outcomes = new List<object>();
        // Clamped: this binds from the query string, so an accidental (or
        // hostile) ?count=2000000000 would take the gate two billion times.
        var requested = Math.Clamp(count ?? 5, 1, 100);
        for (var i = 1; i <= requested; i++)
        {
            var msg = new ChatMessageEvent(
                Guid.NewGuid().ToString(), $"mock-bulk-{i}", $"bulkviewer{i}", $"BulkViewer{i}", false);
            var r = await a.HandleAsync(msg, ct);
            outcomes.Add(new { msg.Login, outcome = r.Outcome.ToString() });
        }
        return Results.Ok(outcomes);
    });

    Log.Information("Dev/mock chat endpoints enabled at /api/dev/chat");
}

Log.Information("DigiChat starting. Overlay: {OverlayUrl} Admin: {AdminUrl}",
    $"{origin}/overlay/", $"{origin}/admin/");

app.Run();

static bool TryReadExpectedStateHeader(
    HttpRequest request,
    string headerName,
    int minimumValue,
    out int? value)
{
    value = null;
    if (!request.Headers.TryGetValue(headerName, out var values))
        return true;

    if (values.Count != 1
        || !int.TryParse(values[0], out var parsed)
        || parsed < minimumValue)
        return false;

    value = parsed;
    return true;
}

internal sealed record MockChatRequest(
    string? UserId, string? Login, string? DisplayName, string? MessageId, bool? FromOtherChannel);
