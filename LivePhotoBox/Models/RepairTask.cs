using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.ComponentModel;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 修复队列中的一个任务格子。
    /// 可以包含 1 个文件（单独照片/视频）或 2 个文件（配对的实况照片+视频）。
    /// 将内部 RepairFileEntry 的属性展开为 File1*/File2* 前缀的扁平属性，方便 XAML x:Bind 绑定。
    /// </summary>
    public partial class RepairTask : ObservableObject
    {
        #region Internal Entries

        private RepairFileEntry? _file1Entry;
        private RepairFileEntry? _file2Entry;

        /// <summary>第一个文件条目（总是存在）</summary>
        public RepairFileEntry? File1Entry
        {
            get => _file1Entry;
            private set
            {
                if (_file1Entry != null) _file1Entry.PropertyChanged -= OnFile1PropertyChanged;
                _file1Entry = value;
                if (_file1Entry != null) _file1Entry.PropertyChanged += OnFile1PropertyChanged;
                SyncAllBindings();
            }
        }

        /// <summary>第二个文件条目（仅配对时存在）</summary>
        public RepairFileEntry? File2Entry
        {
            get => _file2Entry;
            private set
            {
                if (_file2Entry != null) _file2Entry.PropertyChanged -= OnFile2PropertyChanged;
                _file2Entry = value;
                if (_file2Entry != null) _file2Entry.PropertyChanged += OnFile2PropertyChanged;
                SyncAllBindings();
            }
        }

        /// <summary>所有文件条目（1 或 2 个），供处理循环遍历</summary>
        public List<RepairFileEntry> Entries { get; }

        #endregion

        #region Construction

        public RepairTask(int index1, int index2, string baseName, bool isPaired,
            RepairFileEntry file1, RepairFileEntry? file2 = null)
        {
            _index = index1;
            _file1Index = index1;
            _file2Index = index2;
            _baseName = baseName;
            _isPaired = isPaired;
            Entries = file2 != null ? [file1, file2] : [file1];
            File1Entry = file1;
            File2Entry = file2;
        }

        #endregion

        #region Flat Bindable Properties

        [ObservableProperty] private int _index;
        [ObservableProperty] private int _file1Index;
        [ObservableProperty] private int _file2Index;
        [ObservableProperty] private string _baseName = string.Empty;
        [ObservableProperty] private bool _isPaired;

        // ── File1 属性（总是可见）──
        [ObservableProperty] private string _file1Name = string.Empty;
        [ObservableProperty] private string _file1Path = string.Empty;
        [ObservableProperty] private bool _file1IsImage = true;
        [ObservableProperty] private string _file1IssueDescription = string.Empty;
        [ObservableProperty] private bool _file1IsDiagnosisError;
        [ObservableProperty] private string _file1Details = string.Empty;
        [ObservableProperty] private ProcessStatus _file1Status = ProcessStatus.Pending;
        [ObservableProperty] private ProcessStatus _file1DisplayStatus = ProcessStatus.Success;
        [ObservableProperty] private bool _file1HasErrorDetails;

        // ── File2 属性（仅配对时可见）──
        [ObservableProperty] private string _file2Name = string.Empty;
        [ObservableProperty] private string _file2Path = string.Empty;
        [ObservableProperty] private bool _file2IsImage;
        [ObservableProperty] private string _file2IssueDescription = string.Empty;
        [ObservableProperty] private bool _file2IsDiagnosisError;
        [ObservableProperty] private string _file2Details = string.Empty;
        [ObservableProperty] private ProcessStatus _file2Status = ProcessStatus.Pending;
        [ObservableProperty] private ProcessStatus _file2DisplayStatus = ProcessStatus.Success;
        [ObservableProperty] private bool _file2HasErrorDetails;

        /// <summary>是否为分组标题（实况照片组合 / 单独照片 / 单独视频）</summary>
        [ObservableProperty] private bool _isGroupHeader;
        /// <summary>分组标题文本（如 "📷 实况照片组合"）</summary>
        [ObservableProperty] private string _groupHeaderText = string.Empty;

        /// <summary>分组标题可见性</summary>
        public Visibility GroupHeaderVisibility => IsGroupHeader ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>常规任务内容可见性</summary>
        public Visibility RegularContentVisibility => IsGroupHeader ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>File2 行的可见性 — 单独文件时 Collapsed，配对时 Visible</summary>
        public Visibility File2Visibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>格子内边距 — 配对 top=16(照片80)/bot=19(视频80)，单独 bot=8(80)</summary>
        public Thickness GridPadding => IsPaired
            ? new Thickness(8, 16, 8, 19)
            : new Thickness(8, 7, 8, 8);

        /// <summary>配对缩略图可见性 — 仅配对时 Visible（配合大缩略图 56×56）</summary>
        public Visibility PairedThumbnailVisibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>单独文件缩略图可见性 — 仅单独文件时 Visible（保持原 42×42）</summary>
        public Visibility StandaloneThumbnailVisibility => IsPaired ? Visibility.Collapsed : Visibility.Visible;

        // ── 图标字形和颜色（根据文件类型自动切换）──

        private static readonly SolidColorBrush PhotoIconBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x73, 0x16));
        private static readonly SolidColorBrush VideoIconBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7));

        public string File1IconGlyph => File1IsImage ? "" : "";
        public SolidColorBrush File1IconForeground => File1IsImage ? PhotoIconBrush : VideoIconBrush;

        public string File2IconGlyph => File2IsImage ? "" : "";
        public SolidColorBrush File2IconForeground => File2IsImage ? PhotoIconBrush : VideoIconBrush;

        #endregion

        #region Group Header Factory

        /// <summary>仅供 <see cref="CreateGroupHeader"/> 使用的内部构造</summary>
        private RepairTask()
        {
            Entries = [];
        }

        /// <summary>创建一个分组标题项</summary>
        public static RepairTask CreateGroupHeader(string headerText)
        {
            return new RepairTask
            {
                IsGroupHeader = true,
                GroupHeaderText = headerText,
            };
        }

        #endregion

        #region Thumbnail

        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail == value) return;

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(() => Thumbnail = value);
                    return;
                }

                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        public Visibility ThumbnailPlaceholderVisibility =>
            Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        #endregion

        #region Property Forwarding

        private void OnFile1PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(RepairFileEntry.DisplayFileName):
                case nameof(RepairFileEntry.FileName):
                    File1Name = File1Entry?.DisplayFileName ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.FilePath):
                    File1Path = File1Entry?.FilePath ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.IsImage):
                    File1IsImage = File1Entry?.IsImage ?? true;
                    OnPropertyChanged(nameof(File1IconGlyph));
                    OnPropertyChanged(nameof(File1IconForeground));
                    break;
                case nameof(RepairFileEntry.IssueDescription):
                    File1IssueDescription = File1Entry?.IssueDescription ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.IsDiagnosisError):
                    File1IsDiagnosisError = File1Entry?.IsDiagnosisError ?? false;
                    break;
                case nameof(RepairFileEntry.Details):
                    File1Details = File1Entry?.Details ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.Status):
                    File1Status = File1Entry?.Status ?? ProcessStatus.Pending;
                    break;
                case nameof(RepairFileEntry.DisplayStatus):
                    File1DisplayStatus = File1Entry?.DisplayStatus ?? ProcessStatus.Success;
                    break;
                case nameof(RepairFileEntry.HasErrorDetails):
                    File1HasErrorDetails = File1Entry?.HasErrorDetails ?? false;
                    break;
                // Thumbnail forwarding — sync parent thumbnail when it's the only visible one
                case "Thumbnail":
                    if (!IsPaired || File1Entry?.IsImage == true)
                        Thumbnail = File1Entry?.Thumbnail;
                    break;
            }
        }

        private void OnFile2PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(RepairFileEntry.DisplayFileName):
                case nameof(RepairFileEntry.FileName):
                    File2Name = File2Entry?.DisplayFileName ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.FilePath):
                    File2Path = File2Entry?.FilePath ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.IsImage):
                    File2IsImage = File2Entry?.IsImage ?? false;
                    OnPropertyChanged(nameof(File2IconGlyph));
                    OnPropertyChanged(nameof(File2IconForeground));
                    break;
                case nameof(RepairFileEntry.IssueDescription):
                    File2IssueDescription = File2Entry?.IssueDescription ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.IsDiagnosisError):
                    File2IsDiagnosisError = File2Entry?.IsDiagnosisError ?? false;
                    break;
                case nameof(RepairFileEntry.Details):
                    File2Details = File2Entry?.Details ?? string.Empty;
                    break;
                case nameof(RepairFileEntry.Status):
                    File2Status = File2Entry?.Status ?? ProcessStatus.Pending;
                    break;
                case nameof(RepairFileEntry.DisplayStatus):
                    File2DisplayStatus = File2Entry?.DisplayStatus ?? ProcessStatus.Success;
                    break;
                case nameof(RepairFileEntry.HasErrorDetails):
                    File2HasErrorDetails = File2Entry?.HasErrorDetails ?? false;
                    break;
                // Thumbnail forwarding — paired items use the photo's thumbnail
                case "Thumbnail":
                    if (IsPaired)
                        RefreshThumbnail();
                    else
                        Thumbnail = File2Entry?.Thumbnail;
                    break;
            }
        }

        /// <summary>设置条目后全量同步所有绑定属性</summary>
        private void SyncAllBindings()
        {
            if (File1Entry != null)
            {
                File1Name = File1Entry.DisplayFileName;
                File1Path = File1Entry.FilePath;
                File1IsImage = File1Entry.IsImage;
                File1IssueDescription = File1Entry.IssueDescription;
                File1IsDiagnosisError = File1Entry.IsDiagnosisError;
                File1Details = File1Entry.Details;
                File1Status = File1Entry.Status;
                File1DisplayStatus = File1Entry.DisplayStatus;
                File1HasErrorDetails = File1Entry.HasErrorDetails;
            }

            if (File2Entry != null)
            {
                File2Name = File2Entry.DisplayFileName;
                File2Path = File2Entry.FilePath;
                File2IsImage = File2Entry.IsImage;
                File2IssueDescription = File2Entry.IssueDescription;
                File2IsDiagnosisError = File2Entry.IsDiagnosisError;
                File2Details = File2Entry.Details;
                File2Status = File2Entry.Status;
                File2DisplayStatus = File2Entry.DisplayStatus;
                File2HasErrorDetails = File2Entry.HasErrorDetails;
            }

            OnPropertyChanged(nameof(File2Visibility));
            OnPropertyChanged(nameof(PairedThumbnailVisibility));
            OnPropertyChanged(nameof(StandaloneThumbnailVisibility));
            OnPropertyChanged(nameof(File1IconGlyph));
            OnPropertyChanged(nameof(File1IconForeground));
            OnPropertyChanged(nameof(File2IconGlyph));
            OnPropertyChanged(nameof(File2IconForeground));
            RefreshThumbnail();
        }

        /// <summary>刷新缩略图 — 优先照片缩略图，否则用第一个条目的</summary>
        public void RefreshThumbnail()
        {
            Thumbnail = File1Entry?.IsImage == true
                ? File1Entry.Thumbnail
                : File2Entry?.IsImage == true
                    ? File2Entry.Thumbnail
                    : File1Entry?.Thumbnail;
        }

        #endregion
    }
}
