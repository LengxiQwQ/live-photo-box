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
using LivePhotoBox.Helpers;
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
        // 用于统计拆分耗时的计时器。
        private Stopwatch _stopwatch = new();

        // 拆分是否被用户手动停止。
        private bool _splitStoppedByUser;

        // 所有拆分任务是否已完成（成功/失败都算）。
        private bool _splitDone;

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

        // 处理中的状态文本，包含硬件加速信息。
        protected override string ProcessingStatusText =>
            ResourceService.Format("SplitPage_Status_Running") + GetHardwareSuffix();

        #region Observable Properties

        // 输入文件夹路径（用户选择的待扫描目录）。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openSplitInputFolderCommand?.NotifyCanExecuteChanged();
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanDirectoryCommand.CanExecute(null) && !IsScanning)
                {
                    ScanDirectoryCommand.Execute(null);
                }
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
        public int FailedCount => Tasks.Count(t => t.Status == ProcessStatus.Failed);
        // 已取消 = 尚未开始即被取消（Cancelled 状态）+ 处理中被打断被取消（IsCancelled 标记，
        // 保留 Processing 状态显示、颜色中性）。
        public int CancelledCount => Tasks.Count(t => t.Status == ProcessStatus.Cancelled || t.IsCancelled);

        public string ActiveTaskCountText => IsProcessing
            ? ResourceService.Format("SplitPage_StatusProcessing", ProcessingCount)
            : TotalCount > 0
                ? ResourceService.Format("SplitPage_StatusTotal", TotalCount)
                : string.Empty;

        // 队列标题后的扫描统计："识别 5，跳过 3"
        public string ScanStatsText => RecognizedCount > 0 || SkippedCount > 0
            ? $"{ResourceService.Format("SplitPage_ScanRecognized", RecognizedCount)}  •  {ResourceService.Format("SplitPage_ScanSkipped", SkippedCount)}"
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
                    return _splitLocalScanTotal > 0
                        ? ResourceService.Format("SplitPage_Status_ScanningProgress", _splitLocalScanProcessed, _splitLocalScanTotal)
                        : ResourceService.GetString("Status_Scanning");
                }
                if (IsProcessing)
                {
                    // 停止中（用户点了停止，等待任务中断）
                    if (_cancelledByUser)
                        return ResourceService.GetString("Status_Stopping");
                    // 已暂停（暂停完成，等待用户点击继续）
                    if (IsPaused)
                        return ResourceService.GetString("Status_Paused") + GetHardwareSuffix();
                    // 暂停中（等待当前任务结束再进入暂停）
                    if (_isPausing)
                        return ResourceService.GetString("Status_Pausing");
                    return ProcessingStatusText;
                }
                if (_splitStoppedByUser)
                    return ResourceService.GetString("Status_StoppedSimple");
                if (_splitDone && Progress >= 100)
                    return ResourceService.GetString("Status_DoneSimple");
                // Idle / Ready — 使用 SetStatus 设置的文字
                return Status;
            }
        }

        // ── 统计项可见性（零值自动隐藏） ──

        public Visibility FooterTotalVisible => TotalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterSuccessVisible => SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterFailedVisible => FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterUnprocessedVisible => (!_splitStoppedByUser && PendingCount > 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterCancelledVisible => (_splitStoppedByUser && CancelledCount > 0) ? Visibility.Visible : Visibility.Collapsed;

        private void NotifyStatsChanged()
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(ProcessingCount));
            OnPropertyChanged(nameof(SuccessCount));
            OnPropertyChanged(nameof(FailedCount));
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
                OnPropertyChanged();
            }
        }

        // 匹配方式索引：0=所有单文件实况照片协议 / 1=Fusion / 2=MicroVideo V1 / 3=MotionPhoto V2
        //                / 4=OPPO O-Live Photo / 5=vivo Live Photo / 6=Samsung Motion Photo / 7=HUAWEI Moving Photo。
        // 扫描时按此协议过滤单文件实况照片（复用 Core 的 LivePhotoProtocolDetector）。
        public int MatchProtocolIndex
        {
            get
            {
                int value = AppSettingsService.GetValue("SplitMatchProtocolIndex", 0);
                // Fusion 已隐藏（index 1），若历史设置仍为 1，则回退到“所有单文件”。
                return value == 1 ? 0 : value;
            }
            set
            {
                AppSettingsService.SetValue("SplitMatchProtocolIndex", value);
                LogService.Split($"Split match protocol changed to index: {value}");
                OnPropertyChanged();
            }
        }

        // 匹配方式索引 → 目标协议类型（null 表示"所有单文件"，不过滤）。
        private LivePhotoProtocolType? MatchProtocolType => MatchProtocolIndex switch
        {
            1 => LivePhotoProtocolType.Fusion,
            2 => LivePhotoProtocolType.GoogleV1,
            3 => LivePhotoProtocolType.GoogleV2,
            4 => LivePhotoProtocolType.OPPO,
            5 => LivePhotoProtocolType.Vivo,
            6 => LivePhotoProtocolType.Samsung,
            7 => LivePhotoProtocolType.Huawei,
            _ => null
        };

        // 判断单文件实况照片是否符合当前"匹配方式"选中的协议（"所有单文件"=全部通过）。
        private bool PassesProtocolFilter(LivePhotoDiscoveryItem item)
            => PassesProtocolFilter(item.FilePath, item.LivePhotoType);

        // 按 (路径, 类型) 判断协议，供没有 LivePhotoDiscoveryItem 的调用点（逐文件入队）复用。
        private bool PassesProtocolFilter(string filePath, LivePhotoType type)
        {
            var target = MatchProtocolType;
            if (target == null) return true;
            return LivePhotoProtocolDetector.Detect(filePath, type) == target.Value;
        }

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

        private void RefreshTaskView()
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

            query = SortIndex switch
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

        // 输出格式索引（全局：0=默认原样 / 1=JPG+MOV(H265) / 2=HEIC+MOV(H265) / 3=JPG+MP4(H264)）。
        public int OutputFormatIndex
        {
            get => AppSettingsService.GetValue("SplitOutputFormatIndex", 0);
            set
            {
                AppSettingsService.SetValue("SplitOutputFormatIndex", value);
                LogService.Split($"Output format changed to index: {value}");
                OnPropertyChanged();
            }
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
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            // 扫描添加任务时自动同步到显示列表
            Tasks.CollectionChanged += (_, _) => RefreshTaskView();

            // 响应全局设置清除：刷新所有持久化属性 + 清空命名片段
            AppSettingsService.SettingsCleared += () =>
            {
                OnPropertyChanged(nameof(ProtocolIndex));
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
        public override bool IsProcessingAllowed => !IsScanning;

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
            else
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

                    SetStatus("Status_SplitCompletedSummary", total, elapsed, succeeded, failed);
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
            if (task == null) return;
            Tasks.Remove(task);
            TotalCount = Tasks.Count;
            UpdateIsQueueEmpty(Tasks.Count);

            // 删除到最后一行 = 视为清空列表：与"清空列表"按钮走同一套完整重置逻辑，
            // 否则用户逐行删完还得再点一次"清空列表"才是真清空。
            if (Tasks.Count == 0 && !IsProcessing && !IsScanning)
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
            UpdateIsQueueEmpty(0);
            ThumbnailService.ClearCache();
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
            SetStatus("SplitPage_Status_Cleared");
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

        // <inheritdoc/>
        protected override void OnScanningEnded()
        {
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

        // 扫描输入文件夹中的单文件实况照片（JPEG）。
        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("SplitPage_Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Split");
                return;
            }
            if (!Directory.Exists(InputDirectory))
            {
                await ShowInvalidInputDirectoryDialogAsync();
                return;
            }

            IsScanning = true;
            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_SplitPhotos"));
            }

            // 扫描开始时自动填充原始文件移动目录（仅当用户未手动设置过时覆盖）
            AutoFillOriginalDirectory();

            LogService.Split($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            SetStatus("SplitPage_Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _splitStoppedByUser = false;
            _splitDone = false;

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
                }

                // ── 使用统一发现服务扫描（SplitOnly 单文件实况检测）──
                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        InputDirectory, DiscoveryScanMode.SplitOnly, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                // 转换为 SplitTask：只取单文件 JPEG 实况照片，并按"匹配方式"过滤协议
                int index = 0;
                var liveItems = discoveryResult.Items
                    .Where(i => i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
                    .Where(i => PassesProtocolFilter(i))
                    .ToList();

                var tempTasks = liveItems.Select(item =>
                {
                    index++;
                    string baseName = Path.GetFileNameWithoutExtension(item.FilePath);
                    return new SplitTask
                    {
                        Index = index,
                        SourceFileName = Path.GetFileName(item.FilePath),
                        SourcePath = item.FilePath,
                        FileSize = FileSizeFormatter.Format(item.FileSizeBytes),
                        FileSizeBytes = item.FileSizeBytes,
                        DateTaken = GetDateTaken(item.FilePath),
                        BaseName = baseName,
                        AppendedVideoLength = item.AppendedVideoLength,
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };
                }).ToList();

                int finalCount = tempTasks.Count;

                Tasks.ReplaceRange(tempTasks);
                UpdateIsQueueEmpty(tempTasks.Count);
                TotalCount = finalCount;
                // 识别 = 入队数量（已按"匹配方式"过滤）；跳过 = 扫描文件总数 − 识别
                RecognizedCount = finalCount;
                SkippedCount = discoveryResult.Items.Count - finalCount;
                NotifyStatsChanged();

                LogService.Split($"Scan complete: {finalCount} queued, {discoveryResult.Items.Count - finalCount} skipped");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    SplitProgress = 0;
                    Progress = 0;
                    ProgressText = $"0/{TotalCount}";
                });

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (TotalCount > 0)
                    SetStatus("SplitPage_Status_ScanDone", TotalCount);
                else
                {
                    IsDirectoryPanelOpen = true;
                    SetStatus("SplitPage_Status_NoLivePhotos");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("SplitPage_Status_ScanCancelled");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    UpdateIsQueueEmpty(0);
                    ThumbnailService.ClearCache();
                    TotalCount = 0;
                    RecognizedCount = 0;
                    SkippedCount = 0;
                    SplitProgress = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Split($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
            }
        }

        // 添加文件到队列（追加，不清空）。仅接受单文件实况照片（JPEG + HEIC）。
        public async Task AddFilesToQueueAsync(List<string> filePaths)
        {
            if (filePaths.Count == 0) return;

            var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".heif" };
            var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
            var newTasks = new List<SplitTask>();

            foreach (var path in filePaths)
            {
                if (!imgExts.Contains(Path.GetExtension(path))) continue;

                // 逐文件单文件实况检测（JPEG 字节标记 + HEIC 视频轨），与主扫描 ScanAsync 一致
                LivePhotoType type;
                try { type = await LivePhotoDiscoveryService.DetectSingleFileTypeAsync(path); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (type == LivePhotoType.None) continue;
                // 按"匹配方式"过滤协议
                if (!PassesProtocolFilter(path, type)) continue;

                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; }

                int index = Tasks.Count + newTasks.Count + 1;
                newTasks.Add(new SplitTask
                {
                    Index = index,
                    SourceFileName = Path.GetFileName(path),
                    SourcePath = path,
                    FileSize = FileSizeFormatter.Format(size),
                    FileSizeBytes = size,
                    DateTaken = GetDateTaken(path),
                    BaseName = Path.GetFileNameWithoutExtension(path),
                    AppendedVideoLength = 0,
                    Status = ProcessStatus.Pending,
                    Details = pendingText
                });
            }

            // 未被识别/被过滤掉的都计入跳过（未匹配）
            int newlySkipped = filePaths.Count - newTasks.Count;
            if (newTasks.Count > 0)
            {
                foreach (var t in newTasks)
                    Tasks.Add(t);
                TotalCount = Tasks.Count(t => t != null);
                SkippedCount += newlySkipped;
                UpdateIsQueueEmpty(Tasks.Count);
                NotifyStatsChanged();
                LogService.Split($"Added {newTasks.Count} file(s) to queue (total: {TotalCount}, skipped: {SkippedCount})");
            }
            else if (newlySkipped > 0)
            {
                // 全部未匹配也要刷新统计显示
                SkippedCount += newlySkipped;
                NotifyStatsChanged();
                LogService.Split($"No files matched (skipped: {SkippedCount})");
            }
        }

        // 添加文件夹到队列（追加，不清空）
        public async Task AddFolderToQueueAsync(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            var discoveryResult = await Task.Run(
                () => LivePhotoDiscoveryService.ScanAsync(
                    folderPath, DiscoveryScanMode.SplitOnly, GetScanningToken()));
            var liveItems = discoveryResult.Items
                .Where(i => i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
                .Where(i => PassesProtocolFilter(i))
                .ToList();
            // 本文件夹被丢弃的文件计入跳过（即使没有入队也更新）
            SkippedCount += discoveryResult.Items.Count - liveItems.Count;
            if (liveItems.Count == 0)
            {
                NotifyStatsChanged();
                return;
            }

            var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
            int startIndex = Tasks.Count;

            foreach (var item in liveItems)
            {
                Tasks.Add(new SplitTask
                {
                    Index = ++startIndex,
                    SourceFileName = Path.GetFileName(item.FilePath),
                    SourcePath = item.FilePath,
                    FileSize = FileSizeFormatter.Format(item.FileSizeBytes),
                    FileSizeBytes = item.FileSizeBytes,
                    DateTaken = GetDateTaken(item.FilePath),
                    BaseName = Path.GetFileNameWithoutExtension(item.FilePath),
                    AppendedVideoLength = item.AppendedVideoLength,
                    Status = ProcessStatus.Pending,
                    Details = pendingText
                });
            }

            TotalCount = Tasks.Count(t => t != null);
            RecognizedCount = TotalCount;
            UpdateIsQueueEmpty(Tasks.Count);
            NotifyStatsChanged();
            LogService.Split($"Added folder '{folderPath}' to queue (total: {TotalCount})");
        }

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
                    Text = ResourceService.Format("Msg_SplitCompletedSummary", total, succeeded, failed),
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
                int unprocessed = total - succeeded - failed;

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_SplitCancelledSummary", total, succeeded, failed, unprocessed),
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
            return ProcessingPipelineRouter.RunAsync("split", RunLegacyTasksAsync);
        }

        private async Task RunLegacyTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();
            OnPropertyChanged(nameof(ElapsedTimeText));

            var token = GetProcessingToken();
            string outputDir = OutputDirectory;
            int protocolIndex = ProtocolIndex;
            int outputFormatIndex = OutputFormatIndex;
            bool overwriteExisting = OverwriteExisting;
            string inputDirectory = InputDirectory;
            Directory.CreateDirectory(outputDir);

            try
            {
                await Task.Run(async () =>
                {
                    var tasksToProcess = Tasks.Where(t => t.Status != ProcessStatus.Success).ToList();

                    int maxParallel = AppSettingsService.GetValue("SplitThreadCount", 4);

                    var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = 0;
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

                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task));

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;
                            string outputImagePath = string.Empty;
                            string outputVideoPath = string.Empty;

                            try
                            {
                                string outputBaseName = ComputeOutputBaseName(task);
                                var result = await LivePhotoSplitService.SplitAsync(
                                    task.SourcePath, outputDir, protocolIndex, outputFormatIndex, token,
                                    inputDirectory, outputBaseName, overwriteExisting);
                                outputImagePath = result.ImageOutputPath;
                                outputVideoPath = result.VideoOutputPath;
                                isSuccess = true;
                                detailMessage = ResourceService.GetString("SplitPage_Task_Success") ?? "Success";
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
                                    // 取消引发的第三方工具异常（ffmpeg/exiftool 被终止）——按取消处理，不展示错误详情。
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
                                    if (isCanceled)
                                        UpdateTaskCancelled(task, detailMessage);
                                    else
                                    {
                                        task.OutputImagePath = isSuccess ? outputImagePath : string.Empty;
                                        task.OutputVideoPath = isSuccess ? outputVideoPath : string.Empty;
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
            catch (OperationCanceledException)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed;
                double elapsed = _stopwatch.Elapsed.TotalSeconds;
                LogService.Split($"Processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalCount}");
                SetStatus("Status_SplitStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Split($"RunTasksAsync fatal error: {ex.Message}", LogLevel.Error, ex);
                Environment.ExitCode = unchecked((int)0xE0000001);
                throw;
            }
            finally
            {
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;
                FinalizeRunState();

                // 所有任务结束后统一清理 Temp 目录（确保无残留）
                CleanSplitTempDirectory(outputDir);

                // ── 完成后处理原始文件 ──
                if (!wasCancelled)
                {
                    if (AfterCompletionActionIndex == 1) // 移动到指定目录
                    {
                        var moveDir = OriginalDirectory;
                        if (!string.IsNullOrWhiteSpace(moveDir))
                        {
                            try { Directory.CreateDirectory(moveDir); }
                            catch (Exception ex) { LogService.Split($"Failed to create original dir: {ex.Message}", LogLevel.Warning); }
                            foreach (var task in Tasks.Where(t => t.Status == ProcessStatus.Success))
                            {
                                try { if (File.Exists(task.SourcePath)) File.Move(task.SourcePath, Path.Combine(moveDir, Path.GetFileName(task.SourcePath))); } catch { }
                            }
                        }
                    }
                    else if (AfterCompletionActionIndex == 2) // 回收站
                    {
                        foreach (var task in Tasks.Where(t => t.Status == ProcessStatus.Success))
                        {
                            try
                            {
                                if (File.Exists(task.SourcePath))
                                    await MoveFileToRecycleBinAsync(task.SourcePath);
                            }
                            catch (Exception ex)
                            {
                                LogService.Split($"Failed to move source file to recycle bin: {ex.Message}", LogLevel.Warning, ex);
                            }
                        }
                    }
                }

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot。
                if (Tasks.Count > 0 && !_isCleaningUp)
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

        // 将文件移动到回收站（真正进回收站，可恢复）。
        // 通过 P/Invoke SHFileOperationW（FOF_ALLOWUNDO）实现，效果等同资源管理器删除。
        private static Task MoveFileToRecycleBinAsync(string path)
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null)
            {
                LogService.Split("Recycle bin unavailable: MainWindow DispatcherQueue is null", LogLevel.Warning);
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>();
            bool enqueued = dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (SendToRecycleBin(path))
                        tcs.SetResult(null);
                    else
                        tcs.SetException(new InvalidOperationException($"SHFileOperationW failed for {path}"));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (!enqueued)
            {
                LogService.Split($"Recycle bin unavailable: failed to enqueue to UI thread for {path}", LogLevel.Warning);
                return Task.CompletedTask;
            }

            return tcs.Task;
        }

        // 调用 shell 将单个文件送入回收站，返回是否成功。
        private static bool SendToRecycleBin(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };
            return SHFileOperationW(ref op) == 0;
        }

        // ── 回收站 P/Invoke（SHFileOperationW）────────────────
        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        #endregion

        #region Task Status Events

        // 当某个拆分任务开始时触发，可用于自动滚动到当前处理的任务。
        public event EventHandler<SplitTask>? TaskStartedForScroll;

        // 当所有拆分任务处理完毕（全部完成或停止）时触发，可用于滚动到列表顶部。
        public event EventHandler? ProcessingCompletedForScroll;

        // 标记任务开始处理（设置为 Processing 状态）。
        private void UpdateTaskStarted(SplitTask task)
        {
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
            task.ProgressText = "0%";
            task.Details = detailMessage;
            NotifyStatsChanged();
        }

        // 更新任务完成状态（成功/失败），如果所有任务完成则触发 ProcessingCompletedForScroll 事件。
        private void UpdateTaskCompleted(SplitTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
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
            return Task.CompletedTask;
        }

        #endregion

        // 安全地清理拆分过程的 Temp 目录（全部任务结束后调用）。
        private static void CleanSplitTempDirectory(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir)) return;
            try
            {
                string tempDir = Path.Combine(outputDir, "Temp");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                    LogService.Split($"Cleaned split Temp directory: {tempDir}");
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"Failed to clean split Temp directory: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
