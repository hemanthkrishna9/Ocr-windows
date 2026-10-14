namespace IndicOcr.App.Services;

/// <summary>
/// Counts active users of a shared resource (the OCR engine). Users run concurrently; shutdown
/// closes the tracker so no new user can enter, then waits until the active ones have left.
/// </summary>
internal sealed class UseTracker
{
    private readonly object _lock = new();
    private int _count;
    private bool _closed;
    private TaskCompletionSource? _idle;

    public int Count { get { lock (_lock) return _count; } }

    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_closed) return false;
            _count++;
            return true;
        }
    }

    public void Exit()
    {
        TaskCompletionSource? idle = null;
        lock (_lock)
        {
            if (_count > 0) _count--;
            if (_count == 0) idle = _idle;
        }
        idle?.TrySetResult();
    }

    /// <summary>Stops new users and completes when the active ones are done.</summary>
    public Task CloseAndWaitIdleAsync()
    {
        lock (_lock)
        {
            _closed = true;
            if (_count == 0) return Task.CompletedTask;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }
}
