/*
 * MergeViewModel.cs
 *
 * MergePage（合成页面）的视图模型，负责将图片与视频合成为实况照片。
 *
 *   - 扫描输入文件夹中的图片-视频配对
 *   - 选择合并协议（Google/HEIC/OPPO）并执行合并任务
 *   - 将结果写入输出文件夹并统计耗时
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class MergeViewModel : WorkViewModelBase
    {
        // 用于统计合并耗时的计时器。
        private Stopwatch _stopwatch = new();

        // 合并是否被用户手动停止。
        private bool _mergeStoppedByUser;

        // 所有合并任务是否已完成（成功/失败都算）。
        private bool _mergeDone;
                private bool _mergeOperationFailed;

        // 扫描进度本地计数器（基类的 _scanTotal/_scanProcessed 是 private，子类不可访问）。
        private int _mergeLocalScanTotal;
        private int _mergeLocalScanProcessed;

        // UI 更新计时器（约 60ms 间隔），用于在合并过程中刷新进度条和进度文本。
        private readonly DispatcherTimer _uiUpdateTimer;

        // 当前已完成的合并任务数（线程安全，使用 volatile）。
        private volatile int _completedTasksCount;

        // 原始文件移动目录是否已由用户手动设置（系统自动填充时不覆盖用户手动填写的值）。
        private bool _originalDirectoryUserSet;

        // 返回空字符串以隐藏全局 PageStatusBar，MergePage 使用自己的底部状态栏。
        public override string PageStatusTag => string.Empty;

        // <inheritdoc/>
        protected override string ProcessingStatusKey => "Status_Running";

        // Requested protocol only; actual backend is reported by the terminal outcome.
        protected override string ProcessingStatusText =>
            ResourceService.Format("Status_Running") + " | " +
            SelectedProtocolDisplayName;

        #region Observable Properties

        // 输入文件夹路径（用户选择的待扫描目录）。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openMergeInputFolderCommand?.NotifyCanExecuteChanged();
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanDirectoryCommand.CanExecute(null) && !IsScanning)
                {
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        // 输出文件夹路径（合并后的实况照片存放目录）。默认在输入目录下创建子目录。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openMergeOutputFolderCommand?.NotifyCanExecuteChanged();

        // 扫描到的有效配对总数。
        [ObservableProperty]
        private int _totalPairsCount = 0;

        // 扫描到的无配对图片文件数。
        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        // 扫描到的无配对视频文件数。
        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        // 扫描诊断条目集合（独立于 Tasks，不可被 Runner 执行）
        public BulkObservableCollection<ScanDiagnosticDisplayItem> ScanDiagnostics { get; } = [];

        public int ScanDiagnosticsCount => ScanDiagnostics.Count;
        public bool HasScanDiagnostics => ScanDiagnostics.Count > 0;

        // 目录选择面板是否展开显示。
        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        // 合并进度百分比（0~100）。
        [ObservableProperty]
        private double _mergeProgress = 0;

        #endregion

        #region Statistics (computed properties)

        public int PendingCount => Tasks.Count(t => t.Status == ProcessStatus.Pending);
        public int ProcessingCount => Tasks.Count(t => t.Status == ProcessStatus.Processing);
        public int SuccessCount => Tasks.Count(t => t.Status == ProcessStatus.Success);
        public int WarningCount => Tasks.Count(t => t.Status == ProcessStatus.Warning);
        public int FailedCount => Tasks.Count(t => t.Status == ProcessStatus.Failed);
        // 已取消 = 尚未开始即被取消（Cancelled 状态）+ 处理中被打断被取消（IsCancelled 标记，
        // 保留 Processing 状态显示、颜色中性）。
        public int CancelledCount => Tasks.Count(t => t.Status == ProcessStatus.Cancelled || t.IsCancelled);

        public string ActiveTaskCountText => IsProcessing
            ? ResourceService.Format("MergePage_StatusProcessing", ProcessingCount)
            : TotalPairsCount > 0
                ? ResourceService.Format("MergePage_StatusTotal", TotalPairsCount)
                : string.Empty;

        // Match the two visible lists: executable task rows and unselected rows.
        public string ScanStatsText => Tasks.Count > 0 || ScanDiagnostics.Count > 0
            ? $"{ResourceService.Format("QueueStats_Tasks", Tasks.Count)}  •  {ResourceService.Format("QueueStats_Unselected", ScanDiagnostics.Count)}"
            : string.Empty;

        // 底部状态栏耗时：扫描阶段显示扫描用时（扫描结束后冻结），
        // 用户点击处理开始后切换为处理用时（重新从 0 计时）。
        public string ElapsedTimeText
        {
            get
            {
                // 处理中/处理结束（含取消）后始终显示处理用时
                if (IsProcessing || _stopwatch.Elapsed.TotalSeconds > 0)
                    return ResourceService.Format("MergePage_StatusElapsed", _stopwatch.Elapsed.ToString(@"mm\:ss"));
                // 扫描中或扫描结束但尚未开始处理：显示扫描用时（扫描结束后冻结）
                if (IsScanning || ScanStopwatch.Elapsed.TotalSeconds > 0)
                    return ResourceService.Format("MergePage_StatusScanElapsed", ScanStopwatch.Elapsed.ToString(@"mm\:ss"));
                return ResourceService.GetString("MergePage_StatusElapsedIdle");
            }
        }

        // ── 底部栏统一属性 ──

        /// <summary>底部栏进度条数值（0~100），综合扫描和处理进度。</summary>
        /// <remarks>扫描结束后保持 100% 直到处理开始；处理结束后由 MergeProgress 接管。</remarks>
        public double FooterProgressValue
        {
            get
            {
                if (IsScanning)
                    return _mergeLocalScanTotal > 0 ? (_mergeLocalScanProcessed * 100.0 / _mergeLocalScanTotal) : 0;
                if (_mergeLocalScanTotal > 0 && !IsProcessing && !_mergeDone)
                    return 100.0; // 扫描刚完成，进度条滚到头
                return MergeProgress;
            }
        }

        /// <summary>
        /// 底部栏左侧状态文字，覆盖所有生命周期：
        /// 空闲 → Status；扫描中 → 扫描进度；处理中 → ProcessingStatusText；
        /// 暂停 → "已暂停 | ..."；停止 → "已停止"；完成 → "处理完成"。
        /// </summary>
        public string FooterStatusText
        {
            get
            {
                if (IsScanning)
                {
                    // 扫描取消中：优先显示"正在停止扫描..."
                    if (_scanCancelledByUser)
                        return ResourceService.GetString("Status_ScanCancelling");
                    return _mergeLocalScanTotal > 0
                        ? ResourceService.Format("StatusBar_Scanning_Merge", _mergeLocalScanProcessed, _mergeLocalScanTotal)
                        : ResourceService.GetString("Status_Scanning");
                }
                if (IsProcessing)
                {
                    // 停止中（用户点了停止，等待任务中断）
                    if (_cancelledByUser)
                        return ResourceService.GetString("Status_Stopping");
                    // 已暂停（暂停完成，等待用户点击继续）
                    if (IsPaused)
                        return ResourceService.GetString("Status_Paused") + " | " +
                               SelectedProtocolDisplayName;
                    // 暂停中（等待当前任务结束再进入暂停）
                    if (_isPausing)
                        return ResourceService.GetString("Status_Pausing");
                    return ProcessingStatusText;
                }
                if (_mergeStoppedByUser)
                    return ResourceService.GetString("Status_StoppedSimple");
                if (_mergeDone && Progress >= 100)
                    return ResourceService.GetString("Status_DoneSimple") + ProductOutcomePresentation.WarningSummary(WarningCount);
                // Idle / Ready — 使用 SetStatus 设置的文字（如 "初始化"、"扫描完成：..."）
                return Status;
            }
        }

        // ── 统计项可见性（零值自动隐藏） ──

        public Visibility FooterTotalVisible => TotalPairsCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterSuccessVisible => SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterWarningVisible => WarningCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterFailedVisible => FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterUnprocessedVisible => (!_mergeStoppedByUser && PendingCount > 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterCancelledVisible => (_mergeStoppedByUser && CancelledCount > 0) ? Visibility.Visible : Visibility.Collapsed;

        private void NotifyStatsChanged()
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(ProcessingCount));
            OnPropertyChanged(nameof(SuccessCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(WarningCount));
            OnPropertyChanged(nameof(CancelledCount));
            OnPropertyChanged(nameof(ActiveTaskCountText));
            OnPropertyChanged(nameof(ElapsedTimeText));
            OnPropertyChanged(nameof(ScanStatsText));
            // 底部栏统一属性
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterTotalVisible));
            OnPropertyChanged(nameof(FooterSuccessVisible));
            OnPropertyChanged(nameof(FooterFailedVisible));
            OnPropertyChanged(nameof(FooterWarningVisible));
            OnPropertyChanged(nameof(FooterUnprocessedVisible));
            OnPropertyChanged(nameof(FooterCancelledVisible));
        }

        #endregion

        #region Properties

        // 扫描按钮的文本，扫描中显示"取消"，其余显示"扫描"。
        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("MergePage_DynamicCancelText")
            : ResourceService.GetString("MergePage_DynamicScanText");

        // 合并任务列表（Observable 集合，支持高性能批量更新）。
        public BulkObservableCollection<MergeTask> Tasks { get; } = [];

        // 当前选中的合并模式（协议）索引，持久化存储到设置中。
        public string SelectedProtocolDisplayName => SelectedModeIndex switch
        {
            7 => "Apple Live Photo", 8 => "vivo (legacy)",
            _ => LivePhotoProtocol.FromIndex(SelectedModeIndex).DisplayName
        };
        public Visibility SamsungVersionVisibility => SelectedModeIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HuaweiVersionVisibility => SelectedModeIndex == 6 ? Visibility.Visible : Visibility.Collapsed;

        // 双文件协议（Apple Live Photo、vivo 旧版双文件）在合成页下拉框中的可见性（受设置中开发者实验开关控制）
        public Visibility DualFileProtocolsVisibility =>
            AppSettingsService.GetValue(AppSettingsService.KeyEnableExperimentalMergeDualFile, false)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public int SamsungVersionIndex
        {
            get => Math.Clamp(AppSettingsService.GetValue(nameof(SamsungVersionIndex), 1), 0, 1);
            set { AppSettingsService.SetValue(nameof(SamsungVersionIndex), value); OnPropertyChanged(); }
        }
        public int HuaweiVersionIndex
        {
            get => Math.Clamp(AppSettingsService.GetValue(nameof(HuaweiVersionIndex), 0), 0, 1);
            set { AppSettingsService.SetValue(nameof(HuaweiVersionIndex), value); OnPropertyChanged(); }
        }

        public int SelectedModeIndex
        {
            get
            {
                int value = AppSettingsService.GetValue(nameof(SelectedModeIndex), 2);
                // Fusion 已隐藏（index 0），若历史设置仍为 0，则回退到 Motion Photo V2。
                if (value == 0) return 2;
                // 若实验开关未启用，但当前设置为双文件（7 Apple 或 8 vivo legacy），安全回退到 2 (Motion Photo V2)
                if (!AppSettingsService.GetValue(AppSettingsService.KeyEnableExperimentalMergeDualFile, false) && (value == 7 || value == 8))
                {
                    return 2;
                }
                return value;
            }
            set
            {
                AppSettingsService.SetValue(nameof(SelectedModeIndex), value);
                OnPropertyChanged(nameof(SamsungVersionVisibility));
                OnPropertyChanged(nameof(HuaweiVersionVisibility));
                OnPropertyChanged(nameof(SelectedProtocolDisplayName));
                RefreshAvailableOutputFormats(value);

                LogService.Merge($"Live Photo format changed to index: {value}");
                OnPropertyChanged();
            }
        }

        // ── 搜索 / 排序 / 筛选 ──

        [ObservableProperty]
        private string _searchFilterText = string.Empty;

        // 排序：0=文件名，1=总大小（图+视频），2=图片大小，3=视频大小，4=拍摄日期
        [ObservableProperty]
        private int _sortIndex;

        [ObservableProperty]
        private bool _sortDescending;

        partial void OnSortDescendingChanged(bool value)
        {
            OnPropertyChanged(nameof(SortDirectionGlyph));
            RefreshTaskView();
        }

        public string SortDirectionGlyph => _sortDescending ? "" : "";

        [RelayCommand]
        private void ToggleSortDirection()
        {
            SortDescending = !SortDescending;
            OnPropertyChanged(nameof(SortDirectionGlyph));
        }

        // 筛选：null=全部
        [ObservableProperty]
        private ProcessStatus? _filterStatus;

        // 配对方式：0=文件名, 1=Apple ContentIdentifier, 2=vivo com.android.camera.livephoto
        public int PairingMethodIndex
        {
            get => AppSettingsService.GetValue(nameof(PairingMethodIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(PairingMethodIndex), value);
                OnPropertyChanged();
            }
        }

        partial void OnSearchFilterTextChanged(string value) => RefreshTaskView();
        partial void OnSortIndexChanged(int value) => RefreshTaskView();
        partial void OnFilterStatusChanged(ProcessStatus? value) => RefreshTaskView();

        public BulkObservableCollection<MergeTask> DisplayTasks { get; } = [];

        private void RefreshTaskView()
        {
            // 提前物化源数据，避免 LINQ 延迟执行与 ReplaceRange Clear() 的竞态
            var source = Tasks.ToList();

            IEnumerable<MergeTask> query = source;

            if (!string.IsNullOrWhiteSpace(SearchFilterText))
            {
                var s = SearchFilterText.Trim();
                query = query.Where(t =>
                    t.ImageFileName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    t.VideoFileName.Contains(s, StringComparison.OrdinalIgnoreCase));
            }

            if (FilterStatus.HasValue)
                query = query.Where(t => t.Status == FilterStatus.Value);

            query = SortIndex switch
            {
                0 => SortDescending
                    ? query.OrderByDescending(t => t.ImageFileName, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(t => t.ImageFileName, StringComparer.OrdinalIgnoreCase),
                1 => SortDescending
                    ? query.OrderByDescending(t => t.TotalSizeBytes)
                    : query.OrderBy(t => t.TotalSizeBytes),
                2 => SortDescending
                    ? query.OrderByDescending(t => t.ImageSizeBytes)
                    : query.OrderBy(t => t.ImageSizeBytes),
                3 => SortDescending
                    ? query.OrderByDescending(t => t.VideoSizeBytes)
                    : query.OrderBy(t => t.VideoSizeBytes),
                4 => SortDescending
                    ? query.OrderByDescending(t => t.DateTaken)
                    : query.OrderBy(t => t.DateTaken),
                _ => query
            };

            // 提前物化排序结果，再投递到新 UI 帧更新 DisplayTasks
            // 使用 Clear() + Add() 循环而非 ReplaceRange，确保 WinUI ItemsStackPanel
            // 通过 Count 从 N→0→1→2→...→N 的完整变化正确重新虚拟化（参照 EditViewModel 做法）
            var result = query.ToList();

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher is not null)
            {
                dispatcher.TryEnqueue(() =>
                {
                    DisplayTasks.Clear();
                    foreach (var item in result)
                        DisplayTasks.Add(item);
                });
            }
            else
            {
                DisplayTasks.Clear();
                foreach (var item in result)
                    DisplayTasks.Add(item);
            }
        }

        private IAsyncRelayCommand? _openMergeInputFolderCommand;
        private IAsyncRelayCommand? _openMergeOutputFolderCommand;
        private IAsyncRelayCommand? _openMergeOriginalDirCommand;

        // 在文件资源管理器中打开输入文件夹的命令（仅路径存在时启用）。
        public IAsyncRelayCommand OpenMergeInputFolderCommand => _openMergeInputFolderCommand ??= new AsyncRelayCommand(OpenMergeInputFolderAsync, () => DirectoryHelper.CanOpenFolder(InputDirectory));

        // 在文件资源管理器中打开输出文件夹的命令（非空即可，打开时自动建目录）。
        public IAsyncRelayCommand OpenMergeOutputFolderCommand => _openMergeOutputFolderCommand ??= new AsyncRelayCommand(OpenMergeOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        // 在文件资源管理器中打开原始文件存放目录的命令（非空即可，打开时自动建目录）。
        public IAsyncRelayCommand OpenMergeOriginalDirCommand => _openMergeOriginalDirCommand ??= new AsyncRelayCommand(OpenMergeOriginalDirAsync, () => !string.IsNullOrWhiteSpace(OriginalDirectory));

        // ── 转换设置 ──

        // 可用的输出格式列表（由当前协议动态决定，仅包含当前协议支持的项）。
        public ObservableCollection<OutputFormatOptionItem> AvailableOutputFormats { get; } = [];

        [ObservableProperty]
        private OutputFormatOptionItem? _selectedOutputFormat;

        partial void OnSelectedOutputFormatChanged(OutputFormatOptionItem? value)
        {
            if (value is not null && value.Id != OutputFormatIndex)
            {
                OutputFormatIndex = value.Id;
            }
        }

        // 输出格式索引（0=JPG+MP4, 1=JPG+MOV, 2=HEIC+MP4, 3=HEIC+MOV, 4=HEIC+MP4(H.265)）。
        public int OutputFormatIndex
        {
            get => AppSettingsService.GetValue(nameof(OutputFormatIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(OutputFormatIndex), value);
                LogService.Merge($"Output format changed to index: {value}");
                if (SelectedOutputFormat?.Id != value)
                {
                    SelectedOutputFormat = AvailableOutputFormats.FirstOrDefault(f => f.Id == value);
                }
                OnPropertyChanged();
            }
        }

        public void RefreshAvailableOutputFormats(int protocolIndex)
        {
            int[] available = ProtocolFormatMatrix.GetAvailableFormats(protocolIndex);
            AvailableOutputFormats.Clear();
            foreach (int formatIndex in available)
            {
                AvailableOutputFormats.Add(CreateMergeFormatOption(formatIndex));
            }

            int targetFormat = available.Contains(OutputFormatIndex)
                ? OutputFormatIndex
                : ProtocolFormatMatrix.GetDefaultFormat(protocolIndex);

            if (targetFormat != OutputFormatIndex)
            {
                OutputFormatIndex = targetFormat;
            }
            else
            {
                SelectedOutputFormat = AvailableOutputFormats.FirstOrDefault(f => f.Id == targetFormat);
            }
            OnPropertyChanged(nameof(OutputFormatIndex));
        }

        private static OutputFormatOptionItem CreateMergeFormatOption(int formatIndex)
        {
            string nameKey = formatIndex switch
            {
                ProtocolFormatMatrix.FormatJpgMp4 => "MergePage_OutputFormat_JPEG_MP4.Content",
                ProtocolFormatMatrix.FormatJpgMov => "MergePage_OutputFormat_JPEG_MOV.Content",
                ProtocolFormatMatrix.FormatHeicMp4 => "MergePage_OutputFormat_HEIC_MP4.Content",
                ProtocolFormatMatrix.FormatHeicMov => "MergePage_OutputFormat_HEIC_MOV.Content",
                ProtocolFormatMatrix.FormatHeicMp4H265 => "MergePage_OutputFormat_HEIC_MP4_H265.Content",
                _ => string.Empty
            };

            string hintKey = formatIndex switch
            {
                ProtocolFormatMatrix.FormatJpgMp4 => "MergePage_FormatHint_BestCompat",
                ProtocolFormatMatrix.FormatJpgMov => "MergePage_FormatHint_GoodCompat",
                ProtocolFormatMatrix.FormatHeicMp4 => "MergePage_FormatHint_Efficient",
                ProtocolFormatMatrix.FormatHeicMov => "MergePage_FormatHint_GoogleOnly",
                ProtocolFormatMatrix.FormatHeicMp4H265 => "MergePage_FormatHint_HEVC",
                _ => string.Empty
            };

            string fallbackName = formatIndex >= 0 && formatIndex < ProtocolFormatMatrix.FormatNames.Length
                ? ProtocolFormatMatrix.FormatNames[formatIndex]
                : string.Empty;

            string name = ResourceService.GetString(nameKey);
            if (string.IsNullOrWhiteSpace(name) || name == nameKey)
                name = fallbackName;

            return new OutputFormatOptionItem
            {
                Id = formatIndex,
                DisplayName = name,
                Hint = ResourceService.GetString(hintKey)
            };
        }

        // 命名规则：始终使用自定义模板模式（不再提供选择）。
        public int NamingRuleIndex => 2;

        // ── 自定义命名模板 ──

        // 名片段编辑集合（用户在 ListView 中拖拽编辑的片段列表）。
        public BulkObservableCollection<NamingSegment> NamingSegments { get; } = [];

        // 自定义命名模板字符串（持久化到设置）。
        public string CustomNamingPattern
        {
            get => AppSettingsService.GetValue(nameof(CustomNamingPattern), "{name}");
            set
            {
                AppSettingsService.SetValue(nameof(CustomNamingPattern), value);
                OnPropertyChanged();
                RefreshNamingPreview();
            }
        }

        // ── 分段分隔符 ──

        // 命名片段之间的分隔符（_ / - / 空格 / + / 无）。
        public int NamingSeparatorIndex
        {
            get => AppSettingsService.GetValue(nameof(NamingSeparatorIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(NamingSeparatorIndex), value);
                OnPropertyChanged();
                SyncSegmentsToTemplate();
            }
        }

        // 分隔符索引 → 实际字符映射。
        private static readonly string[] SeparatorChars = ["_", "-", " ", "+", ""];

        // 当前分隔符字符串（用于模板生成）。
        public string NamingSeparator => SeparatorChars[NamingSeparatorIndex];

        // 命名片段列表是否为空（用于显示空状态引导提示）。
        public bool IsNamingEmpty => NamingSegments.Count == 0;

        // 命名预览文本（取队列中首个任务的 BaseName 渲染模板）。
        private string _namingPreviewText = string.Empty;
        public string NamingPreviewText
        {
            get => _namingPreviewText;
            set
            {
                if (_namingPreviewText != value)
                {
                    _namingPreviewText = value;
                    OnPropertyChanged();
                }
            }
        }

        // 从 CustomNamingPattern 字符串解析填充 Segments 集合。
        public void LoadSegmentsFromTemplate()
        {
            NamingSegments.Clear();
            if (string.IsNullOrWhiteSpace(CustomNamingPattern))
            {
                CustomNamingPattern = "{name}";
            }
            var segments = LivePhotoMergeService.ParseNamingPattern(CustomNamingPattern);
            // 跳过分隔符 literal 片段（它们由 NamingSeparator 统一管理）
            var separatorChars = new HashSet<char> { '_', '-', ' ', '+' };
            foreach (var seg in segments)
            {
                // 跳过所有形式的冗余分隔符 literal（单字符 + 多字符组合）
                if (seg.Type == NamingSegmentType.Literal && !string.IsNullOrEmpty(seg.Format)
                    && seg.Format.All(c => separatorChars.Contains(c)))
                    continue;
                NamingSegments.Add(seg);
            }
            SyncSegmentsToTemplate();
        }

        // 从 NamingSegments 集合同步回 CustomNamingPattern 字符串。
        public void SyncSegmentsToTemplate()
        {
            // 过滤掉产生空字符串的片段（防御：防止 string.Join 在开头/结尾插入多余分隔符）
            var parts = NamingSegments
                .Select(s => s.ToTemplateString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            var template = string.Join(NamingSeparator, parts);
            AppSettingsService.SetValue(nameof(CustomNamingPattern), template);
            OnPropertyChanged(nameof(CustomNamingPattern));
            OnPropertyChanged(nameof(IsNamingEmpty));
            RefreshNamingPreview();
        }

        // 刷新命名预览文本。
        public void RefreshNamingPreview()
        {
            try
            {
                string sampleBaseName = ResourceService.GetString("NamingPreview_PlaceholderName");
                string preview = LivePhotoMergeService.RenderNamingTemplate(
                    CustomNamingPattern, sampleBaseName, SelectedModeIndex, 1);
                NamingPreviewText = preview;
            }
            catch
            {
                NamingPreviewText = "⚠ Invalid template";
            }
        }

        // 是否覆盖已存在的输出文件。
        public bool OverwriteExisting
        {
            get => AppSettingsService.GetValue(nameof(OverwriteExisting), false);
            set
            {
                AppSettingsService.SetValue(nameof(OverwriteExisting), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(OverwriteStatusText));
            }
        }

        // 覆盖开关右侧的状态文字（"开"/"关"）。
        public string OverwriteStatusText => OverwriteExisting
            ? ResourceService.GetString("MergePage_ToggleOn")
            : ResourceService.GetString("MergePage_ToggleOff");

        // 完成后操作索引（0=无操作, 1=移动到指定目录, 2=回收站）。
        public int AfterCompletionActionIndex
        {
            get => AppSettingsService.GetValue(nameof(AfterCompletionActionIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(AfterCompletionActionIndex), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOriginalDirSectionVisible));
            }
        }

        // 完成后操作选中"移动到指定目录"时显示原始文件目录选择区域。
        public bool IsOriginalDirSectionVisible => AfterCompletionActionIndex == 1;

        // 原始文件移动目标目录。
        public string OriginalDirectory
        {
            get => AppSettingsService.GetValue(nameof(OriginalDirectory), string.Empty);
            set
            {
                AppSettingsService.SetValue(nameof(OriginalDirectory), value);
                OnPropertyChanged();
                _openMergeOriginalDirCommand?.NotifyCanExecuteChanged();
            }
        }

        // 标记用户已手动设置原始文件移动目录（后续自动填充不再覆盖）。
        public void MarkOriginalDirectoryUserSet() => _originalDirectoryUserSet = true;

        // 自动填充原始文件移动目录，仅在用户未手动设置过时生效。
        // 公开供 Code-Behind 在浏览输出目录等场景调用。
        public void AutoFillOriginalDirectory()
        {
            if (_originalDirectoryUserSet) return;
            if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
            OriginalDirectory = Path.Combine(OutputDirectory, ResourceService.GetString("OriginalDir_SubfolderName"));
        }

        #endregion

        #region Constructor

        public MergeViewModel()
        {
            SetStatus("Status_Init");
            RefreshAvailableOutputFormats(SelectedModeIndex);
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            ScanDiagnostics.CollectionChanged += (_, change) =>
            {
                int first = Math.Max(0, change.NewStartingIndex >= 0 ? change.NewStartingIndex : change.OldStartingIndex);
                for (int i = first; i < ScanDiagnostics.Count; i++) ScanDiagnostics[i].Index = i + 1;
                OnPropertyChanged(nameof(HasScanDiagnostics));
                OnPropertyChanged(nameof(ScanDiagnosticsCount));
                OnPropertyChanged(nameof(ScanStatsText));
                UpdateIsQueueEmpty(Tasks.Count + ScanDiagnostics.Count);
            };

            // 扫描添加任务时自动同步到显示列表
            Tasks.CollectionChanged += (_, change) =>
            {
                OnPropertyChanged(nameof(ScanStatsText));
                // A previous terminal summary must not hide newly appended work.
                // Existing successes stay successful and are skipped by the runner.
                if (!IsProcessing && change.NewItems is not null &&
                    change.NewItems.OfType<MergeTask>().Any(task => task.Status == ProcessStatus.Pending))
                {
                    _mergeDone = false;
                    _mergeStoppedByUser = false;
                    SetStatus("Status_Init");
                    OnPropertyChanged(nameof(ActionBtnText));
                    NotifyStatsChanged();
                }
                RefreshTaskView();
                UpdateIsQueueEmpty(Tasks.Count + ScanDiagnostics.Count);
            };

            // 响应全局设置清除：刷新所有持久化属性 + 清空命名片段
            // 响应单项设置变更（如开发者调试工具中开启/关闭合成双文件实验功能）
            AppSettingsService.SettingChanged += key =>
            {
                if (key == AppSettingsService.KeyEnableExperimentalMergeDualFile)
                {
                    OnPropertyChanged(nameof(DualFileProtocolsVisibility));
                    OnPropertyChanged(nameof(SelectedModeIndex));
                    OnPropertyChanged(nameof(SelectedProtocolDisplayName));
                    RefreshAvailableOutputFormats(SelectedModeIndex);
                }
            };

            // 响应全局设置清除：刷新所有持久化属性 + 清空命名片段
            AppSettingsService.SettingsCleared += () =>
            {
                OnPropertyChanged(nameof(DualFileProtocolsVisibility));
                OnPropertyChanged(nameof(SelectedModeIndex));
                RefreshAvailableOutputFormats(SelectedModeIndex);
                OnPropertyChanged(nameof(OutputFormatIndex));
                OnPropertyChanged(nameof(NamingRuleIndex));
                OnPropertyChanged(nameof(CustomNamingPattern));
                OnPropertyChanged(nameof(NamingSeparatorIndex));
                OnPropertyChanged(nameof(PairingMethodIndex));
                OnPropertyChanged(nameof(OverwriteExisting));
                OnPropertyChanged(nameof(OverwriteStatusText));
                OnPropertyChanged(nameof(AfterCompletionActionIndex));
                OnPropertyChanged(nameof(IsOriginalDirSectionVisible));
                OnPropertyChanged(nameof(OriginalDirectory));
                NamingSegments.Clear();
                RefreshNamingPreview();
            };
        }

        #endregion

        #region Command-Related Properties

        // 主操作按钮（开始/停止合并）的文本。
        public override string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartMerge");
            }
        }

        // 主操作按钮图标：开始▶ / 停止■ / 正在停止■
        public string ActionBtnGlyph => IsProcessing
            ? ""                           // Stop ■
            : "";                          // Play ▶

        // 当前是否允许开始处理（扫描中不允许）。
        public override bool IsProcessingAllowed => !IsScanning && Tasks.Count > 0;

        // 当前是否可以编辑合成配置（仅处理中不可编辑，扫描时允许边扫边配）。
        public bool CanEditSelectedMode => !IsProcessing;

        // 合成页输出目录：仅处理中锁定，扫描中可切换输出目录（扫描只影响输入目录）。
        public override bool CanEditOutputConfiguration => !IsProcessing;

        // IsProcessing 变更时级联通知派生类计算属性
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(IsProcessing))
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditSelectedMode)));
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActionBtnGlyph)));
            }
        }

        #endregion

        #region WorkViewModelBase Overrides

        // <inheritdoc/>
        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
            // IsScanning 变化时底部栏状态文字/进度需重新求值：
            // 扫描结束（true→false）后要从"正在扫描文件 x/y"切换到扫描完成文案。
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterProgressValue));
        }

        // <inheritdoc/>
        protected override void OnBeginScanSession()
        {
            _mergeLocalScanTotal = 0;
            _mergeLocalScanProcessed = 0;
            AppViewModel.Instance.BeginMergeScanSession();
            OnPropertyChanged(nameof(ElapsedTimeText));
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            _mergeLocalScanTotal = snapshot.Total;
            _mergeLocalScanProcessed = snapshot.Completed;
            AppViewModel.Instance.ApplyMergeScanProgress(snapshot);
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnCompleteScanSnapshot()
        {
            _mergeLocalScanProcessed = _mergeLocalScanTotal;
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
        }

        #endregion

        #region UI Update Timer

        // UI 更新定时器回调，定期刷新合并进度和进度文本。
        // 通过 _completedTasksCount 和 TotalPairsCount 计算百分比。
        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (TotalPairsCount == 0) return;
            int currentCompleted = _completedTasksCount;
            MergeProgress = (currentCompleted * 100.0) / TotalPairsCount;
            Progress = MergeProgress;
            ProgressText = $"{currentCompleted}/{TotalPairsCount}";
            CheckAndApplyPendingState();
            // 暂停时冻结计时，恢复时继续
            if (IsPaused && _stopwatch.IsRunning)
                _stopwatch.Stop();
            else if (!IsPaused && !_stopwatch.IsRunning && !_mergeStoppedByUser && !_mergeDone)
                _stopwatch.Start();
            OnPropertyChanged(nameof(ElapsedTimeText));
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        #endregion

        #region Run State Lifecycle

        // <inheritdoc/>
        protected override void OnInitializeRunState()
        {
            _mergeOperationFailed = false;
            _mergeStoppedByUser = false;
            _mergeDone = false;
            _completedTasksCount = 0;
            MergeProgress = 0;
            Progress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            SetDirectStatus(ProcessingStatusText);
            NotifyStatsChanged();
            OnPropertyChanged(nameof(ActionBtnText));
            _uiUpdateTimer.Start();
        }

        // <inheritdoc/>
        protected override void OnFinalizeRunState()
        {
            if (_isCleaningUp) return;
            _uiUpdateTimer.Stop();

            if (_cancelledByUser)
            {
                _mergeStoppedByUser = true;
                // 将剩余等待中的任务标为"已取消"
                var cancelledText = ResourceService.GetString("Task_Cancelled");
                foreach (var task in Tasks)
                {
                    if (task.Status == ProcessStatus.Pending)
                    {
                        task.Status = ProcessStatus.Cancelled;
                        task.Details = cancelledText;
                    }
                }
                NotifyStatsChanged();
            }
            else if (!_mergeOperationFailed)
            {
                _mergeDone = true;

                if (TotalPairsCount > 0)
                {
                    MergeProgress = (_completedTasksCount * 100.0) / TotalPairsCount;
                    Progress = MergeProgress;
                    ProgressText = $"{_completedTasksCount}/{TotalPairsCount}";
                }

                if (MergeProgress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    // 使用专属多语言词条汇总合并完成统计。
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_MergeCompletedSummaryWithWarnings", total, elapsed, succeeded, failed, WarningCount);
                    LogService.Merge($"Merge completed: {succeeded} succeeded, {failed} failed in {elapsed:F1}s");

                    // 批量处理完成 → 弹系统通知（便携/注册失败渠道静默跳过）
                    NotificationService.ShowBatchCompleted(
                        ResourceService.GetString("Notif_Feature_Merge"), succeeded, failed, elapsed, OutputDirectory);
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            IsDirectoryPanelOpen = true;
        }

        /// <summary>从队列中移除指定任务（不删除源文件）</summary>
        public void RemoveTask(MergeTask task)
        {
            if (task == null || IsScanning || IsProcessing) return;
            Tasks.Remove(task);
            TotalPairsCount = Tasks.Count;
            UpdateIsQueueEmpty(Tasks.Count + ScanDiagnostics.Count);

            // 删除到最后一行 = 视为清空列表：与"清空列表"按钮走同一套完整重置逻辑，
            // 否则用户逐行删完还得再点一次"清空列表"才是真清空。
            if (Tasks.Count == 0 && ScanDiagnostics.Count == 0 && !IsProcessing && !IsScanning)
            {
                ClearState();
                return;
            }
            NotifyStatsChanged();
        }

        // <inheritdoc/>
        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            ScanDiagnostics.ReplaceRange([]);
            UpdateIsQueueEmpty(0);
            ThumbnailService.ClearCache();
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            _completedTasksCount = 0;
            MergeProgress = 0;
            Progress = 0;
            ProgressText = "0/0";
            _mergeStoppedByUser = false;
            _mergeDone = false;
            _mergeLocalScanTotal = 0;
            _mergeLocalScanProcessed = 0;
            _stopwatch.Reset();
            SetStatus("Status_Cleared");
            IsDirectoryPanelOpen = true;
            NotifyStatsChanged();
            OnPropertyChanged(nameof(ActionBtnText));
        }

        // <inheritdoc/>
        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Tick -= UiUpdateTimer_Tick;
            Tasks.ReplaceRange([]);
            UpdateIsQueueEmpty(0);
            ThumbnailService.ClearCache();
        }

        #endregion

        #region Scan Command

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick() || IsProcessing) return;
            if (IsScanning) { CancelScanning(); SetStatus("Status_ScanCancelling"); return; }
            if (string.IsNullOrWhiteSpace(InputDirectory)) { await ShowNoInputDirectoryDialogAsync("Merge"); return; }
            if (string.IsNullOrWhiteSpace(OutputDirectory))
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_LivePhotos"));
            AutoFillOriginalDirectory();
            await ScanInputsAsync(new[] { InputDirectory }, Array.Empty<string>(), replace: true);
        }

        public Task AddFilesToQueueAsync(List<string> filePaths) => ImportStorageItemsAsync(Array.Empty<string>(), filePaths);
        public Task AddFolderToQueueAsync(string folderPath) => ImportStorageItemsAsync(new[] { folderPath }, Array.Empty<string>());
        public Task ImportStorageItemsAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<string> filePaths)
            => ScanInputsAsync(folderPaths, filePaths, replace: false);

        public void ReportImportFailure(Exception error, string path = "")
        {
            LogService.Merge($"Import failed: {error}", LogLevel.Error, error);
            ScanDiagnostics.Add(ScanDiagnosticDisplayItem.FromDiagnostic(
                LivePhotoScanSupport.Failure(path, error, "ScanOperationFailed", stage: ScanDiagnosticStage.Capability)));
            SetStatus("ScanStatus_Failed");
        }

        private async Task ScanInputsAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<string> filePaths, bool replace)
        {
            if (ApplicationShutdown.IsClosing || IsProcessing || IsScanning || _isCleaningUp || (folderPaths.Count == 0 && filePaths.Count == 0)) return;
            var oldTasks = Tasks.ToList();
            var oldPending = oldTasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning)).ToList();
            var options = new MergeScanOptions
            {
                InputDirectories = folderPaths.ToArray(),
                ExplicitFiles = filePaths.Concat(replace ? Array.Empty<string>() :
                    oldPending.SelectMany(t => new[] { t.ImagePath, t.VideoPath })).ToArray(),
                Mode = PairingMethodIndex switch { 1 => PairingMode.Cid, 2 => PairingMode.Vivo, _ => PairingMode.Name },
                Recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false)
            };
            IsScanning = true;
            var token = GetScanningToken();
            BeginScanSession();
            SetStatus("Status_Scanning");
            IsDirectoryPanelOpen = false;
            try
            {
                var reporter = CreateScanProgressReporter();
                var result = await Task.Run(() => LivePhotoMergeScanService.ScanAsync(options, token, reporter), token);
                token.ThrowIfCancellationRequested();
                await ApplyScanResultAsync(() =>
                {
                    if (result.Status != ScanOperationStatus.Completed)
                    {
                        foreach (var d in result.Diagnostics) ScanDiagnostics.Add(ScanDiagnosticDisplayItem.FromDiagnostic(d));
                        SetStatus(result.Status == ScanOperationStatus.Cancelled ? "Status_ScanCancelled" : "ScanStatus_Failed");
                        return;
                    }
                    bool SamePair(MergeTask task, LivePhotoFilePairInfo pair) =>
                        string.Equals(task.ImagePath, pair.ImagePath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(task.VideoPath, pair.VideoPath, StringComparison.OrdinalIgnoreCase);
                    bool Overlaps(MergeTask task, LivePhotoFilePairInfo pair) =>
                        string.Equals(task.ImagePath, pair.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(task.VideoPath, pair.VideoPath, StringComparison.OrdinalIgnoreCase);
                    var diagnostics = result.Diagnostics.ToList();
                    var updated = replace ? new List<MergeTask>() : oldTasks.Where(t => t.Status is ProcessStatus.Success or ProcessStatus.Warning).ToList();
                    foreach (var pair in result.Pairs)
                    {
                        if (!replace && oldTasks.Any(t => SamePair(t, pair) && t.Status is (ProcessStatus.Success or ProcessStatus.Warning))) continue;
                        if (!replace && oldTasks.Any(t => Overlaps(t, pair) && !SamePair(t, pair)))
                        {
                            diagnostics.Add(QueueConflict(pair.ImagePath, new[] { pair.VideoPath }));
                            continue;
                        }
                        var existing = replace ? null : oldPending.FirstOrDefault(t => SamePair(t, pair));
                        updated.Add(existing ?? CreateScanTask(pair));
                    }
                    if (!replace)
                    {
                        foreach (var task in oldPending.Where(t => !updated.Contains(t)))
                        {
                            if (!diagnostics.Any(d => d.PrimaryPath == task.ImagePath || d.RelatedPaths.Contains(task.ImagePath, StringComparer.OrdinalIgnoreCase)))
                                diagnostics.Add(QueueConflict(task.ImagePath, new[] { task.VideoPath }));
                        }
                    }
                    var touched = options.ExplicitFiles!.Concat(result.Pairs.SelectMany(p => new[] { p.ImagePath, p.VideoPath }))
                        .Concat(diagnostics.SelectMany(d => d.RelatedPaths.Prepend(d.PrimaryPath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var retained = replace ? new List<ScanDiagnosticDisplayItem>() : ScanDiagnostics.Where(d => !touched.Contains(d.PrimaryPath)).ToList();
                    retained.AddRange(diagnostics.Select(ScanDiagnosticDisplayItem.FromDiagnostic));
                    ScanDiagnostics.ReplaceRange(retained);
                    for (int i = 0; i < updated.Count; i++) updated[i].Index = i + 1;
                    Tasks.ReplaceRange(updated);
                    TotalPairsCount = Tasks.Count;
                    StandaloneImagesCount = ScanDiagnostics.Count(d => d.DiagnosticCode == "UnmatchedImage");
                    StandaloneVideosCount = ScanDiagnostics.Count(d => d.DiagnosticCode == "UnmatchedVideo");
                    _mergeDone = false;
                    _mergeStoppedByUser = false;
                    MergeProgress = Progress = 0;
                    ProgressText = $"0/{TotalPairsCount}";
                    FlushPendingScanProgress();
                    CompleteScanSnapshot();
                    NotifyStatsChanged();
                    SetStatus(result.IsPartial ? "ScanStatus_Partial" : "Status_ScanDone", TotalPairsCount);
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!_isCleaningUp) SetStatus("Status_ScanCancelled");
            }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                if (!_isCleaningUp) ReportImportFailure(ex, options.InputDirectories?.FirstOrDefault() ?? options.ExplicitFiles?.FirstOrDefault() ?? "");
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                if (!_isCleaningUp) NotifyStatusChanged();
            }
        }

        private static ScanDiagnostic QueueConflict(string path, IReadOnlyList<string> related) => new()
        {
            DiagnosticCode = "ConflictAlreadyQueued", Severity = ScanDiagnosticSeverity.Error, Stage = ScanDiagnosticStage.Pairing,
            PrimaryPath = path, RelatedPaths = related, MessageKey = "ScanDiagnostic_ConflictAlreadyQueued",
            Summary = "The pending relationship is no longer unique. Run a complete scan before processing it."
        };

        private static MergeTask CreateScanTask(LivePhotoFilePairInfo pair) => new()
        {
            ImageFileName = Path.GetFileName(pair.ImagePath), VideoFileName = Path.GetFileName(pair.VideoPath),
            ImageSize = FileSizeFormatter.Format(pair.ImageSizeBytes), VideoSize = FileSizeFormatter.Format(pair.VideoSizeBytes),
            ImageSizeBytes = pair.ImageSizeBytes, VideoSizeBytes = pair.VideoSizeBytes,
            TotalSizeBytes = pair.ImageSizeBytes + pair.VideoSizeBytes, DateTaken = GetDateTaken(pair.ImagePath),
            BaseName = pair.BaseName, ImagePath = pair.ImagePath, VideoPath = pair.VideoPath,
            Protocol = pair.Protocol, DetectionMethod = pair.DetectionMethod, PairingKind = pair.PairingKind, PairingIdentifier = pair.PairingIdentifier,
            MotionVideoByteOffset = pair.VideoByteOffset, MotionVideoByteLength = pair.VideoByteLength, MotionVideoContainer = pair.VideoContainer,
            Status = ProcessStatus.Pending, Details = ResourceService.GetString("Task_Pending")
        };

        #endregion

        #region Helpers

        // 读取图片的 EXIF 拍摄日期（DateTimeOriginal），读不到时降级为文件修改时间。
        // FastMetadataReader 直接从 JPEG 文件头读 EXIF 标签，< 1ms；HEIC 等返回 null。
        private static DateTime GetDateTaken(string imagePath)
        {
            var (_, _, exifDate) = FastMetadataReader.Read(imagePath);
            if (exifDate is not null &&
                DateTime.TryParseExact(exifDate, "yyyy:MM:dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return File.GetLastWriteTime(imagePath);
        }

        #endregion

        #region Secondary / Toggle Commands

        // 切换次要操作：未处理时清除状态，处理中则切换暂停/继续。
        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            if (IsScanning) { CancelScanning(); return; }
            if (!IsProcessing)
            {
                ClearState();
            }
            else
            {
                TogglePause();
            }
        }

        // 切换合并处理状态：开始合并或停止合并。
        // 停止时显示已取消对话框，完成后显示完成对话框。
        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ToggleProcessAsync()
        {
            if (ApplicationShutdown.IsClosing) return;
            if (IsScanning) return;
            if (IsProcessing)
            {
                SetStatus("Status_Stopping");
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_mergeStoppedByUser || _mergeDone)
            {
                if (_mergeStoppedByUser)
                    await ShowMergeCancelledDialogAsync();
                else
                    await ShowMergeAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Merge");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                await ShowNoOutputDirectoryDialogAsync("Merge");
                return;
            }

            // 检查其他页面是否正在处理中（优先级最低，在所有其他验证之后）
            if (AppViewModel.Instance.IsAnyWorkPageProcessing)
            {
                await ShowOtherPageProcessingDialogAsync();
                return;
            }

            // 开始合成前：强制归位排序和筛选到默认值
            // 确保用户能从上到下看到有序的执行进度
            _sortIndex = 0;
            OnPropertyChanged(nameof(SortIndex));
            _sortDescending = false;
            OnPropertyChanged(nameof(SortDescending));
            OnPropertyChanged(nameof(SortDirectionGlyph));
            _filterStatus = null;
            OnPropertyChanged(nameof(FilterStatus));
            _searchFilterText = string.Empty;
            OnPropertyChanged(nameof(SearchFilterText));
            RefreshTaskView();

            IsDirectoryPanelOpen = false;
            try
            {
                await RunTasksAsync();
            }
            catch (RebuiltPipelineNotReadyException exception)
            {
                await ProcessingNotReadyDialogService.ShowAsync(exception.Operation);
            }
        }

        #endregion

        #region Result Dialogs

        // 显示合并已完成对话框，可打开输出文件夹。
        private async Task ShowMergeAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);

                var stack = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_MergeCompletedTitle"),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_MergeCompletedSummary", total, succeeded, failed) + ProductOutcomePresentation.WarningSummary(WarningCount),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_MergeCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var chosenPrimary = await DialogService.ShowDualAsync(
                    App.MainWindow.Content.XamlRoot,
                    title: null,
                    content: stack,
                    primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (chosenPrimary)
                    await OpenMergeOutputFolderAsync();
            }
        }

        // 显示合并已被用户取消的结果对话框，汇总成功/失败/未处理数量。
        private async Task ShowMergeCancelledDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed - WarningCount;

                var stack = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_TaskCancelledTitle"),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_MergeCancelledSummary", total, succeeded, failed, unprocessed) + ProductOutcomePresentation.WarningSummary(WarningCount),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_MergeCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var chosenPrimary = await DialogService.ShowDualAsync(
                    App.MainWindow.Content.XamlRoot,
                    title: null,
                    content: stack,
                    primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (chosenPrimary)
                    await OpenMergeOutputFolderAsync();
            }
        }


        #endregion

        #region Task Execution

        // 执行所有合并任务的异步核心方法。
        // 处理流程：初始化状态 → 创建输出/临时目录 → 按并发限制并行处理任务 →
        // 对 HEIC 图片转码 JPEG、视频转码 MP4、协议预处理 → 写入实况照片文件 →
        // 清理临时文件 → 显示结果对话框。
        private Task RunTasksAsync()
        {
            return RunRebuiltTasksAsync();
        }

        private async Task RunRebuiltTasksAsync()
        {
            OutputProfile outputProfile;
            try
            {
                outputProfile = ProductSelectionAdapter.Merge(SelectedModeIndex, OutputFormatIndex);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                SetStatus("Status_Error", ex.Message);
                IsDirectoryPanelOpen = true;
                return;
            }
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();
            OnPropertyChanged(nameof(ElapsedTimeText));

            var token = GetProcessingToken();
            string outputDir = OutputDirectory;
            int sourceAction = AfterCompletionActionIndex;
            string sourceArchive = OriginalDirectory;
            var succeededThisRun = new System.Collections.Concurrent.ConcurrentBag<MergeTask>();
            bool operationFailed = false;
            int alreadySucceeded = Tasks.Count(task => task.Status is ProcessStatus.Success or ProcessStatus.Warning);
            _completedTasksCount = alreadySucceeded;
            string capabilityDiagnostic = ProductOutcomePresentation.CapabilityDiagnostic(() => ProductCapabilityQuery.Query(outputProfile));

            try
            {
                Directory.CreateDirectory(outputDir);
                var tasksToProcess = Tasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning)).ToList();
                bool hasHeic = tasksToProcess.Any(t => HeicConverterService.IsHeicFile(t.ImagePath));
                int maxParallel = hasHeic
                    ? AppSettingsService.GetValue("MergeThreadCount", 4)
                    : 20;

                var options = new LivePhotoMergeRunOptions
                {
                    OutputDirectory = outputDir,
                    OutputProfile = outputProfile,
                    StructuredOptions = new()
                    {
                        SamsungVariant = SamsungVersionIndex == 0 ? SamsungTargetVariant.Mpv2 : SamsungTargetVariant.Mpv3,
                        HuaweiVariant = HuaweiVersionIndex == 0 ? HuaweiTargetVariant.HuaweiV6 : HuaweiTargetVariant.HonorV2
                    },
                    NamingRuleIndex = NamingRuleIndex,
                    CustomNamingPattern = CustomNamingPattern,
                    MaxDegreeOfParallelism = maxParallel,
                    OverwriteExisting = OverwriteExisting,
                    PreserveSubfolders = AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false),
                    InputDirectory = InputDirectory,
                };

                var runTasks = Tasks.ToArray();
                await Task.Run(() => LivePhotoMergeRunnerService.RunWithOutcomesAsync(
                    runTasks,
                    options,
                    PauseEvent,
                    token,
                    task => ApplyRebuiltMergeTaskStarted(task),
                    (task, outcome, completedCount) =>
                    {
                        ApplyRebuiltMergeTaskCompleted(task, outcome, alreadySucceeded + completedCount, capabilityDiagnostic);
                        if (outcome.IsSuccess && task is MergeTask completedTask) succeededThisRun.Add(completedTask);
                    }), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                foreach (var task in Tasks.Where(task => task.Status == ProcessStatus.Processing))
                    UpdateTaskCancelled(task, ResourceService.GetString("Task_Cancelled"));
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed - WarningCount;
                double elapsed = _stopwatch.Elapsed.TotalSeconds;
                LogService.Merge($"Processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalPairsCount}");
                SetStatus("Status_MergeStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                operationFailed = _mergeOperationFailed = true;
                LogService.Merge($"Batch input/output protection failed; processing stopped: {ex}", LogLevel.Error, ex);
                ScanDiagnostics.Add(ScanDiagnosticDisplayItem.FromDiagnostic(
                    LivePhotoScanSupport.Failure(InputDirectory, ex, "BatchSafetyFailed", stage: ScanDiagnosticStage.Read)));
                SetStatus("ScanDiagnostic_BatchSafetyFailed");
            }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                operationFailed = _mergeOperationFailed = true;
                ReportImportFailure(ex, InputDirectory);
            }
            finally
            {
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;

                if (!wasCancelled && !operationFailed)
                {
                    if (sourceAction is 1 or 2)
                        foreach (var task in succeededThisRun)
                        {
                            if (token.IsCancellationRequested) break;
                            try
                            {
                                var outcome = task.LastOutcome ?? throw new IOException("Missing completion receipt.");
                                var paths = new[] { task.ImagePath, task.VideoPath }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                                string? moveDir = sourceAction == 1 ? sourceArchive : null;
                                if (sourceAction == 1 && string.IsNullOrWhiteSpace(moveDir))
                                    throw new IOException("Archive directory is missing; originals retained.");
                                var guard = outcome.SourceActionGuard ?? throw new IOException("Source action identity receipt is unavailable; originals retained.");
                                using var committedProtection = new SourceInputProtection(outcome.CommittedOutputs.Select(o => o.Path));
                                guard.Validate(outcome, paths, moveDir);
                                if (ApplicationShutdown.IsClosing) break;
                                using var sourceActionJournal = new LivePhotoBox.Interop.WindowsSplitOutputPublisher.SourceActionJournal(paths, moveDir);
                                if (moveDir is not null) Directory.CreateDirectory(moveDir);
                                foreach (string path in paths)
                                {
                                    token.ThrowIfCancellationRequested();
                                    guard.ValidateSource(outcome, path);
                                    if (moveDir is not null) File.Move(path, Path.Combine(moveDir, Path.GetFileName(path)));
                                    else await MoveFileToRecycleBinAsync(path, outcome);
                                }
                                sourceActionJournal.Complete();
                                task.LastOutcome = ProductOutcomeCompletion.Record(outcome with
                                { SourcePostProcessing = new(moveDir is null ? "recycle" : "move", "completed", "", moveDir) });
                                task.OutcomeSummary = ProductOutcomePresentation.Describe(task.LastOutcome, capabilityDiagnostic);
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                if (task.LastOutcome is { } completed)
                                {
                                    task.LastOutcome = ProductOutcomeCompletion.Record(completed with
                                    { SourcePostProcessing = new(sourceAction == 1 ? "move" : "recycle", "cancelled", "Cancelled by user.",
                                        sourceAction == 1 ? sourceArchive : null) });
                                    task.OutcomeSummary = ProductOutcomePresentation.Describe(task.LastOutcome, capabilityDiagnostic);
                                }
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (task.LastOutcome is { } completed)
                                    task.LastOutcome = ProductOutcomeCompletion.Record(completed with
                                    { SourcePostProcessing = new(sourceAction == 1 ? "move" : "recycle", "failed", ex.Message,
                                        sourceAction == 1 ? sourceArchive : null) });
                                task.OutcomeSummary += Environment.NewLine + ResourceService.GetString("ProductWarning_SourceAction") + ": " + ex.Message;
                                task.Details = ResourceService.GetString("ProductWarning_SourceAction");
                                task.Status = ProcessStatus.Warning;
                                NotifyStatsChanged();
                                LogService.Merge("Source post-processing failed: " + ex.Message, LogLevel.Warning, ex);
                            }
                        }
                }

                wasCancelled = _cancelledByUser;
                FinalizeRunState();

                if (Tasks.Count > 0 && !_isCleaningUp && !operationFailed)
                {
                    try
                    {
                        if (wasCancelled)
                            await ShowMergeCancelledDialogAsync();
                        else
                            await ShowMergeAlreadyDoneDialogAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        LogService.Debug($"Completion dialog skipped (another dialog already open): {ex.Message}", LogSource.UI);
                    }
                }
            }
        }

        private void ApplyRebuiltMergeTaskStarted(IMergeTaskInfo task)
        {
            if (task is not MergeTask mergeTask) return;
            RunRebuiltMergeUiUpdate(() => UpdateTaskStarted(mergeTask));
        }

        private void ApplyRebuiltMergeTaskCompleted(
            IMergeTaskInfo task, ProductOperationOutcome outcome, int completedCount, string capabilityDiagnostic)
        {
            if (task is not MergeTask mergeTask) return;
            RunRebuiltMergeUiUpdate(() =>
            {
                mergeTask.LastOutcome = outcome;
                mergeTask.OutcomeSummary = ProductOutcomePresentation.Describe(outcome, capabilityDiagnostic);
                mergeTask.OutputPath = outcome.HasUsableOutputs ? outcome.CommittedOutputs.FirstOrDefault()?.Path ?? string.Empty : string.Empty;
                string details = ProductOutcomePresentation.Status(outcome);
                if (outcome.Status == ProductOperationStatus.Cancelled)
                    UpdateTaskCancelled(mergeTask, details);
                else
                {
                    _completedTasksCount = Math.Max(_completedTasksCount, completedCount);
                    UpdateTaskCompleted(mergeTask, outcome.IsSuccess, details, _completedTasksCount);
                }
            });
        }

        private static void RunRebuiltMergeUiUpdate(Action update)
        {
            if (ApplicationShutdown.IsClosing) return;
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                update();
                return;
            }

            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try { if (!ApplicationShutdown.IsClosing) update(); }
                finally { completed.TrySetResult(true); }
            }))
                return;

            completed.Task.GetAwaiter().GetResult();
        }



        private static Task MoveFileToRecycleBinAsync(string path, ProductOperationOutcome outcome) =>
            Task.Run(() => SourceRecycleBinService.Recycle(outcome, path));

        #endregion

        #region Task Status Events

        // 当某个合并任务开始时触发，可用于自动滚动到当前处理的任务。
        public event EventHandler<MergeTask>? TaskStartedForScroll;

        // 当所有合并任务处理完毕（全部完成或停止）时触发，可用于滚动到列表顶部。
        public event EventHandler? ProcessingCompletedForScroll;

        // 标记任务开始处理（设置为 Processing 状态）。
        private void UpdateTaskStarted(MergeTask task)
        {
            task.OutputPath = string.Empty;
            task.LastOutcome = null;
            task.OutcomeSummary = string.Empty;
            task.IsCancelled = false;
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("MergePage_Task_Processing");
            NotifyStatsChanged();
            TaskStartedForScroll?.Invoke(this, task);
        }

        // 标记任务被用户取消：保留 Processing 状态显示（颜色中性），
        // 通过 IsCancelled 标记计入底部"已取消"统计；不标为 Failed，避免显示错误语义。
        private void UpdateTaskCancelled(MergeTask task, string detailMessage)
        {
            task.IsCancelled = true;
            task.Status = ProcessStatus.Cancelled;
            task.Details = detailMessage;
            NotifyStatsChanged();
        }

        // 更新任务完成状态（成功/失败），如果所有任务完成则触发 ProcessingCompletedForScroll 事件。
        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = task.LastOutcome?.Status == ProductOperationStatus.Warning ? ProcessStatus.Warning : isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
            NotifyStatsChanged();

            if (completedCount >= Tasks.Count && Tasks.Count > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        #endregion

        #region Folder Commands

        // 在文件资源管理器中打开输入文件夹。
        private async Task OpenMergeInputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(InputDirectory)) return;
                if (!Directory.Exists(InputDirectory))
                {
                    await ShowInvalidInputDirectoryDialogAsync();
                    return;
                }
                FilePickerService.OpenFolderInExplorer(InputDirectory);
            }
            catch (Exception ex) { LogService.Merge($"OpenMergeInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 在文件资源管理器中打开输出文件夹（不存在则自动创建）。
        private async Task OpenMergeOutputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { LogService.Merge($"OpenMergeOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 在文件资源管理器中打开原始文件存放目录（CanExecute 保证路径合法）。
        private Task OpenMergeOriginalDirAsync()
        {
            try
            {
                string path = OriginalDirectory;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);
                    FilePickerService.OpenFolderInExplorer(path);
                }
            }
            catch (Exception ex) { LogService.Merge($"OpenMergeOriginalDir error: {ex.Message}", LogLevel.Error, ex); }
            return Task.FromException(new IOException("Recycle bin UI dispatcher is unavailable; originals retained."));
        }

        #endregion
    }
}
