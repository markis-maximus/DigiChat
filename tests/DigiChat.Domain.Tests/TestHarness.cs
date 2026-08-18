using DigiChat.Domain;
using DigiChat.Domain.Views;
using DigiChat.Infrastructure;
using DigiChat.Infrastructure.Persistence;
using DigiChat.Infrastructure.Seeding;
using DigiChat.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DigiChat.Domain.Tests;

/// <summary>
/// Runs the real services (admission, session, transition) against an
/// in-memory SQLite database seeded from the real data/lineages.json roster.
/// Transition visual windows are zero so nothing blocks.
/// </summary>
public sealed class TestHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public IDbContextFactory<DigiChatDbContext> DbFactory { get; }
    public FakeNotifier Notifier { get; } = new();
    public TransitionGate Gate { get; }
    public AdmissionService Admission { get; }
    public SessionService Sessions { get; }
    public TransitionService Transitions { get; }
    public OverlayStateService State { get; }

    private TestHarness(
        SqliteConnection connection,
        int maxParticipantsPerSession,
        TransitionOptions? transitionOptions,
        TimeSpan? notificationTimeout)
    {
        _connection = connection;
        Gate = new TransitionGate(
            NullLogger<TransitionGate>.Instance, notificationTimeout);
        var options = new DbContextOptionsBuilder<DigiChatDbContext>()
            .UseSqlite(connection)
            .Options;
        DbFactory = new SimpleFactory(options);

        var twitch = Options.Create(new TwitchOptions { MockMode = true, IgnoredUserIds = ["ignored-bot"] });
        var admissions = Options.Create(new AdmissionOptions
        {
            MaxParticipantsPerSession = maxParticipantsPerSession,
        });
        var transition = Options.Create(transitionOptions ?? new TransitionOptions
        {
            StageChangeSeconds = 0,
            DeathSeconds = 0,
            ReincarnationSeconds = 0,
        });
        var overlay = Options.Create(new OverlayOptions());
        var clock = new SystemClock();

        State = new OverlayStateService(DbFactory, new FixedStatus(), Gate, overlay);
        Admission = new AdmissionService(DbFactory, Gate, Notifier, State, clock, twitch, admissions,
            NullLogger<AdmissionService>.Instance);
        Sessions = new SessionService(DbFactory, Gate, Notifier, State, clock,
            NullLogger<SessionService>.Instance);
        Transitions = new TransitionService(DbFactory, Gate, Notifier, State, clock, transition,
            NullLogger<TransitionService>.Instance);
    }

    public static async Task<TestHarness> CreateAsync(
        int maxParticipantsPerSession = 500,
        TransitionOptions? transitionOptions = null,
        TimeSpan? notificationTimeout = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var harness = new TestHarness(
            connection, maxParticipantsPerSession, transitionOptions, notificationTimeout);

        await using var db = await harness.DbFactory.CreateDbContextAsync();
        var seeder = new LineageSeeder(NullLogger<LineageSeeder>.Instance);
        var initializer = new DatabaseInitializer(seeder, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync(db, FindRosterFile());
        return harness;
    }

    /// <summary>Walks up from the test bin folder to the repo's data/lineages.json.</summary>
    public static string FindRosterFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "lineages.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("data/lineages.json not found above test directory");
    }

    public Task<AdmissionResult> ChatAsync(string user, string? messageId = null) =>
        Admission.HandleAsync(new ChatMessageEvent(
            messageId ?? Guid.NewGuid().ToString(), $"uid-{user}", user.ToLowerInvariant(), user, false));

    public async ValueTask DisposeAsync()
    {
        await Gate.DrainNotificationsAsync();
        await _connection.DisposeAsync();
    }

    private sealed class SimpleFactory(DbContextOptions<DigiChatDbContext> options)
        : IDbContextFactory<DigiChatDbContext>
    {
        public DigiChatDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedStatus : ITwitchStatusProvider
    {
        public string Status => "Test";
    }
}

/// <summary>Records every outbound notification for assertions.</summary>
public sealed class FakeNotifier : IOverlayNotifier
{
    public List<SpawnEventView> Spawns { get; } = [];
    public List<StageChangeView> StageChanges { get; } = [];
    public List<DeathView> Deaths { get; } = [];
    public List<ReincarnationView> Reincarnations { get; } = [];
    public List<OverlayStateView> Resyncs { get; } = [];
    public bool FailNextStageChange { get; set; }
    public bool HangNextStageChange { get; set; }

    public Task SpawnAsync(SpawnEventView spawn, CancellationToken ct = default)
    { Spawns.Add(spawn); return Task.CompletedTask; }
    public async Task StageChangedAsync(StageChangeView change, CancellationToken ct = default)
    {
        if (HangNextStageChange)
        {
            HangNextStageChange = false;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        if (FailNextStageChange)
        {
            FailNextStageChange = false;
            throw new InvalidOperationException("Injected stage notification failure");
        }
        StageChanges.Add(change);
    }
    public Task DiedAsync(DeathView d, CancellationToken ct = default)
    { Deaths.Add(d); return Task.CompletedTask; }
    public Task ReincarnationAsync(ReincarnationView r, CancellationToken ct = default)
    { Reincarnations.Add(r); return Task.CompletedTask; }
    public Task StateResyncAsync(OverlayStateView s, CancellationToken ct = default)
    { Resyncs.Add(s); return Task.CompletedTask; }
    public Task AdminStatusAsync(AdminStatusView s, CancellationToken ct = default)
        => Task.CompletedTask;
}
