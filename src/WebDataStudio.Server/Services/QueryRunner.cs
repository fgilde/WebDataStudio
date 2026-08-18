using System.Collections.Concurrent;

namespace WebDataStudio.Server.Services;

/// Tracks in-flight query runs so a second request can cancel one.
public sealed class QueryRunner
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();

    public (string RunId, CancellationTokenSource Source) Start(CancellationToken requestAborted)
    {
        var runId = Guid.NewGuid().ToString("n");
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _runs[runId] = source;
        return (runId, source);
    }

    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Finish(string runId)
    {
        if (_runs.TryRemove(runId, out var source)) source.Dispose();
    }
}
