using DigiChat.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigiChat.Domain.Tests;

public class ReincarnationTests
{
    [Fact]
    public async Task Reincarnation_CreatesNewGeneration_ReassignsVisible_SetsFresh()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.ChatAsync("Erin");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Ultimate);

        await h.Transitions.KillAsync();
        var result = await h.Transitions.ReincarnateAsync();
        Assert.True(result.Success);

        var view = Assert.Single(h.Notifier.Reincarnations);
        Assert.Equal(2, view.NewGenerationNumber);
        Assert.Equal(2, view.Participants.Count);
        Assert.All(view.Participants, p => Assert.False(p.AwaitingLineage));

        var state = await h.State.GetOverlayStateAsync();
        Assert.Equal(DigivolutionStage.Fresh, state.Stage);
        Assert.Equal(2, state.GenerationNumber);
    }

    [Fact]
    public async Task Reincarnation_AllowsLineageReuseAcrossGenerations()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");

        await h.Transitions.KillAsync();
        await h.Transitions.ReincarnateAsync();

        await using var db = await h.DbFactory.CreateDbContextAsync();
        // Dave now has one assignment per generation; both may even be the same
        // lineage — historical reuse is explicitly allowed (spec §9).
        var assignments = await db.Assignments.Where(a => a.LineageId != null).ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(2, assignments.Select(a => a.GenerationId).Distinct().Count());
    }

    [Fact]
    public async Task AbsentViewer_DoesNotConsumeLineage_UntilTheyReturn()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");

        // Dave leaves: new session, Dave hasn't chatted, then reincarnation.
        await h.Sessions.StartNewSessionAsync();
        await h.Transitions.KillAsync();
        await h.Transitions.ReincarnateAsync();

        await using (var db = await h.DbFactory.CreateDbContextAsync())
        {
            var gen2 = await db.Generations.SingleAsync(g => g.Number == 2);
            Assert.Equal(0, await db.Assignments.CountAsync(a => a.GenerationId == gen2.Id));
        }

        // Dave returns and gets a new-generation lineage on first message.
        var back = await h.ChatAsync("Dave");
        Assert.Equal(AdmissionOutcome.Admitted, back.Outcome);
        Assert.False(back.Participant!.AwaitingLineage);
    }

    [Fact]
    public async Task Reincarnate_RefusedWhileTheGenerationIsAlive()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");

        var result = await h.Transitions.ReincarnateAsync();
        Assert.False(result.Success);
        Assert.Empty(h.Notifier.Reincarnations);

        var state = await h.State.GetOverlayStateAsync();
        Assert.Equal(1, state.GenerationNumber);
        Assert.False(state.IsDead);
    }

    [Fact]
    public async Task Kill_MarksEveryoneDead_KeepingStageAndLineages()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        var original = await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);

        var result = await h.Transitions.KillAsync();
        Assert.True(result.Success);

        // The death animation needs to know who is on screen and as what.
        var view = Assert.Single(h.Notifier.Deaths);
        var dying = Assert.Single(view.Participants);
        Assert.Equal(original.Participant!.LineageSlug, dying.LineageSlug);

        var state = await h.State.GetOverlayStateAsync();
        Assert.True(state.IsDead);
        Assert.Equal(1, state.GenerationNumber);
        Assert.Equal(DigivolutionStage.Champion, state.Stage); // corpses stay as they fell
        Assert.Equal(original.Participant!.LineageSlug, Assert.Single(state.Participants).LineageSlug);

        // Dying twice is not a thing.
        Assert.False((await h.Transitions.KillAsync()).Success);
    }

    [Fact]
    public async Task StageChange_RefusedWhileDead_SoCorpsesDoNotDigivolve()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        await h.Transitions.KillAsync();
        h.Notifier.StageChanges.Clear();

        var result = await h.Transitions.ChangeStageAsync(DigivolutionStage.Ultimate);
        Assert.False(result.Success);
        Assert.Empty(h.Notifier.StageChanges);

        // Still dead, still Champion — and the death is still what Undo undoes,
        // rather than being buried under a newer transition record.
        var state = await h.State.GetOverlayStateAsync();
        Assert.True(state.IsDead);
        Assert.Equal(DigivolutionStage.Champion, state.Stage);

        Assert.True((await h.Transitions.UndoLastAsync()).Success);
        Assert.False((await h.State.GetOverlayStateAsync()).IsDead);
    }

    [Fact]
    public async Task UndoDeath_RevivesTheSameGeneration()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        var original = await h.ChatAsync("Dave");
        await h.Transitions.ChangeStageAsync(DigivolutionStage.Champion);
        await h.Transitions.KillAsync();

        var undo = await h.Transitions.UndoLastAsync();
        Assert.True(undo.Success);

        var state = await h.State.GetOverlayStateAsync();
        Assert.False(state.IsDead);
        Assert.Equal(1, state.GenerationNumber);
        Assert.Equal(DigivolutionStage.Champion, state.Stage);
        Assert.Equal(original.Participant!.LineageSlug, Assert.Single(state.Participants).LineageSlug);
    }

    [Fact]
    public async Task Reincarnation_IsFinal_AndCannotBeUndone()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.KillAsync();
        await h.Transitions.ReincarnateAsync();

        var undo = await h.Transitions.UndoLastAsync();
        Assert.False(undo.Success);

        // Nothing rolled back: still generation 2, still Fresh.
        var state = await h.State.GetOverlayStateAsync();
        Assert.Equal(2, state.GenerationNumber);
        Assert.Equal(DigivolutionStage.Fresh, state.Stage);

        // And the admin panel offers no undo button for it.
        var status = await h.State.GetAdminStatusAsync();
        Assert.Null(status.LastUndoableAction);
    }

    [Fact]
    public async Task ChatWhileDead_IsHeldOffScreen_UntilReincarnation()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.KillAsync();
        h.Notifier.Spawns.Clear();

        // A newcomer during the death is recorded but must not walk in among
        // the corpses — they wait for the new generation.
        var late = await h.ChatAsync("Late");
        Assert.Equal(AdmissionOutcome.Admitted, late.Outcome);
        Assert.True(late.Participant!.HeldForReincarnation);
        Assert.Null(late.Participant.LineageSlug);
        Assert.Empty(h.Notifier.Spawns);

        // Listed but flagged, the same way awaiting-lineage chatters are: the
        // overlay skips both, so nothing appears among the corpses.
        var dead = await h.State.GetOverlayStateAsync();
        Assert.True(Assert.Single(dead.Participants, p => p.DisplayName == "Late").HeldForReincarnation);
        Assert.True(dead.IsDead);

        await h.Transitions.ReincarnateAsync();

        var reborn = await h.State.GetOverlayStateAsync();
        var arrival = Assert.Single(reborn.Participants, p => p.DisplayName == "Late");
        Assert.False(arrival.HeldForReincarnation);
        Assert.False(arrival.AwaitingLineage);
        Assert.NotNull(arrival.LineageSlug);
    }

    [Fact]
    public async Task ChatWhileDead_UndoAssignsLineageAndBringsViewerOnScreen()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");
        await h.Transitions.KillAsync();
        var late = await h.ChatAsync("Late");
        Assert.True(late.Participant!.HeldForReincarnation);

        var undo = await h.Transitions.UndoLastAsync();

        Assert.True(undo.Success);
        var state = await h.State.GetOverlayStateAsync();
        var revived = Assert.Single(state.Participants, p => p.DisplayName == "Late");
        Assert.False(revived.HeldForReincarnation);
        Assert.False(revived.AwaitingLineage);
        Assert.NotNull(revived.LineageSlug);
        Assert.Contains(h.Notifier.Resyncs,
            resync => resync.Participants.Any(p => p.DisplayName == "Late" && !p.AwaitingLineage));
    }

    [Fact]
    public async Task ChatDuringReincarnation_WaitsAndJoinsNewGeneration()
    {
        await using var h = await TestHarness.CreateAsync();
        await h.Sessions.StartNewSessionAsync();
        await h.ChatAsync("Dave");

        await h.Transitions.KillAsync();

        // Fire the admission while reincarnation holds the gate; the admission
        // must land in the *new* generation (spec §14).
        var reincarnate = h.Transitions.ReincarnateAsync();
        var admissionTask = h.ChatAsync("Late");
        await reincarnate;
        var admission = await admissionTask;

        Assert.Equal(AdmissionOutcome.Admitted, admission.Outcome);
        await using var db = await h.DbFactory.CreateDbContextAsync();
        var gen2 = await db.Generations.SingleAsync(g => g.Number == 2);
        var late = await db.Viewers.SingleAsync(v => v.TwitchUserId == "uid-Late");
        Assert.True(await db.Assignments.AnyAsync(a => a.GenerationId == gen2.Id && a.ViewerId == late.Id));
    }
}
