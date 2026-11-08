using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.Collections;

// UI-thread owned. Nonempty projections update existing rows without a
// Reset/Clear cycle. Explicit clear and an empty result use one atomic Reset.
internal sealed class CollectionViewUpdater<T>(ObservableCollection<T> target, Func<Action, bool> enqueue)
{
    private Func<IReadOnlyList<T>>? _projection;
    private IReadOnlyList<T>? _snapshot;
    private HashSet<T>? _desiredItems;
    private HashSet<T>? _presentItems;
    private int _offset;
    private bool _queued;
    private bool _publishAtomically;
    private TaskCompletionSource _completion = Completed();

    public Task Completion => _completion.Task;

    // Explicit clear is a single user operation, never a sequence of row edits.
    // Any already queued callback can only observe the empty/new request state.
    public void ClearImmediately()
    {
        _projection = null;
        _snapshot = null;
        _desiredItems = null;
        _presentItems = null;
        _offset = 0;
        if (target.Count > 0) target.Clear();
        _completion.TrySetResult();
    }

    public void Request(Func<IReadOnlyList<T>> projection, bool publishAtomically = false)
    {
        _completion.TrySetResult();
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _projection = projection;
        _snapshot = null;
        _desiredItems = null;
        _presentItems = null;
        _offset = 0;
        _publishAtomically = publishAtomically;
        Schedule();
    }

    private void Schedule()
    {
        if (_queued) return;
        _queued = true;
        if (enqueue(ApplyBatch)) return;
        _queued = false;
        _projection = null;
        _snapshot = null;
        _completion.TrySetCanceled();
    }

    private void ApplyBatch()
    {
        _queued = false;
        try
        {
            if (_projection != null)
            {
                _snapshot = _projection();
                _projection = null;
                if (_publishAtomically)
                {
                    if (target is not BulkObservableCollection<T> bulk)
                        throw new InvalidOperationException("Atomic presentation requires a bulk observable collection.");
                    bulk.ReplaceRangeIfChanged(_snapshot);
                    _snapshot = null;
                    _completion.TrySetResult();
                    return;
                }
                if (_snapshot.Count == 0)
                {
                    ClearImmediately();
                    return;
                }
                if (target.SequenceEqual(_snapshot))
                {
                    _snapshot = null;
                    _completion.TrySetResult();
                    return;
                }
                _desiredItems = new HashSet<T>(_snapshot);
                _presentItems = new HashSet<T>(target);
            }
            if (_snapshot == null) return;
            var snapshot = _snapshot;
            var clock = Stopwatch.StartNew();
            int remaining = 128;
            // Unchanged prefixes do not consume UI batches. Previously appending
            // 128 rows queued a separate callback for each 128 old rows too.
            while (_offset < snapshot.Count && remaining > 0)
            {
                int index = _offset++;
                if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], snapshot[index])) continue;
                int previous = _presentItems?.Add(snapshot[index]) == true ? -1 : target.IndexOf(snapshot[index]);
                if (previous > index) target.Move(previous, index);
                else if (index == target.Count) target.Add(snapshot[index]);
                else if (_desiredItems?.Contains(target[index]) == false) target[index] = snapshot[index];
                else target.Insert(index, snapshot[index]);
                remaining--;
                if (clock.ElapsedMilliseconds >= 4) break;
            }
            while (_offset >= snapshot.Count && target.Count > snapshot.Count && remaining-- > 0 && clock.ElapsedMilliseconds < 4)
                target.RemoveAt(target.Count - 1);
            if (_offset < snapshot.Count || target.Count > snapshot.Count) Schedule();
            else
            {
                _snapshot = null;
                _desiredItems = null;
                _presentItems = null;
                _completion.TrySetResult();
            }
        }
        catch (Exception error)
        {
            _snapshot = null;
            _desiredItems = null;
            _presentItems = null;
            _projection = null;
            _completion.TrySetException(error);
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
