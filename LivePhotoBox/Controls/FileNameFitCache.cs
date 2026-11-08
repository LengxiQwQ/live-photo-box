using System.Collections.Generic;

namespace LivePhotoBox.Controls;

internal readonly record struct FileNameFitKey(string Text, double Width, string FontFamily,
    double FontSize, ushort Weight, int Style, int Stretch, int Spacing, string Language,
    int FlowDirection, bool ScaleEnabled, double TextScale, double RasterizationScale);

// Cached strings only; never retain XAML controls, containers, or media files.
internal sealed class FileNameFitCache(int capacity)
{
    private readonly object _sync = new();
    private readonly Dictionary<FileNameFitKey, LinkedListNode<(FileNameFitKey Key, string Text)>> _items = [];
    private readonly LinkedList<(FileNameFitKey Key, string Text)> _recent = [];
    public int Count { get { lock (_sync) return _items.Count; } }

    public bool TryGet(FileNameFitKey key, out string text)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(key, out var node)) { text = ""; return false; }
            _recent.Remove(node);
            _recent.AddFirst(node);
            text = node.Value.Text;
            return true;
        }
    }

    public void Store(FileNameFitKey key, string text)
    {
        lock (_sync)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                existing.Value = (key, text);
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                return;
            }
            if (capacity <= 0) return;
            var node = _recent.AddFirst((key, text));
            _items.Add(key, node);
            while (_items.Count > capacity && _recent.Last is { } oldest)
            {
                _items.Remove(oldest.Value.Key);
                _recent.RemoveLast();
            }
        }
    }
}
