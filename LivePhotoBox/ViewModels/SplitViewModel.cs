/*
 * SplitViewModel.cs
 *
 * SplitPage（拆分页面）的视图模型，负责将单文件实况照片拆分为图片与视频。
 *
 *   - 扫描输入目录中的单文件实况照片（JPEG）
 *   - 选择输出协议（无协议/Apple/vivo）与输出格式
 *   - 将图片与视频分别写入输出目录并统计耗时
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Collections;
using System.Collections.ObjectModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class SplitViewModel : WorkViewModelBase
    {
        private MergeScanOptions? _lastScanScope;
        // 用于统计拆分耗时的计时器。
        private Stopwatch _stopwatch = new();

        // 拆分是否被用户手动停止。
        private bool _splitStoppedByUser;

        // 所有拆分任务是否已完成（成功/失败都算）。
        private bool _splitDone;
                private bool _splitOperationFailed;

        // 扫描进度本地计数器（基类的 _scanTotal/_scanProcessed 是 private，子类不可访问）。
        private int _splitLocalScanTotal;
        private int _splitLocalScanProcessed;

        // UI 更新计时器（约 60ms 间隔），用于在拆分过程中刷新进度条和进度文本。
        private readonly DispatcherTimer _uiUpdateTimer;

        // 当前已完成的拆分任务数（线程安全，使用 volatile）。
        private volatile int _completedTasksCount;

        // 原始文件移动目录是否已由用户手动设置（系统自动填充时不覆盖用户手动填写的值）。
        private bool _originalDirectoryUserSet;

        // 返回空字符串以隐藏全局 PageStatusBar，SplitPage 使用自己的底部状态栏。
        public override string PageStatusTag => string.Empty;

        // <inheritdoc/>
        protected override string ProcessingStatusKey => "SplitPage_Status_Running";

        // Actual backend is reported by the terminal outcome, not hardware discovery.
        protected override string ProcessingStatusText =>
            ResourceService.Format("SplitPage_Status_Running");

        #region Observable Properties

        // 输入文件夹路径（用户选择的待扫描目录）。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openSplitInputFolderCommand?.NotifyCanExecuteChanged();
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && !IsProcessing)
                _ = ScanChangedDirectoryAsync(value);

        }

        private async Task ScanChangedDirectoryAsync(string directory)
        {
            try
            {
                OutputDirectory = Path.Combine(directory, ResourceService.GetString("OutputDir_SplitPhotos"));
                AutoFillOriginalDirectory();
                await ScanInputsAsync(new[] { directory }, Array.Empty<string>(), replace: true, clearExisting: true);
            }
            catch (Exception error) when (LivePhotoScanSupport.CanReportOperationFailure(error))
            {
                ReportImportFailure(error, directory);
            }
        }

        // 输出文件夹路径（拆分后的照片/视频存放目录）。默认在输入目录下创建子目录。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openSplitOutputFolderCommand?.NotifyCanExecuteChanged();

        // 扫描到的有效单文件实况照片总数。
        [ObservableProperty]
        private int _totalCount = 0;

        // 扫描识别的实况照片文件数（与 TotalCount 一致，语义上供统计展示）。
        [ObservableProperty]
        private int _recognizedCount = 0;

        // 扫描中跳过的文件数（非实况照片格式等）。
        [ObservableProperty]
        private int _skippedCount = 0;

        // 扫描诊断条目集合（独立于 Tasks，不可被 Runner 执行）
        public BulkObservableCollection<ScanDiagnosticDisplayItem> ScanDiagnostics { get; } = [];

        private CollectionViewUpdater<ScanDiagnosticDisplayItem>? _diagnosticsUpdater;

        private void ClearVisibleScanLists()
        {
            if (_displayUpdater != null) _displayUpdater.ClearImmediately();
            else DisplayTasks.Clear();
            if (_diagnosticsUpdater != null) _diagnosticsUpdater.ClearImmediately();
            else ScanDiagnostics.Clear();
        }


        private void UpdateScanDiagnostics(IEnumerable<ScanDiagnosticDisplayItem> items)
        {
            var existing = ScanDiagnostics.ToLookup(d => (d.PrimaryPath, d.DiagnosticCode, d.GroupKey));
            var snapshot = items.Select(row => existing[(row.PrimaryPath, row.DiagnosticCode, row.GroupKey)]
                .FirstOrDefault(old => old.HasSameContent(row)) ?? row).ToArray();
            _diagnosticsUpdater ??= new(ScanDiagnostics, action =>
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) { action(); return true; }
                return dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action());
            });
            _diagnosticsUpdater.Request(() => snapshot, publishAtomically: IsScanning);
        }


        public int ScanDiagnosticsCount => ScanDiagnostics.Count;
        public bool HasScanDiagnostics => ScanDiagnostics.Count > 0;

        // 目录选择面板是否展开显示。
        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        // 拆分进度百分比（0~100）。
        [ObservableProperty]
        private double _splitProgress = 0;

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
            ? ResourceService.Format("SplitPage_StatusProcessing", ProcessingCount)
            : TotalCount > 0
                ? ResourceService.Format("SplitPage_StatusTotal", TotalCount)
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
                    return ResourceService.Format("SplitPage_StatusElapsed", _stopwatch.Elapsed.ToString(@"mm\:ss"));
                // 扫描中或扫描结束但尚未开始处理：显示扫描用时（扫描结束后冻结）
                if (IsScanning || ScanStopwatch.Elapsed.TotalSeconds > 0)
                    return ResourceService.Format("SplitPage_StatusScanElapsed", ScanStopwatch.Elapsed.ToString(@"mm\:ss"));
                return ResourceService.GetString("SplitPage_StatusElapsedIdle");
            }
        }

        // ── 底部栏统一属性 ──

        /// <summary>底部栏进度条数值（0~100），综合扫描和处理进度。</summary>
        public double FooterProgressValue
        {
            get
            {
                if (IsScanning)
                    return _splitLocalScanTotal > 0 ? (_splitLocalScanProcessed * 100.0 / _splitLocalScanTotal) : 0;
                if (_splitLocalScanTotal > 0 && !IsProcessing && !_splitDone)
                    return 100.0; // 扫描刚完成，进度条滚到头
                return SplitProgress;
            }
        }

        /// <summary>
        /// 底部栏左侧状态文字，覆盖所有生命周期：
        /// 空闲 → Status；扫描中 → 扫描进度；处理中 → ProcessingStatusText；
        /// 暂停 → "已暂停"；停止 → "已停止"；完成 → "处理完成"。
        /// </summary>
        public string FooterStatusText
        {
            get
            {
                if (IsScanning)
                {
                    // 扫描取消中：优先显示"正在停止扫描..."
                    if (_scanCancelledByUser)
                        return ResourceService.GetString("SplitPage_Status_ScanCancelling");
                    return ScanProgressStatusText;
                }
                if (IsProcessing)
                {
                    // 停止中（用户点了停止，等待任务中断）
                    if (_cancelledByUser)
                        return ResourceService.GetString("Status_Stopping");
                    // 已暂停（暂停完成，等待用户点击继续）
                    if (IsPaused)
                        return ResourceService.GetString("Status_Paused");
                    // 暂停中（等待当前任务结束再进入暂停）
                    if (_isPausing)
                        return ResourceService.GetString("Status_Pausing");
                    return ProcessingStatusText;
                }
                if (_splitStoppedByUser)
                    return ResourceService.GetString("Status_StoppedSimple");
                if (_splitDone && Progress >= 100)
                    return ResourceService.GetString("Status_DoneSimple") + ProductOutcomePresentation.WarningSummary(WarningCount);
                // Idle / Ready — 使用 SetStatus 设置的文字
                return Status;
            }
        }

        // ── 统计项可见性（零值自动隐藏） ──

        public Visibility FooterTotalVisible => TotalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterSuccessVisible => SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterWarningVisible => WarningCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterFailedVisible => FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterUnprocessedVisible => (!_splitStoppedByUser && PendingCount > 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterCancelledVisible => (_splitStoppedByUser && CancelledCount > 0) ? Visibility.Visible : Visibility.Collapsed;

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
            ? ResourceService.GetString("SplitPage_DynamicCancelText")
            : ResourceService.GetString("SplitPage_DynamicScanText");

        // 拆分任务列表（Observable 集合，支持高性能批量更新）。
        public BulkObservableCollection<SplitTask> Tasks { get; } = [];

        // 当前选中的输出协议索引（0=无协议 / 1=Apple / 2=vivo），持久化存储到设置中。
        public int ProtocolIndex
        {
            get => AppSettingsService.GetValue("SplitProtocolIndex", 0);
            set
            {
                AppSettingsService.SetValue("SplitProtocolIndex", value);
                LogService.Split($"Split protocol changed to index: {value}");
                RefreshAvailableOutputFormats(value);
                OnPropertyChanged();
            }
        }

        // 匹配方式索引：0=所有实况照片协议 / 1=Fusion / 2=MicroVideo V1 / 3=MotionPhoto V2
        //                / 4=OPPO O-Live Photo / 5=vivo Live Photo / 6=Samsung Motion Photo / 7=HUAWEI Moving Photo / 8=Apple双文件 / 9=同名双文件候选（仍要求源实况协议证明）。
        // 扫描时按此协议过滤单文件实况照片（复用 Core 的 LivePhotoProtocolDetector）。
        public int MatchProtocolIndex
        {
            get
            {
                int value = ScanSettingsMigration.SplitChoice();
                // Fusion 已隐藏（index 1），若历史设置仍为 1，则回退到“所有单文件”。
                return value == 1 || value is < 0 or > 7 ? 0 : value;
            }
            set
            {
                if (value is < 0 or > 7) return;
                AppSettingsService.SetValue("SplitMatchProtocolIndex", value);
                LogService.Split($"Split match protocol changed to index: {value}");
                OnPropertyChanged();
            }
        }

        // 匹配方式索引 → 目标协议类型（null 表示"所有实况照片"，不过滤）。
        private LivePhotoProtocolType? MatchProtocolType => MatchProtocolIndex switch
        {
            1 => LivePhotoProtocolType.Fusion,
            2 => LivePhotoProtocolType.GoogleV1,
            3 => LivePhotoProtocolType.GoogleV2,
            4 => LivePhotoProtocolType.OPPO,
            5 => LivePhotoProtocolType.Vivo,
            6 => LivePhotoProtocolType.Samsung,
            7 => LivePhotoProtocolType.Huawei,
            8 => LivePhotoProtocolType.Apple,
            _ => null
        };

        private static LivePhotoProtocolType MapNativeProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.GoogleMicroVideoV1 => LivePhotoProtocolType.GoogleV1,
            SourceProtocol.GoogleMotionPhotoV2 => LivePhotoProtocolType.GoogleV2,
            SourceProtocol.OppoLivePhoto => LivePhotoProtocolType.OPPO,
            SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => LivePhotoProtocolType.Vivo,
            SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => LivePhotoProtocolType.Samsung,
            SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => LivePhotoProtocolType.Huawei,
            _ => LivePhotoProtocolType.Unknown
        };

        // ── 搜索 / 排序 / 筛选 ──

        [ObservableProperty]
        private string _searchFilterText = string.Empty;

        // 排序：0=文件名，1=大小，2=拍摄日期
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

        partial void OnSearchFilterTextChanged(string value) => RefreshTaskView();
        partial void OnSortIndexChanged(int value) => RefreshTaskView();
        partial void OnFilterStatusChanged(ProcessStatus? value) => RefreshTaskView();

        public BulkObservableCollection<SplitTask> DisplayTasks { get; } = [];

        private CollectionViewUpdater<SplitTask>? _displayUpdater;

        private async Task AwaitDisplayRefreshAsync()
        {
            Task? current;
            Task? diagnostics;
            do
            {
                current = _displayUpdater?.Completion;
                diagnostics = _diagnosticsUpdater?.Completion;
                if (current != null) await current.ConfigureAwait(true);
                if (diagnostics != null) await diagnostics.ConfigureAwait(true);
            } while (current != _displayUpdater?.Completion || diagnostics != _diagnosticsUpdater?.Completion);
        }

        private void RefreshTaskView(bool final = false)
        {
            _displayUpdater ??= new(DisplayTasks, action =>
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) { action(); return true; }
                return dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action());
            });
            _displayUpdater.Request(() => BuildTaskView(sort: final || !IsScanning), publishAtomically: IsScanning);
        }

        private IReadOnlyList<SplitTask> BuildTaskView(bool sort)
        {
            // 提前物化源数据，避免 LINQ 延迟执行与 ReplaceRange Clear() 的竞态
            var source = Tasks.ToList();

            IEnumerable<SplitTask> query = source;

            if (!string.IsNullOrWhiteSpace(SearchFilterText))
            {
                var s = SearchFilterText.Trim();
                query = query.Where(t =>
                    t.SourceFileName.Contains(s, StringComparison.OrdinalIgnoreCase));
            }

            if (FilterStatus.HasValue)
                query = query.Where(t => t.Status == FilterStatus.Value);

            query = !sort ? query : SortIndex switch
            {
                0 => SortDescending
                    ? query.OrderByDescending(t => t.SourceFileName, StringComparer.OrdinalIgnoreCase)
                    : query.OrderBy(t => t.SourceFileName, StringComparer.OrdinalIgnoreCase),
                1 => SortDescending
                    ? query.OrderByDescending(t => t.FileSizeBytes)
                    : query.OrderBy(t => t.FileSizeBytes),
                2 => SortDescending
                    ? query.OrderByDescending(t => t.DateTaken)
                    : query.OrderBy(t => t.DateTaken),
                _ => query
            };

            return query.ToList();
        }

        private IAsyncRelayCommand? _openSplitInputFolderCommand;
        private IAsyncRelayCommand? _openSplitOutputFolderCommand;
        private IAsyncRelayCommand? _openSplitOriginalDirCommand;

        // 在文件资源管理器中打开输入文件夹的命令（仅路径存在时启用）。
        public IAsyncRelayCommand OpenSplitInputFolderCommand => _openSplitInputFolderCommand ??= new AsyncRelayCommand(OpenSplitInputFolderAsync, () => DirectoryHelper.CanOpenFolder(InputDirectory));

        // 在文件资源管理器中打开输出文件夹的命令（非空即可，打开时自动建目录）。
        public IAsyncRelayCommand OpenSplitOutputFolderCommand => _openSplitOutputFolderCommand ??= new AsyncRelayCommand(OpenSplitOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        // 在文件资源管理器中打开原始文件存放目录的命令（非空即可，打开时自动建目录）。
        public IAsyncRelayCommand OpenSplitOriginalDirCommand => _openSplitOriginalDirCommand ??= new AsyncRelayCommand(OpenSplitOriginalDirAsync, () => !string.IsNullOrWhiteSpace(OriginalDirectory));

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

        // 输出格式索引（全局：0=默认原样 / 1=JPG+MOV(H265) / 2=HEIC+MOV(H265) / 3=JPG+MP4(H264)）。
        public int OutputFormatIndex
        {
            get => AppSettingsService.GetValue("SplitOutputFormatIndex", 0);
            set
            {
                AppSettingsService.SetValue("SplitOutputFormatIndex", value);
                LogService.Split($"Output format changed to index: {value}");
                if (SelectedOutputFormat?.Id != value)
                {
                    SelectedOutputFormat = AvailableOutputFormats.FirstOrDefault(f => f.Id == value);
                }
                OnPropertyChanged();
            }
        }

        public void RefreshAvailableOutputFormats(int protocolIndex)
        {
            int[] available = ProtocolFormatMatrix.GetAvailableSplitFormats(protocolIndex);
            AvailableOutputFormats.Clear();
            foreach (int formatIndex in available)
            {
                AvailableOutputFormats.Add(CreateSplitFormatOption(formatIndex));
            }

            int targetFormat = available.Contains(OutputFormatIndex)
                ? OutputFormatIndex
                : ProtocolFormatMatrix.GetDefaultSplitFormat(protocolIndex);

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

        private static OutputFormatOptionItem CreateSplitFormatOption(int formatIndex)
        {
            string nameKey = formatIndex switch
            {
                ProtocolFormatMatrix.SplitFormatKeep => "SplitPage_OutputFormat_Default.Content",
                ProtocolFormatMatrix.SplitFormatJpgMov => "SplitPage_OutputFormat_JpgMov.Content",
                ProtocolFormatMatrix.SplitFormatHeicMov => "SplitPage_OutputFormat_HeicMov.Content",
                ProtocolFormatMatrix.SplitFormatJpgMp4 => "SplitPage_OutputFormat_JpgMp4.Content",
                _ => string.Empty
            };

            string hintKey = formatIndex switch
            {
                ProtocolFormatMatrix.SplitFormatKeep => "SplitPage_FormatHint_Default",
                ProtocolFormatMatrix.SplitFormatJpgMov => "SplitPage_FormatHint_JpgMov",
                ProtocolFormatMatrix.SplitFormatHeicMov => "SplitPage_FormatHint_HeicMov",
                ProtocolFormatMatrix.SplitFormatJpgMp4 => "SplitPage_FormatHint_JpgMp4",
                _ => string.Empty
            };

            string fallbackName = formatIndex switch
            {
                ProtocolFormatMatrix.SplitFormatKeep => "默认（原样拆分）",
                ProtocolFormatMatrix.SplitFormatJpgMov => "JPG + MOV (H.265)",
                ProtocolFormatMatrix.SplitFormatHeicMov => "HEIC + MOV (H.265)",
                ProtocolFormatMatrix.SplitFormatJpgMp4 => "JPG + MP4 (H.264)",
                _ => string.Empty
            };

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
            get => AppSettingsService.GetValue("SplitCustomNamingPattern", "{name}");
            set
            {
                AppSettingsService.SetValue("SplitCustomNamingPattern", value);
                OnPropertyChanged();
                RefreshNamingPreview();
            }
        }

        // ── 分段分隔符 ──

        // 命名片段之间的分隔符（_ / - / 空格 / + / 无）。
        public int NamingSeparatorIndex
        {
            get => AppSettingsService.GetValue("SplitNamingSeparatorIndex", 0);
            set
            {
                AppSettingsService.SetValue("SplitNamingSeparatorIndex", value);
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

        // 命名预览文本（取占位名渲染模板）。
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
            AppSettingsService.SetValue("SplitCustomNamingPattern", template);
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
                    CustomNamingPattern, sampleBaseName, ProtocolIndex, 1);
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
            get => AppSettingsService.GetValue("SplitOverwriteExisting", false);
            set
            {
                AppSettingsService.SetValue("SplitOverwriteExisting", value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(OverwriteStatusText));
            }
        }

        // 覆盖开关右侧的状态文字（"开"/"关"）。
        public string OverwriteStatusText => OverwriteExisting
            ? ResourceService.GetString("SplitPage_ToggleOn")
            : ResourceService.GetString("SplitPage_ToggleOff");

        // 完成后操作索引（0=无操作, 1=移动到指定目录, 2=回收站）。
        public int AfterCompletionActionIndex
        {
            get => AppSettingsService.GetValue("SplitAfterCompletionActionIndex", 0);
            set
            {
                AppSettingsService.SetValue("SplitAfterCompletionActionIndex", value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOriginalDirSectionVisible));
            }
        }

        // 完成后操作选中"移动到指定目录"时显示原始文件目录选择区域。
        public bool IsOriginalDirSectionVisible => AfterCompletionActionIndex == 1;

        // 原始文件移动目标目录。
        public string OriginalDirectory
        {
            get => AppSettingsService.GetValue("SplitOriginalDirectory", string.Empty);
            set
            {
                AppSettingsService.SetValue("SplitOriginalDirectory", value);
                OnPropertyChanged();
                _openSplitOriginalDirCommand?.NotifyCanExecuteChanged();
            }
        }

        // 标记用户已手动设置原始文件移动目录（后续自动填充不再覆盖）。
        public void MarkOriginalDirectoryUserSet() => _originalDirectoryUserSet = true;

        // 自动填充原始文件移动目录，仅在用户未手动设置过时生效。
        public void AutoFillOriginalDirectory()
        {
            if (_originalDirectoryUserSet) return;
            if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
            OriginalDirectory = Path.Combine(OutputDirectory, ResourceService.GetString("OriginalDir_SubfolderName"));
        }

        #endregion

        #region Constructor

        public SplitViewModel()
        {
            SetStatus("SplitPage_Status_Ready");
            RefreshAvailableOutputFormats(ProtocolIndex);
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            ScanDiagnostics.CollectionChanged += (_, change) =>
            {
                int first = Math.Max(0, change.NewStartingIndex >= 0 ? change.NewStartingIndex : change.OldStartingIndex);
                int end = change.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
                    ? Math.Min(ScanDiagnostics.Count, first + (change.NewItems?.Count ?? 0)) : ScanDiagnostics.Count;
                for (int i = first; i < end; i++) ScanDiagnostics[i].Index = i + 1;
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
                if (!IsProcessing && !IsScanning && change.NewItems is not null &&
                    change.NewItems.OfType<SplitTask>().Any(task => task.Status == ProcessStatus.Pending))
                {
                    _splitDone = false;
                    _splitStoppedByUser = false;
                    SetStatus("SplitPage_Status_Ready");
                    OnPropertyChanged(nameof(ActionBtnText));
                    NotifyStatsChanged();
                }
                RefreshTaskView();
                UpdateIsQueueEmpty(Tasks.Count + ScanDiagnostics.Count);
            };

            // 响应全局设置清除：刷新所有持久化属性 + 清空命名片段
            AppSettingsService.SettingsCleared += () =>
            {
                OnPropertyChanged(nameof(ProtocolIndex));
                RefreshAvailableOutputFormats(ProtocolIndex);
                OnPropertyChanged(nameof(MatchProtocolIndex));
                OnPropertyChanged(nameof(OutputFormatIndex));
                OnPropertyChanged(nameof(NamingRuleIndex));
                OnPropertyChanged(nameof(CustomNamingPattern));
                OnPropertyChanged(nameof(NamingSeparatorIndex));
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

        // 主操作按钮（开始/停止拆分）的文本。
        public override string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartSplit");
            }
        }

        // 主操作按钮图标：开始▶ / 停止■
        public string ActionBtnGlyph => IsProcessing
            ? ""                           // Stop ■
            : "";                          // Play ▶

        // 当前是否允许开始处理（扫描中不允许）。
        public override bool IsProcessingAllowed => !IsScanning && Tasks.Count > 0;

        // 当前是否可以编辑拆分配置（仅处理中不可编辑，扫描时允许边扫边配）。
        public bool CanEditSelectedMode => !IsProcessing;

        // 拆分页输出目录：仅处理中锁定，扫描中可切换输出目录（扫描只影响输入目录）。
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
            // 扫描结束（true→false）后要从扫描进度文案切换到扫描完成文案。
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterProgressValue));
        }

        // <inheritdoc/>
        protected override void OnBeginScanSession()
        {
            _splitLocalScanTotal = 0;
            _splitLocalScanProcessed = 0;
            AppViewModel.Instance.BeginSplitScanSession();
            OnPropertyChanged(nameof(ElapsedTimeText));
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            _splitLocalScanTotal = snapshot.Total;
            _splitLocalScanProcessed = snapshot.Completed;
            AppViewModel.Instance.ApplySplitScanProgress(snapshot);
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnCompleteScanSnapshot()
        {
            _splitLocalScanProcessed = _splitLocalScanTotal;
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        // <inheritdoc/>
        protected override void OnInitializeRunState()
        {
            _splitOperationFailed = false;
            _splitStoppedByUser = false;
            _splitDone = false;
            _completedTasksCount = 0;
            SplitProgress = 0;
            Progress = 0;
            ProgressText = $"0/{TotalCount}";
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
                _splitStoppedByUser = true;
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
            else if (!_splitOperationFailed)
            {
                _splitDone = true;

                if (TotalCount > 0)
                {
                    SplitProgress = (_completedTasksCount * 100.0) / TotalCount;
                    Progress = SplitProgress;
                    ProgressText = $"{_completedTasksCount}/{TotalCount}";
                }

                if (SplitProgress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_SplitCompletedSummaryWithWarnings", total, elapsed, succeeded, failed, WarningCount);
                    LogService.Split($"Split completed: {succeeded} succeeded, {failed} failed in {elapsed:F1}s");

                    // 批量处理完成 → 弹系统通知（便携/注册失败渠道静默跳过）
                    NotificationService.ShowBatchCompleted(
                        ResourceService.GetString("Notif_Feature_Split"), succeeded, failed, elapsed, OutputDirectory);
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            IsDirectoryPanelOpen = true;
        }

        /// <summary>从队列中移除指定任务（不删除源文件）</summary>
        public void RemoveTask(SplitTask task)
        {
            if (task == null || IsScanning || IsProcessing) return;
            Tasks.Remove(task);
            TotalCount = Tasks.Count;
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
            ResetScanResults();
            ThumbnailService.ClearCache();
            SetStatus("SplitPage_Status_Cleared");
            IsDirectoryPanelOpen = true;
        }

        protected override void ResetScanResults()
        {
            _lastScanScope = null;
            Tasks.ReplaceRangeIfChanged([]);
            ClearVisibleScanLists();
            UpdateIsQueueEmpty(0);
            TotalCount = 0;
            RecognizedCount = 0;
            SkippedCount = 0;
            _completedTasksCount = 0;
            SplitProgress = 0;
            Progress = 0;
            ProgressText = "0/0";
            _splitStoppedByUser = false;
            _splitDone = false;
            _splitLocalScanTotal = 0;
            _splitLocalScanProcessed = 0;
            _stopwatch.Reset();
            NotifyStatsChanged();
            OnPropertyChanged(nameof(ActionBtnText));
        }

        // <inheritdoc/>
        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Tick -= UiUpdateTimer_Tick;
            Tasks.ReplaceRange([]);
            ClearVisibleScanLists();
            UpdateIsQueueEmpty(0);
            ThumbnailService.ClearCache();
        }

        // <inheritdoc/>
        protected override void OnScanningEnded()
        {
            if (ApplicationShutdown.IsClosing) return;
            base.OnScanningEnded();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
        }

        #endregion

        #region UI Update Timer

        // UI 更新定时器回调，定期刷新拆分进度和进度文本。
        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (TotalCount == 0) return;
            int currentCompleted = _completedTasksCount;
            SplitProgress = (currentCompleted * 100.0) / TotalCount;
            Progress = SplitProgress;
            ProgressText = $"{currentCompleted}/{TotalCount}";
            CheckAndApplyPendingState();
            // 暂停时冻结计时，恢复时继续
            if (IsPaused && _stopwatch.IsRunning)
                _stopwatch.Stop();
            else if (!IsPaused && !_stopwatch.IsRunning && !_splitStoppedByUser && !_splitDone)
                _stopwatch.Start();
            OnPropertyChanged(nameof(ElapsedTimeText));
            OnPropertyChanged(nameof(FooterProgressValue));
            OnPropertyChanged(nameof(FooterStatusText));
        }

        #endregion

        #region Scan Command

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick() || IsProcessing) return;
            if (IsScanning) { CancelScanning(); SetStatus("SplitPage_Status_ScanCancelling"); return; }
            if (string.IsNullOrWhiteSpace(InputDirectory)) { await ShowNoInputDirectoryDialogAsync("Split"); return; }
            if (string.IsNullOrWhiteSpace(OutputDirectory))
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_SplitPhotos"));
            AutoFillOriginalDirectory();
            await ScanInputsAsync(new[] { InputDirectory }, Array.Empty<string>(), replace: true);
        }

        public Task AddFilesToQueueAsync(List<string> filePaths) => ImportStorageItemsAsync(Array.Empty<string>(), filePaths);
        public Task AddFolderToQueueAsync(string folderPath) => ImportStorageItemsAsync(new[] { folderPath }, Array.Empty<string>());
        public Task ImportStorageItemsAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<string> filePaths)
            => ScanInputsAsync(folderPaths, filePaths, replace: false);

        public void ReportImportFailure(Exception error, string path = "")
        {
            LogService.Split($"Import failed: {error}", LogLevel.Error, error);
            ScanDiagnostics.Add(ScanDiagnosticDisplayItem.FromDiagnostic(
                LivePhotoScanSupport.Failure(path, error, "ScanOperationFailed", stage: ScanDiagnosticStage.Capability)));
            SetStatus("ScanStatus_Failed");
        }

        private Task ScanInputsAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<string> filePaths, bool replace, bool clearExisting = false)
        {
            var directories = folderPaths.ToArray();
            var files = filePaths.ToArray();
            if (directories.Length == 0 && files.Length == 0) return Task.CompletedTask;
            LogService.Split($"Scan request: replace={replace}, previousRows={Tasks.Count}, directory={directories.FirstOrDefault()}, explicitFiles={files.Length}");
            return RunScanRequestAsync(replace, request => ExecuteScanInputsAsync(directories, files, replace, request), clearExisting);
        }

        private async Task ExecuteScanInputsAsync(IReadOnlyList<string> folderPaths, IReadOnlyList<string> filePaths, bool replace, ScanRequestQueue.Request request)
        {
            if (ApplicationShutdown.IsClosing || IsProcessing || _isCleaningUp || !request.IsCurrent) return;
            var scanTiming = Stopwatch.StartNew();
            var oldTasks = Tasks.ToList();
            var targetProtocol = MatchProtocolType;
            var discoveryMode = DiscoveryScanMode.SplitOnly;
            var retainedFiles = replace ? Array.Empty<string>() : LivePhotoScanSupport.RetainSelections(
                (_lastScanScope?.ExplicitFiles ?? Array.Empty<string>()).Concat(oldTasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning))
                    .SelectMany(t => new[] {t.SourcePath,t.SecondaryPath}).Where(p=>p!=null).Select(p=>p!))
                    .Concat(ScanDiagnostics.Where(d=>d.IsImage||d.IsVideo).SelectMany(d=>d.RelatedPaths.Prepend(d.PrimaryPath)))).ToArray();
            var options = new MergeScanOptions
            {
                InputDirectories = folderPaths.Concat(!replace ? _lastScanScope?.InputDirectories ?? Array.Empty<string>() : Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ExplicitFiles = filePaths.Concat(retainedFiles).ToArray(),
                Recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false)
            };
            _lastScanScope = options;
            IsScanning = true;
            var token = GetScanningToken(request.Token);
            BeginScanSession();
            SetStatus("SplitPage_Status_Scanning");
            IsDirectoryPanelOpen = false;
            try
            {
                var reporter = CreateScanProgressReporter();
                // No candidate callback: only publish the completed scan.
                var result = await Task.Run(() => LivePhotoDiscoveryService.ScanFilesAsync(options, discoveryMode, token, reporter), token);
                if (!request.IsCurrent) return;
                if (result.Status == ScanOperationStatus.Cancelled || token.IsCancellationRequested)
                {
                    ClearCancelledScanResults();
                    return;
                }
                var stagedTasks = await Task.Run(() => result.Items
                    .Where(item => item.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
                    .Where(item => targetProtocol == null || MapNativeProtocol(item.Protocol) == targetProtocol.Value)
                    .ToDictionary(item =>
                {
                    if (!request.IsCurrent) throw new OperationCanceledException();
                    return item.FilePath;
                }, CreateScanTask, StringComparer.OrdinalIgnoreCase), CancellationToken.None);

                long discoveryAndBatchesMs = scanTiming.ElapsedMilliseconds;
                if (!request.IsCurrent) return;
                BeginScanPresentation();
                await ApplyScanResultAsync(() =>
                {
                    var diagnostics = result.Diagnostics.ToList();
                    var selected = result.Items.Where(i => i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic).ToList();
                    foreach (var item in selected.Where(i => targetProtocol != null && MapNativeProtocol(i.Protocol) != targetProtocol.Value))
                        diagnostics.Add(LivePhotoScanSupport.Info(item.FilePath, "ProtocolFiltered", "This source protocol is not selected."));
                    selected.RemoveAll(i => targetProtocol != null && MapNativeProtocol(i.Protocol) != targetProtocol.Value);
                    var updated = replace ? new List<SplitTask>() : oldTasks.Where(t => t.Status is ProcessStatus.Success or ProcessStatus.Warning).ToList();
                    var includedPaths = updated.Select(t => t.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in selected)
                    {
                        if (!includedPaths.Add(item.FilePath)) continue;
                        updated.Add(stagedTasks.TryGetValue(item.FilePath, out var staged) ? staged : CreateScanTask(item));
                    }
                    var touched = options.ExplicitFiles!.Concat(result.Items.Select(i => i.FilePath))
                        .Concat(diagnostics.Select(d => d.PrimaryPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var retained = replace ? new List<ScanDiagnosticDisplayItem>() : ScanDiagnostics.Where(d => !touched.Contains(d.PrimaryPath)).ToList();
                    retained.AddRange(diagnostics.Select(ScanDiagnosticDisplayItem.FromDiagnostic));
                    UpdateScanDiagnostics(retained);
                    for (int i = 0; i < updated.Count; i++) updated[i].Index = i + 1;
                    Tasks.ReplaceRangeIfChanged(updated);
                    RefreshTaskView(final: true);
                    TotalCount = RecognizedCount = Tasks.Count;
                    SkippedCount = retained.Count;
                    _splitDone = false;
                    _splitStoppedByUser = false;
                    SplitProgress = Progress = 0;
                    ProgressText = $"0/{TotalCount}";
                    FlushPendingScanProgress();
                    NotifyStatsChanged();
                }, result.Status == ScanOperationStatus.Cancelled ? CancellationToken.None : token, () => request.IsCurrent);
                await AwaitScanPresentationAsync(AwaitDisplayRefreshAsync, result.Status == ScanOperationStatus.Cancelled ? CancellationToken.None : token);
                if (!request.IsCurrent) return;
                LogService.Split($"Scan timing: thumbnailsEnabled={ThumbnailService.IsLoadingEnabled}, request={request.Generation}, mode={targetProtocol?.ToString() ?? "all"}, replace={replace}, previousRows={oldTasks.Count}, currentRows={Tasks.Count}, discoveryAndBatchesMs={discoveryAndBatchesMs}, finalListMs={scanTiming.ElapsedMilliseconds - discoveryAndBatchesMs}, totalMs={scanTiming.ElapsedMilliseconds}");
                await ApplyScanResultAsync(() =>
                {
                    if (result.Status == ScanOperationStatus.Completed) CompleteScanSnapshot();
                    SetStatus(result.Status == ScanOperationStatus.Failed ? "ScanStatus_Failed" : "SplitPage_Status_ScanDone", TotalCount);
                }, result.Status == ScanOperationStatus.Cancelled ? CancellationToken.None : token, () => request.IsCurrent);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (request.IsCurrent && !_isCleaningUp) ClearCancelledScanResults();
            }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                if (request.IsCurrent && !_isCleaningUp) ReportImportFailure(ex, options.InputDirectories?.FirstOrDefault() ?? options.ExplicitFiles?.FirstOrDefault() ?? "");
            }
            finally
            {
                OnScanningEnded();
                if (request.IsCurrent && !_isCleaningUp) NotifyStatusChanged();
            }
        }
        private static SplitTask CreateScanTask(LivePhotoDiscoveryItem item) => new()
        {
            SourceFileName = Path.GetFileName(item.FilePath), SourcePath = item.FilePath, SecondaryPath = item.PairedVideoPath,
            FileSize = FileSizeFormatter.Format(item.FileSizeBytes), FileSizeBytes = item.FileSizeBytes,
            DateTaken = item.LastWriteTime, BaseName = Path.GetFileNameWithoutExtension(item.FilePath),
            AppendedVideoLength = item.AppendedVideoLength, Status = ProcessStatus.Pending,
            Details = ResourceService.GetString("SplitPage_Task_Pending")
        };

        #endregion

        #region Helpers

        // 读取图片的 EXIF 拍摄日期（DateTimeOriginal），读不到时降级为文件修改时间。
        private static DateTime GetDateTaken(string imagePath)
        {
            var (_, _, exifDate) = FastMetadataReader.Read(imagePath);
            if (exifDate is not null &&
                DateTime.TryParseExact(exifDate, "yyyy:MM:dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return File.GetLastWriteTime(imagePath);
        }

        // 按命名模板渲染输出基本名（图片/视频共用，扩展名由 Core 追加），并消毒非法字符。
        private string ComputeOutputBaseName(SplitTask task)
        {
            string baseName = Path.GetFileNameWithoutExtension(task.SourcePath);
            string rendered = LivePhotoMergeService.RenderNamingTemplate(
                CustomNamingPattern, baseName, ProtocolIndex, task.Index, task.SourcePath);
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(rendered.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return cleaned.Trim('_', '-', ' ', '+');
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

        // 切换拆分处理状态：开始拆分或停止拆分。
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

            if (_splitStoppedByUser || _splitDone)
            {
                if (_splitStoppedByUser)
                    await ShowSplitCancelledDialogAsync();
                else
                    await ShowSplitAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Split");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                await ShowNoOutputDirectoryDialogAsync("Split");
                return;
            }

            // 检查其他页面是否正在处理中（优先级最低，在所有其他验证之后）
            if (AppViewModel.Instance.IsAnyWorkPageProcessing)
            {
                await ShowOtherPageProcessingDialogAsync();
                return;
            }

            // 开始拆分前：强制归位排序和筛选到默认值
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

        // 显示拆分已完成对话框，可打开输出文件夹。
        private async Task ShowSplitAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_SplitCompletedSummary", total, succeeded, failed) + ProductOutcomePresentation.WarningSummary(WarningCount),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_SplitCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var chosenPrimary = await DialogService.ShowDualAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString("Msg_SplitCompletedTitle"),
                    stack,
                    primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (chosenPrimary)
                    await OpenSplitOutputFolderAsync();
            }
        }

        // 显示拆分已被用户取消的结果对话框，汇总成功/失败/未处理数量。
        private async Task ShowSplitCancelledDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed - WarningCount;

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_SplitCancelledSummary", total, succeeded, failed, unprocessed) + ProductOutcomePresentation.WarningSummary(WarningCount),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_SplitCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var chosenPrimary = await DialogService.ShowDualAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString("Msg_TaskCancelledTitle"),
                    stack,
                    primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (chosenPrimary)
                    await OpenSplitOutputFolderAsync();
            }
        }

        #endregion

        #region Task Execution

        // 执行所有拆分任务的异步核心方法。
        private Task RunTasksAsync()
        {
            return RunSplitTasksAsync();
        }

        private async Task RunSplitTasksAsync()
        {
            SplitMediaFormat outputFormat;
            try
            {
                outputFormat = ProductSelectionAdapter.Split(ProtocolIndex, OutputFormatIndex);
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
            bool overwriteExisting = OverwriteExisting;
            bool keepVideoStabilizationEdges = AppSettingsService.GetValue("KeepVideoStabilizationEdges", false);
            string? inputDirectory = AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false)
                ? InputDirectory : null;
            int sourceAction = AfterCompletionActionIndex;
            string sourceArchive = OriginalDirectory;
            var succeededThisRun = new System.Collections.Concurrent.ConcurrentBag<SplitTask>();
            string capabilityDiagnostic = ProductOutcomePresentation.CapabilityDiagnostic(() => ProductCapabilityQuery.Query(outputFormat));

            try
            {
                Directory.CreateDirectory(outputDir);
                await Task.Run(async () =>
                {
                    var tasksToProcess = Tasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning)).ToList();
                    using var originals = new SourceInputProtection(Tasks.SelectMany(t => new[] { t.SourcePath, t.SecondaryPath }));
                    if (!originals.IsComplete)
                    {
                        foreach (var task in tasksToProcess)
                        {
                            task.Details = originals.Diagnostic ?? "来源保护未完成，未生成输出。";
                            task.Status = ProcessStatus.Failed;
                        }
                        return;
                    }

                    int maxParallel = AppSettingsService.GetValue("SplitThreadCount", 4);

                    var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = Tasks.Count - tasksToProcess.Count;
                    _completedTasksCount = localCompletedCount;
                    var lockObj = new object();

                    async Task ProcessTask(SplitTask task)
                    {
                        await semaphore.WaitAsync(token);

                        bool activeCounted = false;
                        try
                        {
                            PauseEvent.Wait(token);
                            Interlocked.Increment(ref _activeWorkerCount);
                            activeCounted = true;
                            if (token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException();
                            }

                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => { if (!ApplicationShutdown.IsClosing && !_isCleaningUp) UpdateTaskStarted(task); });

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;
                            string outputImagePath = string.Empty;
                            string outputVideoPath = string.Empty;
                            ProductOperationOutcome? outcome = null;

                            try
                            {
                                string outputBaseName = ComputeOutputBaseName(task);
                                outcome = await LivePhotoSplitService.SplitWithOutcomeAsync(
                                    task.SourcePath, outputDir, outputFormat, token,
                                    inputDirectory, outputBaseName, overwriteExisting, task.SecondaryPath,
                                    keepVideoStabilizationEdges: keepVideoStabilizationEdges);
                                isSuccess = outcome.IsSuccess;
                                isCanceled = outcome.Status == ProductOperationStatus.Cancelled;
                                outputImagePath = outcome.CommittedOutputs.FirstOrDefault(output => output.Role == "PrimaryImage")?.Path ?? string.Empty;
                                outputVideoPath = outcome.CommittedOutputs.FirstOrDefault(output => output.Role == "MotionVideo")?.Path ?? string.Empty;
                                if (isSuccess) succeededThisRun.Add(task);
                                detailMessage = ProductOutcomePresentation.Status(outcome);
                            }
                            catch (OperationCanceledException)
                            {
                                isCanceled = true;
                                detailMessage = ResourceService.GetString("Task_Cancelled");
                            }
                            catch (Exception ex)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    // 取消引发的异常——按取消处理，不展示错误详情。
                                    isCanceled = true;
                                    detailMessage = ResourceService.GetString("Task_Cancelled");
                                }
                                else
                                {
                                    isSuccess = false;
                                    detailMessage = ResourceService.Format("Task_Error", ex.Message);
                                    LogService.Split($"Split failed for {task.SourcePath}: {ex.Message}", LogLevel.Error, ex);
                                }
                            }

                            int currentCompleted = 0;
                            if (!isCanceled)
                            {
                                lock (lockObj)
                                {
                                    localCompletedCount++;
                                    currentCompleted = localCompletedCount;
                                    _completedTasksCount = currentCompleted;
                                }
                            }

                            // 死等 UI 线程把状态更新完毕
                            var tcs = new TaskCompletionSource<bool>();
                            if (App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (ApplicationShutdown.IsClosing || _isCleaningUp) return;
                                    task.LastOutcome = outcome;
                                    task.OutcomeSummary = outcome is null ? detailMessage : ProductOutcomePresentation.Describe(outcome, capabilityDiagnostic);
                                    task.OutputImagePath = outcome?.HasUsableOutputs == true ? outputImagePath : string.Empty;
                                    task.OutputVideoPath = outcome?.HasUsableOutputs == true ? outputVideoPath : string.Empty;
                                    if (isCanceled)
                                        UpdateTaskCancelled(task, detailMessage);
                                    else
                                    {
                                        task.OutputImagePath = outcome?.HasUsableOutputs == true ? outputImagePath : string.Empty;
                                        task.OutputVideoPath = outcome?.HasUsableOutputs == true ? outputVideoPath : string.Empty;
                                        UpdateTaskCompleted(task, isSuccess, detailMessage, currentCompleted);
                                    }
                                }
                                finally
                                {
                                    tcs.TrySetResult(true);
                                }
                            }) == true)
                            {
                                await tcs.Task;
                            }
                            else
                            {
                                tcs.TrySetResult(true);
                            }

                            if (isCanceled)
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        finally
                        {
                            if (activeCounted)
                                Interlocked.Decrement(ref _activeWorkerCount);
                            try { semaphore.Release(); }
                            catch (ObjectDisposedException) { }
                        }
                    }

                    try
                    {
                        foreach (var task in tasksToProcess)
                        {
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            pendingTasks.Add(ProcessTask(task));

                            if (pendingTasks.Count >= maxParallel)
                            {
                                var completedTask = await Task.WhenAny(pendingTasks);
                                pendingTasks.Remove(completedTask);

                                try
                                {
                                    await completedTask;
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                            }
                        }

                        if (!token.IsCancellationRequested)
                        {
                            await Task.WhenAll(pendingTasks);
                        }

                        if (token.IsCancellationRequested)
                        {
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    finally
                    {
                        try { await Task.WhenAll(pendingTasks); } catch { }
                        semaphore.Dispose();
                    }
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed - WarningCount;
                double elapsed = _stopwatch.Elapsed.TotalSeconds;
                LogService.Split($"Processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalCount}");
                SetStatus("Status_SplitStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                _splitOperationFailed = true;
                ReportImportFailure(ex, OutputDirectory);
            }
            finally
            {
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;

                // ── 完成后处理原始文件 ──
                if (!wasCancelled && !_splitOperationFailed)
                {
                    if (sourceAction is 1 or 2)
                        foreach (var task in succeededThisRun)
                        {
                            if (token.IsCancellationRequested) break;
                            try
                            {
                                var outcome = task.LastOutcome ?? throw new IOException("Missing completion receipt.");
                                var paths = new[] { task.SourcePath, task.SecondaryPath }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                                string? moveDir = sourceAction == 1 ? sourceArchive : null;
                                if (ApplicationShutdown.IsClosing) break;
                                var processing = await ProductSourcePostProcessor.ProcessAsync(outcome, paths,
                                    sourceAction == 1, moveDir, token);
                                task.LastOutcome = ProductOutcomeCompletion.Record(outcome with { SourcePostProcessing = processing });
                                task.OutcomeSummary = ProductOutcomePresentation.Describe(task.LastOutcome, capabilityDiagnostic);
                                if (processing.Status != "completed")
                                {
                                    task.Details = ResourceService.GetString("ProductWarning_SourceAction") + ": " + processing.Reason;
                                    task.Status = ProcessStatus.Warning;
                                    NotifyStatsChanged();
                                    if (processing.Status == "cancelled") break;
                                }
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
                                LogService.Split("Source post-processing failed: " + ex.Message, LogLevel.Warning, ex);
                            }
                        }
                }

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot。
                wasCancelled = _cancelledByUser;
                FinalizeRunState();

                if (Tasks.Count > 0 && !_isCleaningUp && !_splitOperationFailed)
                {
                    try
                    {
                        if (wasCancelled)
                            await ShowSplitCancelledDialogAsync();
                        else
                            await ShowSplitAlreadyDoneDialogAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        LogService.Debug($"Completion dialog skipped (another dialog already open): {ex.Message}", LogSource.UI);
                    }
                }
            }
        }

        private static Task MoveFileToRecycleBinAsync(string path, ProductOperationOutcome outcome) =>
            Task.Run(() => SourceRecycleBinService.Recycle(outcome, path));

        #endregion

        #region Task Status Events

        // 当某个拆分任务开始时触发，可用于自动滚动到当前处理的任务。
        public event EventHandler<SplitTask>? TaskStartedForScroll;

        // 当所有拆分任务处理完毕（全部完成或停止）时触发，可用于滚动到列表顶部。
        public event EventHandler? ProcessingCompletedForScroll;

        // 标记任务开始处理（设置为 Processing 状态）。
        private void UpdateTaskStarted(SplitTask task)
        {
            task.LastOutcome = null;
            task.OutcomeSummary = string.Empty;
            task.IsCancelled = false;
            task.OutputImagePath = string.Empty;
            task.OutputVideoPath = string.Empty;
            task.Status = ProcessStatus.Processing;
            task.ProgressText = "0%";
            task.Details = ResourceService.GetString("SplitPage_Task_Processing");
            NotifyStatsChanged();
            TaskStartedForScroll?.Invoke(this, task);
        }

        // 标记任务被用户取消：保留 Processing 状态显示（颜色中性），
        // 通过 IsCancelled 标记计入底部"已取消"统计；不标为 Failed，避免显示错误语义。
        private void UpdateTaskCancelled(SplitTask task, string detailMessage)
        {
            task.IsCancelled = true;
            task.Status = ProcessStatus.Cancelled;
            task.ProgressText = "0%";
            task.Details = detailMessage;
            NotifyStatsChanged();
        }

        // 更新任务完成状态（成功/失败），如果所有任务完成则触发 ProcessingCompletedForScroll 事件。
        private void UpdateTaskCompleted(SplitTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = task.LastOutcome?.Status == ProductOperationStatus.Warning ? ProcessStatus.Warning : isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.ProgressText = isSuccess ? "100%" : "0%";
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
        private async Task OpenSplitInputFolderAsync()
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
            catch (Exception ex) { LogService.Split($"OpenSplitInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 在文件资源管理器中打开输出文件夹（不存在则自动创建）。
        private async Task OpenSplitOutputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { LogService.Split($"OpenSplitOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 在文件资源管理器中打开原始文件存放目录。
        private Task OpenSplitOriginalDirAsync()
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
            catch (Exception ex) { LogService.Split($"OpenSplitOriginalDir error: {ex.Message}", LogLevel.Error, ex); }
            return Task.FromException(new IOException("Recycle bin UI dispatcher is unavailable; originals retained."));
        }

        #endregion

    }
}
