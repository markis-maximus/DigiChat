using DigiChat.Domain;
using DigiChat.Infrastructure.Twitch;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DigiChat.Domain.Tests;

public class TwitchAdmissionRetryTests
{
    private static readonly ChatMessageEvent Message = new(
        "retry-message", "retry-user", "retry_user", "Retry User", false);

    [Fact]
    public async Task TransientAdmissionFailure_RetriesAndReturnsSuccessfulResult()
    {
        var attempts = 0;
        var logger = new CapturingLogger();

        var result = await TwitchEventSubService.TryHandleAdmissionWithRetryAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("Injected transient admission failure");
                return Task.FromResult(AdmissionResult.NoSession);
            },
            Message,
            logger,
            CancellationToken.None,
            [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Same(AdmissionResult.NoSession, result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, logger.Levels.Count(level => level == LogLevel.Warning));
        Assert.DoesNotContain(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task PersistentAdmissionFailure_StopsAfterBoundAndLogsFinalFailure()
    {
        var attempts = 0;
        var logger = new CapturingLogger();

        var result = await TwitchEventSubService.TryHandleAdmissionWithRetryAsync(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("Injected persistent admission failure");
            },
            Message,
            logger,
            CancellationToken.None,
            [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Null(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, logger.Levels.Count(level => level == LogLevel.Warning));
        Assert.Equal(1, logger.Levels.Count(level => level == LogLevel.Error));
    }

    [Fact]
    public async Task HostCancellation_StopsWithoutRetryOrFailureLog()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var logger = new CapturingLogger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TwitchEventSubService.TryHandleAdmissionWithRetryAsync(
                token =>
                {
                    attempts++;
                    cts.Cancel();
                    return Task.FromCanceled<AdmissionResult>(token);
                },
                Message,
                logger,
                cts.Token,
                [TimeSpan.Zero, TimeSpan.Zero]));

        Assert.Equal(1, attempts);
        Assert.Empty(logger.Levels);
    }

    [Fact]
    public void EstablishedSession_ResetsAccumulatedBackoffBeforeAbnormalDrop()
    {
        var backoff = new TwitchEventSubService.ReconnectBackoffState();

        for (var failure = 0; failure < 6; failure++)
            backoff.AdvanceAfterFailure();

        Assert.Equal(TimeSpan.FromSeconds(60), backoff.CurrentDelay);

        // RunConnectionAsync invokes this only after a valid session_welcome
        // and successful subscription. A following WebSocketException then
        // uses the reset delay before advancing again.
        backoff.MarkSessionEstablished();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.CurrentDelay);
        backoff.AdvanceAfterFailure();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.CurrentDelay);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
