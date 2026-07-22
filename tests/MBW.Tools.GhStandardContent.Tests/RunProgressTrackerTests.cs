using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Tests;

public sealed class RunProgressTrackerTests
{
    [Fact]
    public void TracksQueuedRunningCompletedAndDrainStatus()
    {
        List<RunProgress> snapshots = [];
        RunProgressTracker tracker = new(3, snapshots.Add);

        tracker.Start("owner/first");
        tracker.Start("owner/second");
        tracker.Start("owner/third");
        tracker.Complete("owner/first");
        tracker.Complete("owner/second");
        tracker.Complete("owner/third");

        Assert.Equal(RunProgressPhase.Starting, snapshots[0].Phase);
        Assert.Equal((0, 0, 3), (snapshots[0].Completed, snapshots[0].Running, snapshots[0].Queued));

        Assert.Equal(RunProgressPhase.Processing, snapshots[1].Phase);
        Assert.Equal("owner/first", snapshots[1].StatusRepository);
        Assert.Equal((0, 1, 2), (snapshots[1].Completed, snapshots[1].Running, snapshots[1].Queued));

        Assert.Equal(RunProgressPhase.Waiting, snapshots[3].Phase);
        Assert.Equal("owner/first", snapshots[3].StatusRepository);
        Assert.Equal((0, 3, 0), (snapshots[3].Completed, snapshots[3].Running, snapshots[3].Queued));

        Assert.Equal(RunProgressPhase.Waiting, snapshots[4].Phase);
        Assert.Equal("owner/second", snapshots[4].StatusRepository);
        Assert.Equal((1, 2, 0), (snapshots[4].Completed, snapshots[4].Running, snapshots[4].Queued));

        RunProgress final = snapshots[^1];
        Assert.Equal(RunProgressPhase.Finalizing, final.Phase);
        Assert.Equal((3, 0, 0), (final.Completed, final.Running, final.Queued));
        Assert.Empty(final.ActiveRepositories);
    }

    [Fact]
    public void EmptyRunImmediatelyFinalizes()
    {
        RunProgress? snapshot = null;

        _ = new RunProgressTracker(0, value => snapshot = value);

        Assert.NotNull(snapshot);
        Assert.Equal(RunProgressPhase.Finalizing, snapshot.Phase);
        Assert.Equal(0, snapshot.Total);
    }
}
