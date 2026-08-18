using DigiChat.Domain;
using DigiChat.Infrastructure.Persistence;
using DigiChat.Infrastructure.Seeding;
using DigiChat.Infrastructure.Services;
using DigiChat.Infrastructure.Twitch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigiChat.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDigiChatInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<DataOptions>()
            .Bind(config.GetSection(DataOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.LineageFile), "Data:LineageFile is required")
            .ValidateOnStart();
        services.AddOptions<TwitchOptions>()
            .Bind(config.GetSection(TwitchOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.TokenFile), "Twitch:TokenFile is required")
            .ValidateOnStart();
        services.AddOptions<AdmissionOptions>()
            .Bind(config.GetSection(AdmissionOptions.Section))
            .Validate(o => o.MaxParticipantsPerSession is >= 1 and <= 5000,
                "Admissions:MaxParticipantsPerSession must be between 1 and 5000")
            .ValidateOnStart();
        services.AddOptions<TransitionOptions>()
            .Bind(config.GetSection(TransitionOptions.Section))
            .Validate(o => IsFiniteDuration(o.StageChangeSeconds, 3, 60)
                        && IsFiniteDuration(o.DeathSeconds, 3, 60)
                        && IsFiniteDuration(o.ReincarnationSeconds, 5, 180),
                "Transition durations must be finite and within their safety bounds " +
                "(stage/death 3-60s; reincarnation 5-180s)")
            .ValidateOnStart();
        services.AddOptions<OverlayOptions>().Bind(config.GetSection(OverlayOptions.Section));

        var connectionString = DatabaseLocation.Resolve(
            config.GetConnectionString("DigiChat") ?? DesignTimeDbContextFactory.DefaultConnectionString);
        services.AddDbContextFactory<DigiChatDbContext>(o => o.UseSqlServer(connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<LineageSeeder>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<TransitionGate>();
        services.AddSingleton<OverlayStateService>();
        services.AddSingleton<AdmissionService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<TransitionService>();

        services.AddHttpClient("twitch");
        services.AddSingleton<TwitchTokenStore>();
        services.AddSingleton<TwitchAuthService>();
        services.AddSingleton<TwitchStatus>();
        services.AddSingleton<ITwitchStatusProvider>(sp => sp.GetRequiredService<TwitchStatus>());
        services.AddHostedService<TwitchEventSubService>();

        return services;
    }

    private static bool IsFiniteDuration(double seconds, double minimum, double maximum) =>
        double.IsFinite(seconds) && seconds >= minimum && seconds <= maximum;
}
