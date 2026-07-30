// <copyright file="MergeViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using System.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
    // 实况照片合并页面的 ViewModel，对应 MergePage。
    // 负责扫描输入文件夹中的图片-视频配对，选择合并协议（Google/HEIC/OPPO），
    // 以及执行合并任务并将结果写入输出文件夹。
    public partial class MergeViewModel : WorkViewModelBase
    {
        // 用于统计合并耗时的计时器。
        private Stopwatch _stopwatch = new();

        // 合并是否被用户手动停止。
        private bool _mergeStoppedByUser;

        // 所有合并任务是否已完成（成功/失败都算）。
        private bool _mergeDone;

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

        // 处理中的状态文本，包含当前选中的协议名称和硬件加速信息。
        protected override string ProcessingStatusText =>
            ResourceService.Format("Status_Running") + " | " +
            LivePhotoProtocol.FromIndex(SelectedModeIndex).DisplayName +
            GetHardwareSuffix();

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
        public int FailedCount => Tasks.Count(t => t.Status == ProcessStatus.Failed);
        public int CancelledCount => Tasks.Count(t => t.Status == ProcessStatus.Cancelled);

        public string ActiveTaskCountText => IsProcessing
            ? ResourceService.Format("MergePage_StatusProcessing", ProcessingCount)
            : TotalPairsCount > 0
                ? ResourceService.Format("MergePage_StatusTotal", TotalPairsCount)
                : string.Empty;

        // 队列标题后的扫描统计："匹配 5，未匹配 3"
        public string ScanStatsText => TotalPairsCount > 0 || StandaloneImagesCount > 0 || StandaloneVideosCount > 0
            ? $"{ResourceService.Format("MergePage_ScanMatched", TotalPairsCount)}  •  {ResourceService.Format("MergePage_ScanUnmatched", StandaloneImagesCount + StandaloneVideosCount)}"
            : string.Empty;

        public string ElapsedTimeText => _stopwatch.Elapsed.TotalSeconds > 0
            ? ResourceService.Format("MergePage_StatusElapsed", _stopwatch.Elapsed.ToString(@"mm\:ss"))
            : ResourceService.GetString("MergePage_StatusElapsedIdle");

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
                    return _mergeLocalScanTotal > 0
                        ? ResourceService.Format("StatusBar_Scanning_Merge", _mergeLocalScanProcessed, _mergeLocalScanTotal)
                        : ResourceService.GetString("Status_Scanning");
                }
                if (IsProcessing)
                    return ProcessingStatusText;
                if (IsPaused)
                    return ResourceService.GetString("Status_Paused") + " | " +
                           LivePhotoProtocol.FromIndex(SelectedModeIndex).DisplayName +
                           GetHardwareSuffix();
                if (_mergeStoppedByUser)
                    return ResourceService.GetString("Status_StoppedSimple");
                if (_mergeDone && Progress >= 100)
                    return ResourceService.GetString("Status_DoneSimple");
                // Idle / Ready — 使用 SetStatus 设置的文字（如 "初始化"、"扫描完成：..."）
                return Status;
            }
        }

        // ── 统计项可见性（零值自动隐藏） ──

        public Visibility FooterTotalVisible => TotalPairsCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterSuccessVisible => SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterFailedVisible => FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterUnprocessedVisible => (!_mergeStoppedByUser && PendingCount > 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FooterCancelledVisible => (_mergeStoppedByUser && CancelledCount > 0) ? Visibility.Visible : Visibility.Collapsed;

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
            ? ResourceService.GetString("MergePage_DynamicCancelText")
            : ResourceService.GetString("MergePage_DynamicScanText");

        // 合并任务列表（Observable 集合，支持高性能批量更新）。
        public BulkObservableCollection<MergeTask> Tasks { get; } = [];

        // 当前选中的合并模式（协议）索引，持久化存储到设置中。
        public int SelectedModeIndex
        {
            get => AppSettingsService.GetValue(nameof(SelectedModeIndex), 2);
            set
            {
                AppSettingsService.SetValue(nameof(SelectedModeIndex), value);
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

        // 在文件资源管理器中打开输出文件夹的命令（仅路径存在时启用）。
        public IAsyncRelayCommand OpenMergeOutputFolderCommand => _openMergeOutputFolderCommand ??= new AsyncRelayCommand(OpenMergeOutputFolderAsync, () => DirectoryHelper.CanOpenFolder(OutputDirectory));

        // 在文件资源管理器中打开原始文件存放目录的命令（仅路径存在时启用）。
        public IAsyncRelayCommand OpenMergeOriginalDirCommand => _openMergeOriginalDirCommand ??= new AsyncRelayCommand(OpenMergeOriginalDirAsync, () => DirectoryHelper.CanOpenFolder(OriginalDirectory));

        // ── 转换设置 ──

        // 输出格式索引（0=JPG+MP4, 1=JPG+MOV, 2=HEIC+MP4, 3=HEIC+MOV）。
        public int OutputFormatIndex
        {
            get => AppSettingsService.GetValue(nameof(OutputFormatIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(OutputFormatIndex), value);
                LogService.Merge($"Output format changed to index: {value}");
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
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            // 扫描添加任务时自动同步到显示列表
            Tasks.CollectionChanged += (_, _) => RefreshTaskView();

            // 响应全局设置清除：刷新所有持久化属性 + 清空命名片段
            AppSettingsService.SettingsCleared += () =>
            {
                OnPropertyChanged(nameof(SelectedModeIndex));
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
        public override bool IsProcessingAllowed => !IsScanning;

        // 当前是否可以编辑合成配置（仅处理中不可编辑，扫描时允许边扫边配）。
        public bool CanEditSelectedMode => !IsProcessing;

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
        }

        // <inheritdoc/>
        protected override void OnBeginScanSession()
        {
            _mergeLocalScanTotal = 0;
            _mergeLocalScanProcessed = 0;
            AppViewModel.Instance.BeginMergeScanSession();
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
            else
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

                    // ✨【状态栏统计显示修复】：使用专属多语言词条
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_MergeCompletedSummary", total, elapsed, succeeded, failed);
                    LogService.Merge($"Merge completed: {succeeded} succeeded, {failed} failed in {elapsed:F1}s");
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            IsDirectoryPanelOpen = true;
        }

        /// <summary>从队列中移除指定任务（不删除源文件）</summary>
        public void RemoveTask(MergeTask task)
        {
            if (task == null) return;
            Tasks.Remove(task);
            TotalPairsCount = Tasks.Count;
            UpdateIsQueueEmpty(Tasks.Count);
            NotifyStatsChanged();
        }

        // <inheritdoc/>
        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
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

        // 扫描输入文件夹中的图片-视频配对，支持文件名匹配和元数据匹配两种模式。
        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Merge");
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
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_LivePhotos"));
            }

            // 扫描开始时自动填充原始文件移动目录（仅当用户未手动设置过时覆盖）
            AutoFillOriginalDirectory();

            LogService.Merge($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            SetStatus("Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _mergeStoppedByUser = false;
            _mergeDone = false;

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
                }

                // ── 使用统一发现服务扫描 ──
                // Mutually exclusive: only the selected matching method runs
                DiscoveryScanMode scanMode = PairingMethodIndex switch
                {
                    0 => DiscoveryScanMode.FilenamePair,
                    1 => DiscoveryScanMode.CidMatch,
                    2 => DiscoveryScanMode.VivoMatch,
                    _ => DiscoveryScanMode.FilenamePair
                };

                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        InputDirectory, scanMode, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                // 转换为 MergeTask：只取 DualFile 类型的图片条目（每个图片对应一个配对）
                int index = 0;
                var dualFileImages = discoveryResult.Items
                    .Where(i => i.LivePhotoType == LivePhotoType.DualFile && i.IsImage);

                var tempTasks = dualFileImages.Select(img =>
                {
                    index++;
                    string baseName = Path.GetFileNameWithoutExtension(img.FilePath);
                    string vidPath = img.PairedVideoPath ?? "";
                    long vidSize = 0;
                    string vidFileName = "";

                    if (!string.IsNullOrEmpty(vidPath))
                    {
                        try
                        {
                            vidSize = new System.IO.FileInfo(vidPath).Length;
                            vidFileName = Path.GetFileName(vidPath);
                        }
                        catch (System.IO.IOException ex)
                        {
                            LogService.Merge($"Failed to get video file size for {baseName}", LogLevel.Warning, ex);
                        }
                    }

                    return new MergeTask
                    {
                        Index = index,
                        ImageFileName = Path.GetFileName(img.FilePath),
                        VideoFileName = vidFileName,
                        ImageSize = FileSizeFormatter.Format(img.FileSizeBytes),
                        VideoSize = FileSizeFormatter.Format(vidSize),
                        ImageSizeBytes = img.FileSizeBytes,
                        VideoSizeBytes = vidSize,
                        TotalSizeBytes = img.FileSizeBytes + vidSize,
                        DateTaken = GetDateTaken(img.FilePath),
                        BaseName = baseName,
                        ImagePath = img.FilePath,
                        VideoPath = vidPath,
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };
                }).ToList();

                int standaloneImg = discoveryResult.StandaloneImagePaths.Count;
                int standaloneVid = discoveryResult.StandaloneVideoPaths.Count;
                int totalPairs = tempTasks.Count;

                Tasks.ReplaceRange(tempTasks);
                UpdateIsQueueEmpty(tempTasks.Count);
                TotalPairsCount = totalPairs;
                NotifyStatsChanged();
                StandaloneImagesCount = standaloneImg;
                StandaloneVideosCount = standaloneVid;

                LogService.Merge($"Scan complete: {totalPairs} pairs, {standaloneImg} standalone images, {standaloneVid} standalone videos");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    MergeProgress = 0;
                    Progress = 0;
                    ProgressText = $"0/{TotalPairsCount}";
                });

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (TotalPairsCount > 0)
                    SetStatus("Status_ScanDone", TotalPairsCount);
                else
                {
                    IsDirectoryPanelOpen = true;
                    SetStatus("Status_ScanNoPairs", StandaloneImagesCount, StandaloneVideosCount);
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Status_ScanCancelled");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    UpdateIsQueueEmpty(0);
                    ThumbnailService.ClearCache();
                    TotalPairsCount = 0;
                    StandaloneImagesCount = 0;
                    StandaloneVideosCount = 0;
                    MergeProgress = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Merge($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
            }
        }

        // 添加文件到队列（追加，不清空）。按当前配对方式验证后才加入。
        public async Task AddFilesToQueueAsync(List<string> filePaths)
        {
            if (filePaths.Count == 0) return;

            // 分离图片和视频
            var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".heif" };
            var vidExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
            var images = filePaths.Where(p => imgExts.Contains(Path.GetExtension(p))).ToList();
            var videos = filePaths.Where(p => vidExts.Contains(Path.GetExtension(p))).ToList();

            // 按 basename 粗配对
            var vidDict = videos.ToDictionary(
                v => Path.GetFileNameWithoutExtension(v), v => v,
                StringComparer.OrdinalIgnoreCase);

            var pendingText = ResourceService.GetString("Task_Pending");
            var newTasks = new List<MergeTask>();

            foreach (var imgPath in images)
            {
                var baseName = Path.GetFileNameWithoutExtension(imgPath);
                if (!vidDict.TryGetValue(baseName, out var vidPath)) continue;

                // 根据配对方式验证
                bool valid = PairingMethodIndex switch
                {
                    0 => true, // 文件名模式：basename 相同即可
                    1 => await VerifyApplePairAsync(imgPath, vidPath),   // Apple: CID UUID
                    2 => VerifyVivoPair(imgPath, vidPath),              // vivo: livephoto ID
                    _ => true
                };

                if (!valid) continue;

                long imgSize = new FileInfo(imgPath).Length;
                long vidSize = new FileInfo(vidPath).Length;
                int index = Tasks.Count + newTasks.Count + 1;

                newTasks.Add(new MergeTask
                {
                    Index = index,
                    ImageFileName = Path.GetFileName(imgPath),
                    VideoFileName = Path.GetFileName(vidPath),
                    ImageSize = FileSizeFormatter.Format(imgSize),
                    VideoSize = FileSizeFormatter.Format(vidSize),
                    ImageSizeBytes = imgSize,
                    VideoSizeBytes = vidSize,
                    TotalSizeBytes = imgSize + vidSize,
                    DateTaken = GetDateTaken(imgPath),
                    BaseName = baseName,
                    ImagePath = imgPath,
                    VideoPath = vidPath,
                    Status = ProcessStatus.Pending,
                    Details = pendingText
                });
            }

            if (newTasks.Count > 0)
            {
                foreach (var t in newTasks)
                    Tasks.Add(t);
                TotalPairsCount = Tasks.Count(t => t != null);
                UpdateIsQueueEmpty(Tasks.Count);
                NotifyStatsChanged();
                LogService.Merge($"Added {newTasks.Count} file pairs to queue (total: {TotalPairsCount})");
            }
        }

        // 添加文件夹到队列（追加，不清空）
        public async Task AddFolderToQueueAsync(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            var scanResult = LivePhotoMergeScanService.Scan(folderPath, GetScanningToken());
            if (scanResult.Pairs.Count == 0) return;

            var pendingText = ResourceService.GetString("Task_Pending");
            int startIndex = Tasks.Count;

            foreach (var pair in scanResult.Pairs)
            {
                // 根据配对方式验证
                bool valid = PairingMethodIndex switch
                {
                    0 => true,
                    1 => await VerifyApplePairAsync(pair.ImagePath, pair.VideoPath),
                    2 => VerifyVivoPair(pair.ImagePath, pair.VideoPath),
                    _ => true
                };
                if (!valid) continue;

                Tasks.Add(new MergeTask
                {
                    Index = ++startIndex,
                    ImageFileName = Path.GetFileName(pair.ImagePath),
                    VideoFileName = Path.GetFileName(pair.VideoPath),
                    ImageSize = FileSizeFormatter.Format(pair.ImageSizeBytes),
                    VideoSize = FileSizeFormatter.Format(pair.VideoSizeBytes),
                    ImageSizeBytes = pair.ImageSizeBytes,
                    VideoSizeBytes = pair.VideoSizeBytes,
                    TotalSizeBytes = pair.ImageSizeBytes + pair.VideoSizeBytes,
                    DateTaken = GetDateTaken(pair.ImagePath),
                    BaseName = pair.BaseName,
                    ImagePath = pair.ImagePath,
                    VideoPath = pair.VideoPath,
                    Status = ProcessStatus.Pending,
                    Details = pendingText
                });
            }

            TotalPairsCount = Tasks.Count(t => t != null);
            UpdateIsQueueEmpty(Tasks.Count);
            NotifyStatsChanged();
            LogService.Merge($"Added folder '{folderPath}' to queue (total: {TotalPairsCount})");
        }

        // ── 配对验证辅助方法 ──

        private static async Task<bool> VerifyApplePairAsync(string imgPath, string vidPath)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return false;

                var output = await LivePhotoMetadataMatcher.MatchAsync(
                    new[] { imgPath }, new[] { vidPath }, exifToolPath, CancellationToken.None);
                return output.Pairs.Count > 0;
            }
            catch { return false; }
        }

        private static bool VerifyVivoPair(string imgPath, string vidPath)
        {
            try
            {
                var output = LivePhotoMetadataMatcher.MatchVivo(
                    new[] { imgPath }, new[] { vidPath });
                return output.Pairs.Count > 0;
            }
            catch { return false; }
        }

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
            LogService.Merge($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

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
            LogService.Merge($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={Tasks.Count}");

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
                SetStatus("Status_WarnOutput");
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
            await RunTasksAsync();
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
                    Text = ResourceService.Format("Msg_MergeCompletedSummary", total, succeeded, failed),
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
                int unprocessed = total - succeeded - failed;

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
                    Text = ResourceService.Format("Msg_MergeCancelledSummary", total, succeeded, failed, unprocessed),
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
        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            var token = GetProcessingToken();
            string outputDir = OutputDirectory;
            int modeIndex = SelectedModeIndex;
            Directory.CreateDirectory(outputDir);

            // 所有临时文件统一放在 Temp 子目录，处理完毕后整体删除
            string tempDir = Path.Combine(outputDir, "Temp");
            Directory.CreateDirectory(tempDir);

            try
            {
                await Task.Run(async () =>
                {
                    var tasksToProcess = Tasks.Where(t => t.Status != ProcessStatus.Success).ToList();

                    // 智能并行数：含 HEIC 用保守值，纯 JPG 直接拉满
                    bool hasHeic = tasksToProcess.Any(t => HeicConverterService.IsHeicFile(t.ImagePath));
                    int maxParallel = hasHeic
                        ? AppSettingsService.GetValue("MergeThreadCount", 4)
                        : 20;
                    LogService.Merge($"Parallel: {maxParallel} (hasHeic={hasHeic}, {tasksToProcess.Count} tasks)", LogLevel.Debug);

                    var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = 0;
                    var lockObj = new object();

                    async Task ProcessTask(MergeTask task)
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

                            var protocol = LivePhotoProtocol.FromIndex(modeIndex);
                            string outputName = LivePhotoMergeService.CreateOutputFileName(
                                task.BaseName, modeIndex, task.ImagePath, OutputFormatIndex, NamingRuleIndex,
                                customPattern: CustomNamingPattern,
                                taskIndex: task.Index);
                            string? mergeSubDir = null;
                            if (AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false))
                                mergeSubDir = PathHelper.GetRelativeSubDirectory(InputDirectory, task.ImagePath);

                            // 根据覆盖设置决定输出路径
                            string finalPath;
                            string targetDir = mergeSubDir != null ? Path.Combine(outputDir, mergeSubDir) : outputDir;
                            if (OverwriteExisting)
                            {
                                Directory.CreateDirectory(targetDir);
                                finalPath = Path.Combine(targetDir, outputName);
                                try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                            }
                            else
                            {
                                finalPath = PathHelper.GetUniqueFilePath(outputDir, outputName, mergeSubDir);
                            }
                            string workingImagePath = task.ImagePath;
                            string workingVideoPath = task.VideoPath;
                            var tempFiles = new System.Collections.Generic.List<string>();

                            try
                            {
                                // HEIC→JPEG 转换：用户选 HEIC 输出格式时跳过
                                bool keepHeic = (OutputFormatIndex == 2 || OutputFormatIndex == 3)
                                    && HeicConverterService.IsHeicFile(workingImagePath);
                                if (!keepHeic && HeicConverterService.IsHeicFile(workingImagePath))
                                {
                                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(
                                        workingImagePath, tempDir, token);
                                    tempFiles.Add(workingImagePath);
                                }

                                // 视频格式：用户选 MOV（索引 1/3）保留 MOV，选 MP4（索引 0/2）强制转 MP4。
                                // Samsung (4) / vivo (3) / HUAWEI (5) 始终需要 MP4。
                                // IsGoogleProtocolForceMp4 开关仅对 MOV 格式生效（覆盖为 MP4）。
                                bool wantMov = OutputFormatIndex == 1 || OutputFormatIndex == 3;
                                bool forceMp4 = !wantMov
                                    || modeIndex == 4   // Samsung 始终 MP4
                                    || modeIndex == 3   // vivo 始终 MP4
                                    || modeIndex == 5   // HUAWEI 始终 MP4（LIVE_ 尾标引用 MP4 大小）
                                    || AppSettingsService.GetValue("IsGoogleProtocolForceMp4", false);

                                // 在 ffmpeg 转码前读取源封面帧时间戳——
                                // Apple mebx 轨和 vivo uuid box 会被 ffmpeg -map 0:V:0 丢弃
                                long coverTimestampUs = LivePhotoMergeService.ReadSourceCoverTimestamp(task.VideoPath);

                                (workingVideoPath, bool vt) =
                                    await VideoTranscodeService.EnsureMp4Async(
                                        task.VideoPath, tempDir, token, forceMp4);
                                if (vt) tempFiles.Add(workingVideoPath);

                                string prepared = await protocol.PrepareImageAsync(
                                    workingImagePath, tempDir, token);
                                if (prepared != workingImagePath)
                                {
                                    workingImagePath = prepared;
                                    tempFiles.Add(workingImagePath);
                                }

                                await LivePhotoMergeService.WriteLivePhotoAsync(
                                    workingImagePath, workingVideoPath, finalPath, modeIndex, token, coverTimestampUs);

                                isSuccess = true;
                                detailMessage = ResourceService.GetString("Task_Success");
                            }
                            catch (OperationCanceledException)
                            {
                                isCanceled = true;
                                detailMessage = ResourceService.GetString("Status_Aborted") ?? "Aborted";
                            }
                            catch (Exception ex)
                            {
                                isSuccess = false;
                                detailMessage = ResourceService.Format("Task_Error", ex.Message);
                                LogService.Merge($"Merge task failed for {task.BaseName}: {ex.Message}", LogLevel.Error, ex);
                            }
                            finally
                            {
                                if (!isSuccess)
                                    try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                                foreach (var f in tempFiles)
                                    try { if (File.Exists(f)) File.Delete(f); } catch { }
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

                            // ✨ 核心修复：死等 UI 线程把状态更新完毕！
                            var tcs = new TaskCompletionSource<bool>();
                            if (App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (isCanceled)
                                        UpdateTaskCancelled(task, detailMessage);
                                    else
                                        UpdateTaskCompleted(task, isSuccess, detailMessage, currentCompleted);
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

                            // 当达到最大并发数时，等待任意一个完成
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
                                    // 取消处理 — break 出循环，后面统一 rethrow
                                    break;
                                }
                            }
                        }

                        // 等待所有剩余任务完全结束（因为内部用了 TaskCompletionSource，执行到这里时所有的 UI 也100%更新完了）
                        if (!token.IsCancellationRequested)
                        {
                            await Task.WhenAll(pendingTasks);
                        }

                        // 如果因取消而退出循环，确保异常传播到外层 catch 更新状态
                        if (token.IsCancellationRequested)
                        {
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    finally
                    {
                        // 先等所有任务退出再 dispose semaphore，避免 ProcessTask 的 finally
                        // 还在调 semaphore.Release() 时 semaphore 已被销毁 → ObjectDisposedException
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
                LogService.Merge($"Processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalPairsCount}");
                SetStatus("Status_MergeStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 非取消类异常 = 程序缺陷或系统资源耗尽。
                // 不能悄悄吞掉——否则进程退出码为 0，用户无法感知崩溃。
                LogService.Merge($"RunTasksAsync fatal error: {ex.Message}", LogLevel.Error, ex);
                Environment.ExitCode = unchecked((int)0xE0000001);
                throw;
            }
            finally
            {
                // 清理所有临时文件
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { LogService.Merge($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }

                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;
                FinalizeRunState();

                // ── 完成后处理原始文件 ──
                if (!wasCancelled)
                {
                    if (AfterCompletionActionIndex == 1) // 移动到指定目录
                    {
                        var moveDir = OriginalDirectory;
                        if (!string.IsNullOrWhiteSpace(moveDir))
                        {
                            try { Directory.CreateDirectory(moveDir); }
                            catch (Exception ex) { LogService.Merge($"Failed to create original dir: {ex.Message}", LogLevel.Warning); }
                            foreach (var task in Tasks.Where(t => t.Status == ProcessStatus.Success))
                            {
                                try { if (File.Exists(task.ImagePath)) File.Move(task.ImagePath, Path.Combine(moveDir, Path.GetFileName(task.ImagePath))); } catch { }
                                if (!string.IsNullOrEmpty(task.VideoPath))
                                    try { if (File.Exists(task.VideoPath)) File.Move(task.VideoPath, Path.Combine(moveDir, Path.GetFileName(task.VideoPath))); } catch { }
                            }
                        }
                    }
                    else if (AfterCompletionActionIndex == 2) // 回收站（暂用直接删除）
                    {
                        foreach (var task in Tasks.Where(t => t.Status == ProcessStatus.Success))
                        {
                            try { if (File.Exists(task.ImagePath)) File.Delete(task.ImagePath); } catch { }
                            if (!string.IsNullOrEmpty(task.VideoPath))
                                try { if (File.Exists(task.VideoPath)) File.Delete(task.VideoPath); } catch { }
                        }
                    }
                }

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot。
                // 多个队列同时完成时 WinUI 只允许一个 ContentDialog，
                // 冲突会抛 COMException，这里吞掉即可（不影响处理结果）。
                if (Tasks.Count > 0 && !_isCleaningUp)
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

        #endregion

        #region Task Status Events

        // 当某个合并任务开始时触发，可用于自动滚动到当前处理的任务。
        public event EventHandler<MergeTask>? TaskStartedForScroll;

        // 当所有合并任务处理完毕（全部完成或停止）时触发，可用于滚动到列表顶部。
        public event EventHandler? ProcessingCompletedForScroll;

        // 标记任务开始处理（设置为 Processing 状态）。
        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
            NotifyStatsChanged();
            TaskStartedForScroll?.Invoke(this, task);
        }

        // 标记任务被用户取消（保留 Processing 状态，颜色中性，只更新详情）。
        private void UpdateTaskCancelled(MergeTask task, string detailMessage)
        {
            // 用户取消不标记为"失败"——保留 Processing 状态，颜色中性，只更新详情
            task.Details = detailMessage;
        }

        // 更新任务完成状态（成功/失败），如果所有任务完成则触发 ProcessingCompletedForScroll 事件。
        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
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
                    FilePickerService.OpenFolderInExplorer(path);
            }
            catch (Exception ex) { LogService.Merge($"OpenMergeOriginalDir error: {ex.Message}", LogLevel.Error, ex); }
            return Task.CompletedTask;
        }

        #endregion
    }
}
