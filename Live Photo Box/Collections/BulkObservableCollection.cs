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
    }
}
