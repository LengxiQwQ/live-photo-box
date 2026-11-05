using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

/// <summary>Admission, cancellation and draining are one atomic lifecycle.</summary>
internal sealed class WorkLifetime
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Lease> _active = [];
    private TaskCompletionSource _idle = Completed();
    private Task? _cancellation;
    private bool _stopping;
    internal bool IsStopping { get { lock (_gate) return _stopping; } }
    internal CancellationToken Token => _stop.Token;
    internal string[] ActiveOperations { get { lock (_gate) { var names = new List<string>(); foreach (var lease in _active) names.Add(lease.Name); return names.ToArray(); } } }

    internal Lease? TryBegin(string name)
    {
        lock (_gate)
        {
            if (_stopping) return null;
            if (_active.Count == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var lease = new Lease(this, name);
            _active.Add(lease);
            return lease;
        }
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return _cancellation ??= _stop.CancelAsync();
        }
    }

    internal Task WaitForIdleAsync() { lock (_gate) return _idle.Task; }
    private void End(Lease lease)
    {
        lock (_gate)
            if (_active.Remove(lease) && _active.Count == 0) _idle.TrySetResult();
    }
    private static TaskCompletionSource Completed() { var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(); return tcs; }
    internal sealed class Lease(WorkLifetime owner, string name) : IDisposable
    {
        private WorkLifetime? _owner = owner;
        internal string Name { get; } = name;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End(this);
    }
}
