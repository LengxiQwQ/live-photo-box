using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class SplitTask : ObservableObject
    {
        #region Observable Properties

        [ObservableProperty] private int _index;
        [ObservableProperty] private string _sourceFileName = string.Empty;
        [ObservableProperty] private string _sourcePath = string.Empty;
        [ObservableProperty] private string _fileSize = string.Empty;
        [ObservableProperty] private string _progressText = "0%";
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;

        #endregion

        #region Thumbnail

        private volatile int _thumbnailLoadState;
        private ImageSource? _thumbnail;

        private int _thumbnailGeneration = 0;
        private CancellationTokenSource? _thumbnailCts;

        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail != null) return _thumbnail;
                if (string.IsNullOrWhiteSpace(SourcePath)) return null;
                if (SplitThumbnailService.GetCached(SourcePath) is { } cachedThumbnail)
                {
                    _thumbnail = cachedThumbnail;
                    return _thumbnail;
                }
                return _thumbnail;
            }
            set
            {
                if (SetProperty(ref _thumbnail, value))
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        partial void OnSourcePathChanged(string value)
        {
            CancelThumbnailLoad();
            Thumbnail = SplitThumbnailService.GetCached(value);
            OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        }

        public void CancelThumbnailLoad()
        {
            Interlocked.Increment(ref _thumbnailGeneration);
            if (_thumbnailCts != null)
            {
                try { _thumbnailCts.Cancel(); } catch { }
                _thumbnailCts = null;
            }

            Interlocked.Exchange(ref _thumbnailLoadState, 0);
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
        }

        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
        }

        public Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null, bool forceLoad = false)
        {
            if (_thumbnail != null || string.IsNullOrWhiteSpace(SourcePath))
            {
                return Task.CompletedTask;
            }

            if (SplitThumbnailService.GetCached(SourcePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return Task.CompletedTask;
            }

            if (Interlocked.CompareExchange(ref _thumbnailLoadState, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            return EnsureThumbnailCoreAsync(dispatcher, forceLoad);
        }

        private async Task EnsureThumbnailCoreAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher, bool forceLoad)
        {
            int currentGen = Interlocked.Increment(ref _thumbnailGeneration);

            var cts = new CancellationTokenSource();
            _thumbnailCts = cts;
            var token = cts.Token;

            try
            {
                // ✨ 核心优化：滑动防抖 (Debounce)
                if (!forceLoad)
                {
                    await Task.Delay(150, token).ConfigureAwait(false);
                }

                if (currentGen != _thumbnailGeneration || token.IsCancellationRequested) return;

                dispatcher ??= App.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

                // 去掉了强行忽略令牌的 forceLoad ? CancellationToken.None : token
                var loadedThumbnail = await SplitThumbnailService.LoadAsync(SourcePath, dispatcher, token);

                if (currentGen == _thumbnailGeneration && !token.IsCancellationRequested && loadedThumbnail != null)
                {
                    Thumbnail = loadedThumbnail;
                }
            }
            catch (OperationCanceledException) { /* 忽略正常的取消异常 */ }
            catch { }
            finally
            {
                if (currentGen == _thumbnailGeneration)
                {
                    Interlocked.Exchange(ref _thumbnailLoadState, 0);
                }
            }
        }

        #endregion

        #region Computed Properties

        public string DisplaySourceFileName => TruncateFileName(SourceFileName);

        public string DisplayStatus
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Details))
                    return Details;

                return Status switch
                {
                    ProcessStatus.Pending => ResourceService.GetString("SplitPage_Task_Pending"),
                    ProcessStatus.Processing => ResourceService.GetString("SplitPage_Task_Processing"),
                    ProcessStatus.Success => ResourceService.GetString("SplitPage_Task_Success"),
                    ProcessStatus.Failed => ResourceService.GetString("Task_Failed"),
                    _ => Status.ToString()
                };
            }
        }

        #endregion

        #region Helpers

        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= 30) return fileName;
            return $"{nameWithoutExt.Substring(0, 22)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }

        #endregion
    }
}