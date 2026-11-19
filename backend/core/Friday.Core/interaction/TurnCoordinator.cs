namespace Friday.Core.Interaction;

// A canceled or replaced turn can never publish output, even if its provider ignores cancellation.
public sealed class TurnCoordinator : IDisposable
{
    private readonly object _sync = new();
    private long _version;
    private Turn? _current;
    public Turn Begin(CancellationToken token = default)
    {
        lock (_sync)
        {
            Cancel();
            return _current = new Turn(this, ++_version, token);
        }
    }
    public void Cancel()
    {
        lock (_sync) { ++_version; _current?.Cancel(); _current = null; }
    }
    public void Dispose() => Cancel();
    public sealed class Turn : IDisposable
    {
        private readonly TurnCoordinator _owner;
        private readonly long _id;
        private readonly CancellationTokenSource _source;
        private bool _disposed;
        internal Turn(TurnCoordinator owner, long id, CancellationToken token)
        { _owner = owner; _id = id; _source = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public CancellationToken Token => _source.Token;
        public bool IsCurrent { get { lock (_owner._sync) return !_disposed && !_source.IsCancellationRequested && _owner._version == _id; } }
        public void EnsureCurrent() { if (!IsCurrent) throw new OperationCanceledException(); }
        internal void Cancel() { if (!_disposed) _source.Cancel(); }
        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (ReferenceEquals(_owner._current, this)) _owner._current = null;
                _source.Dispose();
            }
        }
    }
}
