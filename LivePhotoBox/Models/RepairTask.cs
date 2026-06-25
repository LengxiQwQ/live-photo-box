using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.ComponentModel;

namespace LivePhotoBox.Models
{
    // 修复队列中的一个任务格子。
    // 可以包含 1 个文件（单独照片/视频）或 2 个文件（配对的实况照片+视频）。
    // 将内部 RepairFileEntry 的属性展开为 File1*/File2* 前缀的扁平属性，方便 XAML x:Bind 绑定。
    public partial class RepairTask : ObservableObject
    {
        #region Internal Entries

        private RepairFileEntry? _file1Entry;
        private RepairFileEntry? _file2Entry;

        // 第一个文件条目（总是存在）
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

        // 第二个文件条目（仅配对时存在）
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

        // 所有文件条目（1 或 2 个），供处理循环遍历
        public List<RepairFileEntry> Entries { get; }

        #endregion

        #region Construction

        // 创建一个修复任务，关联 1 个（单独文件）或 2 个（配对实况照片）文件条目。
        // index1: File1 序号
        // index2: File2 序号（配对时有效）
        // baseName: 任务基础名称
        // isPaired: 是否为配对实况照片
        // file1: 第一个文件条目（总是存在）
        // file2: 第二个文件条目（可选，仅配对时提供）
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

        // 任务序号
        [ObservableProperty] private int _index;
        // File1 在列表中的序号
        [ObservableProperty] private int _file1Index;
        // File2 在列表中的序号
        [ObservableProperty] private int _file2Index;
        // 任务基础名称（不含扩展名）
        [ObservableProperty] private string _baseName = string.Empty;
        // 是否为配对实况照片（照片 + 视频）
        [ObservableProperty] private bool _isPaired;

        // ── File1 属性（总是可见）──
        // File1 文件名
        [ObservableProperty] private string _file1Name = string.Empty;
        // File1 完整路径
        [ObservableProperty] private string _file1Path = string.Empty;
        // File1 是否为图片
        [ObservableProperty] private bool _file1IsImage = true;
        // File1 问题描述
        [ObservableProperty] private string _file1IssueDescription = string.Empty;
        // File1 诊断阶段是否出错
        [ObservableProperty] private bool _file1IsDiagnosisError;
        // File1 详细错误信息
        [ObservableProperty] private string _file1Details = string.Empty;
        // File1 原始处理状态
        [ObservableProperty] private ProcessStatus _file1Status = ProcessStatus.Pending;
        // File1 UI 显示状态（无需修复时强制成功）
        [ObservableProperty] private ProcessStatus _file1DisplayStatus = ProcessStatus.Success;
        // File1 是否有错误详情
        [ObservableProperty] private bool _file1HasErrorDetails;

        // ── File2 属性（仅配对时可见）──
        // File2 文件名
        [ObservableProperty] private string _file2Name = string.Empty;
        // File2 完整路径
        [ObservableProperty] private string _file2Path = string.Empty;
        // File2 是否为图片
        [ObservableProperty] private bool _file2IsImage;
        // File2 问题描述
        [ObservableProperty] private string _file2IssueDescription = string.Empty;
        // File2 诊断阶段是否出错
        [ObservableProperty] private bool _file2IsDiagnosisError;
        // File2 详细错误信息
        [ObservableProperty] private string _file2Details = string.Empty;
        // File2 原始处理状态
        [ObservableProperty] private ProcessStatus _file2Status = ProcessStatus.Pending;
        // File2 UI 显示状态（无需修复时强制成功）
        [ObservableProperty] private ProcessStatus _file2DisplayStatus = ProcessStatus.Success;
        // File2 是否有错误详情
        [ObservableProperty] private bool _file2HasErrorDetails;

        // 是否为分组标题（实况照片组合 / 单独照片 / 单独视频）
        [ObservableProperty] private bool _isGroupHeader;
        // 分组标题文本（如 "实况照片组合"）
        [ObservableProperty] private string _groupHeaderText = string.Empty;

        // 分组标题可见性
        public Visibility GroupHeaderVisibility => IsGroupHeader ? Visibility.Visible : Visibility.Collapsed;
        // 常规任务内容可见性
        public Visibility RegularContentVisibility => IsGroupHeader ? Visibility.Collapsed : Visibility.Visible;

        // File2 行的可见性 — 单独文件时 Collapsed，配对时 Visible
        public Visibility File2Visibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

        // 格子内边距 — 配对 top/bot 对称=16（居中分隔线），单独 bot=8
        public Thickness GridPadding => IsPaired
            ? new Thickness(8, 14, 8, 14)
            : new Thickness(8, 7, 8, 8);

        // Row 0 固定高度 — 单独=48(容纳缩略图), 配对=38(纯文字行)
        public GridLength Row0Height => IsPaired ? new GridLength(38) : new GridLength(48);

        // Row 2 固定高度 — 单独=0, 配对=38(对称 Row0)
        public GridLength Row2Height => IsPaired ? new GridLength(38) : new GridLength(0);

        // 配对状态变更时刷新布局相关的绑定属性
        partial void OnIsPairedChanged(bool value)
        {
            OnPropertyChanged(nameof(GridPadding));
            OnPropertyChanged(nameof(Row0Height));
            OnPropertyChanged(nameof(Row2Height));
        }

        // 配对缩略图可见性 — 仅配对时 Visible（配合大缩略图 56×56）
        public Visibility PairedThumbnailVisibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

        // 单独文件缩略图可见性 — 仅单独文件时 Visible（保持原 42×42）
        public Visibility StandaloneThumbnailVisibility => IsPaired ? Visibility.Collapsed : Visibility.Visible;

        // ── 图标字形和颜色（根据文件类型自动切换）──

        // 照片图标颜色（橙色）
        private static readonly SolidColorBrush PhotoIconBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x73, 0x16));
        // 视频图标颜色（紫色）
        private static readonly SolidColorBrush VideoIconBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7));

        // File1 图标 Segoe MDL2 字符（照片 / 视频）
        public string File1IconGlyph => File1IsImage ? "" : "";
        // File1 图标前景色（照片橙色 / 视频紫色）
        public SolidColorBrush File1IconForeground => File1IsImage ? PhotoIconBrush : VideoIconBrush;

        // File2 图标 Segoe MDL2 字符（照片 / 视频）
        public string File2IconGlyph => File2IsImage ? "" : "";
        // File2 图标前景色（照片橙色 / 视频紫色）
        public SolidColorBrush File2IconForeground => File2IsImage ? PhotoIconBrush : VideoIconBrush;

        #endregion

        #region Group Header Factory

        // 仅供 <see cref="CreateGroupHeader"/> 使用的内部构造
        private RepairTask()
        {
            Entries = [];
        }

        // 创建一个分组标题项
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

        // 任务缩略图（优先显示照片缩略图，支持 UI 线程切换）
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

        // 缩略图占位符可见性 — 缩略图未加载时显示默认图标
        public Visibility ThumbnailPlaceholderVisibility =>
            Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        #endregion

        #region Property Forwarding

        // 监听 File1 属性变更，同步到扁平化绑定属性
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

        // 监听 File2 属性变更，同步到扁平化绑定属性
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

        // 设置条目后全量同步所有绑定属性
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

        // 刷新缩略图 — 优先照片缩略图，否则用第一个条目的
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
