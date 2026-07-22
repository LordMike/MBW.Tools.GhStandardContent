namespace MBW.Tools.GhStandardContent.Core;

internal sealed class RunProgressTracker
{
    private readonly List<string> _active = [];
    private readonly Action<RunProgress> _progressChanged;
    private readonly object _sync = new();
    private readonly int _total;
    private int _completed;
    private int _started;

    public RunProgressTracker(int total, Action<RunProgress> progressChanged)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        _total = total;
        _progressChanged = progressChanged;
        Publish(total == 0 ? RunProgressPhase.Finalizing : RunProgressPhase.Starting, null);
    }

    public void Start(string repository)
    {
        lock (_sync)
        {
            _started++;
            _active.Add(repository);
            if (_started < _total)
                Publish(RunProgressPhase.Processing, repository);
            else
                Publish(RunProgressPhase.Waiting, _active[0]);
        }
    }

    public void Complete(string repository)
    {
        lock (_sync)
        {
            _active.Remove(repository);
            _completed++;

            if (_completed == _total)
            {
                Publish(RunProgressPhase.Finalizing, null);
                return;
            }

            if (_started < _total)
            {
                Publish(RunProgressPhase.Processing, _active.LastOrDefault());
                return;
            }

            Publish(RunProgressPhase.Waiting, _active[0]);
        }
    }

    private void Publish(RunProgressPhase phase, string? statusRepository)
    {
        _progressChanged(new RunProgress(
            _total,
            _completed,
            _active.Count,
            _total - _started,
            _active.ToArray(),
            phase,
            statusRepository));
    }
}
