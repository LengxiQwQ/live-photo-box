using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace LivePhotoBox.Collections
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotifications;

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (_suppressNotifications)
            {
                return;
            }

            base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (_suppressNotifications)
            {
                return;
            }

            base.OnPropertyChanged(e);
        }

        public void ReplaceRange(IEnumerable<T> items)
        {
            _suppressNotifications = true;
            try
            {
                Items.Clear();
                // Convert IEnumerable to List to ensure it's fully consumed
                var itemsList = items is List<T> list ? list : items.ToList();
                foreach (var item in itemsList)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotifications = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        // Source queues may still publish one batch Reset; an unchanged scan
        // must not rebuild subscriptions or cause a redundant view projection.
        public void ReplaceRangeIfChanged(IEnumerable<T> items)
        {
            var snapshot = items.ToList();
            if (!this.SequenceEqual(snapshot)) ReplaceRange(snapshot);
        }

        public void AddRange(IEnumerable<T> items)
        {
            var batch = items.ToArray();
            if (batch.Length == 0) return;
            _suppressNotifications = true;
            try { foreach (var item in batch) Items.Add(item); }
            finally { _suppressNotifications = false; }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        // Source queue only: ListViews bind separate display collections.
        // Publish the actual new rows so observers do not rescan every old row.
        public void AppendSourceBatch(IEnumerable<T> items)
        {
            var batch = items.ToArray();
            if (batch.Length == 0) return;
            int start = Items.Count;
            _suppressNotifications = true;
            try { foreach (var item in batch) Items.Add(item); }
            finally { _suppressNotifications = false; }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, batch, start));
        }

        public void AppendNumberedBatch(IReadOnlyList<T> items, System.Action<T, int> number)
        {
            for (int i = 0; i < items.Count; i++) number(items[i], Count + i + 1);
            AppendSourceBatch(items);
        }
    }
}
