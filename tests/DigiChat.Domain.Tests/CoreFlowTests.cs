using DigiChat.Domain;
using DigiChat.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigiChat.Domain.Tests;

public class CoreFlowTests
{
    // ---------------------------------------------------------------- admission

    [Fact]
    public async Task FirstMessage_AdmitsViewer_AssignsLineage_AndSpawns()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        var result = await h.ChatAsync("Dave");

        Assert.Equal(AdmissionOutcome.Admitted, result.Outcome);
        Assert.NotNull(result.Participant);
        Assert.False(result.Participant!.AwaitingLineage);
        Assert.NotNull(result.Participant.FormName);
        Assert.Single(h.Notifier.Spawns);
    }

    [Fact]
    public async Task SecondMessage_DoesNotDuplicateOrRespawn()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        await h.ChatAsync("Dave");
        var second = await h.ChatAsync("Dave");

        Assert.Equal(AdmissionOutcome.AlreadyParticipant, second.Outcome);
        Assert.Single(h.Notifier.Spawns);

        var state = await h.State.GetOverlayStateAsync();
        Assert.Single(state.Participants);
    }

    [Fact]
    public async Task RepeatMessages_DoNotGrowTheIdempotencyLedger()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        await h.ChatAsync("Dave", "first-admission");
        for (var i = 0; i < 20; i++)
            Assert.Equal(AdmissionOutcome.AlreadyParticipant,
                (await h.ChatAsync("Dave", $"repeat-{i}")).Outcome);

        await using var db = await h.DbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.ProcessedChatEvents.CountAsync());
    }

    [Fact]
    public async Task RepeatMessageRedelivery_CannotAdmitViewerIntoLaterSession()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave", "initial-admission");
        Assert.Equal(AdmissionOutcome.AlreadyParticipant,
            (await h.ChatAsync("Dave", "repeat-from-first-session")).Outcome);

        await h.Sessions.StartNewSessionAsync();
        var redelivery = await h.ChatAsync("Dave", "repeat-from-first-session");

        Assert.Equal(AdmissionOutcome.DuplicateEvent, redelivery.Outcome);
        Assert.Empty((await h.State.GetOverlayStateAsync()).Participants);
    }

    [Fact]
    public async Task DuplicateEventSubMessageId_IsFullyIgnored()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        var messageId = "eventsub-msg-1";
        var first = await h.ChatAsync("Dave", messageId);
        var duplicate = await h.ChatAsync("Dave", messageId);

        Assert.Equal(AdmissionOutcome.Admitted, first.Outcome);
        Assert.Equal(AdmissionOutcome.DuplicateEvent, duplicate.Outcome);
        Assert.Single(h.Notifier.Spawns);

        await using var db = await h.DbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Participants.CountAsync());
        Assert.Equal(1, await db.Assignments.CountAsync());
    }

    [Fact]
    public async Task NoActiveSession_MeansNoAdmission()
    {
        await using var h = await TestHarness.CreateAsync();
        var result = await h.ChatAsync("Early");
        Assert.Equal(AdmissionOutcome.NoActiveSession, result.Outcome);
        Assert.Empty(h.Notifier.Spawns);
    }

    [Fact]
    public async Task IgnoredUserId_NeverAdmits()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        var result = await h.Admission.HandleAsync(
            new ChatMessageEvent(Guid.NewGuid().ToString(), "ignored-bot", "bot", "Bot", false));
        Assert.Equal(AdmissionOutcome.Ignored, result.Outcome);
    }

    [Fact]
    public async Task SharedChatMessageFromOtherChannel_IsIgnored()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        var result = await h.Admission.HandleAsync(
            new ChatMessageEvent(Guid.NewGuid().ToString(), "uid-x", "x", "X", IsFromOtherChannel: true));
        Assert.Equal(AdmissionOutcome.Ignored, result.Outcome);
    }

    // ---------------------------------------------------------------- lineage pool

    [Fact]
    public async Task ThirtyViewers_GetThirtyDistinctLineages_ThirtyFirstAwaits()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        for (var i = 1; i <= 30; i++)
        {
            var r = await h.ChatAsync($"Viewer{i}");
            Assert.Equal(AdmissionOutcome.Admitted, r.Outcome);
            Assert.False(r.Participant!.AwaitingLineage);
        }

        var overflow = await h.ChatAsync("Viewer31");
        Assert.Equal(AdmissionOutcome.Admitted, overflow.Outcome);
        Assert.True(overflow.Participant!.AwaitingLineage);

        await using var db = await h.DbFactory.CreateDbContextAsync();
        var assigned = await db.Assignments.Where(a => a.LineageId != null)
            .Select(a => a.LineageId!.Value).ToListAsync();
        Assert.Equal(30, assigned.Count);
        Assert.Equal(30, assigned.Distinct().Count()); // no silent duplicates

        var status = await h.State.GetAdminStatusAsync();
        Assert.Contains(status.Warnings, w => w.Contains("awaiting"));
    }

    [Fact]
    public async Task SessionAdmissionCap_BoundsUniqueViewerGrowth()
    {
        await using var h = await TestHarness.CreateAsync(maxParticipantsPerSession: 2);
        await h.Sessions.StartNewSessionAsync();

        Assert.Equal(AdmissionOutcome.Admitted, (await h.ChatAsync("One")).Outcome);
        Assert.Equal(AdmissionOutcome.Admitted, (await h.ChatAsync("Two")).Outcome);
        Assert.Equal(AdmissionOutcome.CapacityReached, (await h.ChatAsync("Three")).Outcome);

        await using var db = await h.DbFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.Participants.CountAsync());
        Assert.Equal(2, await db.ProcessedChatEvents.CountAsync());
    }

    // ---------------------------------------------------------------- sessions

    [Fact]
    public async Task NewSession_StartsEmpty_ReturningViewerKeepsGenerationLineage()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();

        var first = await h.ChatAsync("Dave");
        var originalLineage = first.Participant!.LineageSlug;

        await h.Sessions.StartNewSessionAsync();
        var empty = await h.State.GetOverlayStateAsync();
        Assert.Empty(empty.Participants);

        var back = await h.ChatAsync("Dave");
        Assert.Equal(AdmissionOutcome.Admitted, back.Outcome);
        Assert.Equal(originalLineage, back.Participant!.LineageSlug); // same generation → same lineage
    }

    [Fact]
    public async Task NewSession_StaleExpectedSessionCannotStartAnotherSession()
    {
        await using var h = await TestHarness.CreateAsync();

        var first = await h.Sessions.StartNewSessionAsync(expectedCurrentSessionNumber: 0);
        var staleRetry = await h.Sessions.StartNewSessionAsync(expectedCurrentSessionNumber: 0);

        Assert.True(first.Success);
        Assert.False(staleRetry.Success);
        Assert.Equal(1, (await h.State.GetAdminStatusAsync()).SessionNumber);
    }

    [Fact]
    public async Task BackendRestart_DoesNotCreateSession_AndStatePersists()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie);

        // "Restart": a brand-new context over the same database re-runs nothing
        // destructive; state is read back exactly.
        var state = await h.State.GetOverlayStateAsync();
        Assert.Equal(DigivolutionStage.Rookie, state.Stage);
        Assert.Single(state.Participants);
        Assert.Equal(1, state.SessionNumber);
    }

    // ---------------------------------------------------------------- stages

    [Fact]
    public async Task StageChange_ChangesEveryVisibleForm_AndSameStageIsNoOp()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.ChatAsync("Erin");

        var result = await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        Assert.True(result.Success);
        var change = Assert.Single(h.Notifier.StageChanges);
        Assert.Equal(DigivolutionStage.Fresh, change.FromStage);
        Assert.Equal(DigivolutionStage.Champion, change.ToStage);
        Assert.Equal(2, change.Participants.Count);
        Assert.All(change.Participants, p => Assert.NotNull(p.FormName));

        // Clicking the same stage again: no-op, no new record, no broadcast.
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        Assert.Single(h.Notifier.StageChanges);

        await using var db = await h.DbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    [Fact]
    public async Task ArbitraryStageJumps_AreAllowed()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");

        Assert.True((await h.Transitions.ChangeStageAsync(DigivolutionStage.Ultimate)).Success);
        Assert.True((await h.Transitions.ChangeStageAsync(DigivolutionStage.InTraining)).Success);
        Assert.True((await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion)).Success);

        var state = await h.State.GetOverlayStateAsync();
        Assert.Equal(DigivolutionStage.Champion, state.Stage);
    }

    [Fact]
    public async Task InvalidNumericStage_IsRejectedWithoutPersistingIt()
    {
        await using var h = await TestHarness.CreateAsync();

        var result = await h.Transitions.ChangeStageAsync((DigivolutionStage)999);

        Assert.False(result.Success);
        Assert.Equal(DigivolutionStage.Fresh, (await h.State.GetOverlayStateAsync()).Stage);
    }

    [Fact]
    public async Task ConcurrentTransitions_ReserveVisualWindowInsideTheMutationGate()
    {
        await using var h = await TestHarness.CreateAsync(transitionOptions: new TransitionOptions
        {
            StageChangeSeconds = 1,
            DeathSeconds = 1,
            ReincarnationSeconds = 1,
        });
        await h.Sessions.StartNewSessionAsync();

        var results = await Task.WhenAll(
            h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie),
            h.Transitions.ChangeStageAsync(DigivolutionStage.Champion));

        Assert.Single(results, r => r.Success);
        await using var db = await h.DbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    [Fact]
    public async Task NotificationFailure_ReconcilesWithoutChangingCommittedResult()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Gate.DrainNotificationsAsync();
        h.Notifier.Resyncs.Clear();
        h.Notifier.FailNextStageChange = true;

        var result = await h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie);
        await h.Gate.DrainNotificationsAsync();

        Assert.True(result.Success);
        Assert.Equal(DigivolutionStage.Rookie, (await h.State.GetOverlayStateAsync()).Stage);
        Assert.Single(h.Notifier.Resyncs);
        Assert.Equal(DigivolutionStage.Rookie, h.Notifier.Resyncs[0].Stage);
    }

    [Fact]
    public async Task HungNotification_TimesOut_Reconciles_AndTailContinues()
    {
        await using var h = await TestHarness.CreateAsync(
            notificationTimeout: TimeSpan.FromMilliseconds(50));
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Gate.DrainNotificationsAsync();
        h.Notifier.Resyncs.Clear();
        h.Notifier.StageChanges.Clear();
        h.Notifier.HangNextStageChange = true;

        var first = await h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie);
        await h.Gate.DrainNotificationsAsync();

        Assert.True(first.Success);
        Assert.Contains(h.Notifier.Resyncs, s => s.Stage == DigivolutionStage.Rookie);

        var second = await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        await h.Gate.DrainNotificationsAsync();

        Assert.True(second.Success);
        Assert.Contains(h.Notifier.StageChanges,
            change => change.ToStage == DigivolutionStage.Champion);
    }

    [Fact]
    public async Task Undo_RestoresPreviousStage()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie);
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);

        var undo = await h.Transitions.UndoLastAsync();
        Assert.True(undo.Success);
        Assert.Equal(DigivolutionStage.Rookie, (await h.State.GetOverlayStateAsync()).Stage);

        // Second undo goes one step further back (records are append-only).
        await h.Transitions.UndoLastAsync();
        Assert.Equal(DigivolutionStage.Fresh, (await h.State.GetOverlayStateAsync()).Stage);
    }

    [Fact]
    public async Task Undo_StaleExpectedTransitionCannotUndoTwice()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Rookie);
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        var expectedTransition = (await h.State.GetAdminStatusAsync()).LastUndoableTransitionId;

        var first = await h.Transitions.UndoLastAsync(expectedTransition);
        var staleRetry = await h.Transitions.UndoLastAsync(expectedTransition);

        Assert.True(first.Success);
        Assert.False(staleRetry.Success);
        Assert.Equal(DigivolutionStage.Rookie, (await h.State.GetOverlayStateAsync()).Stage);
    }
}
