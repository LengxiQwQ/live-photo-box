using LivePhotoBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Helpers;

internal static class ScanResultsPresenter
{
    // Wait behind collection/binding notifications, then realize the visible
    // viewport. This does not load every offscreen row or await thumbnails.
    public static Task WaitForLayoutAsync(FrameworkElement owner, CancellationToken token, params FrameworkElement[] lists) =>
        QueuedUiOperation.RunAsync(action => owner.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => action()), () =>
        {
            token.ThrowIfCancellationRequested();
            if (!owner.IsLoaded) return;
            owner.UpdateLayout();
            foreach (var list in lists)
            {
                if (!list.IsLoaded) continue;
                if (list.Visibility == Visibility.Visible) list.UpdateLayout();
                VisualTreeHelperExtensions.FindDescendant<ScrollViewer>(list)?.ChangeView(null, 0, null, disableAnimation: true);
                if (list.Visibility == Visibility.Visible) list.UpdateLayout();
            }
        });
}
