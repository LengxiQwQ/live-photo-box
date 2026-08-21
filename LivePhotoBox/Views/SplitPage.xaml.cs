/*
 * SplitPage.xaml.cs
 *
 * 实况照片拆分页面的代码后置。
 * 提供将单文件实况照片拆分为独立图片和视频文件的功能。
 * 包含任务列表自动滚动、文件夹选择、全屏预览和错误详情提示。
 *
 * 对应 ViewModel：SplitViewModel
 *
 * 生命周期：
 *   - 构造函数 → 创建 TaskListAutoScroller，注册 Loaded/Unloaded
 *   - Loaded → 附加自动滚动器，绑定 ViewModel 事件
 *   - Unloaded → 分离自动滚动器，解绑事件
 *   - 用户操作（浏览文件夹、打开文件、预览等）通过事件处理
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        // ── 面板拖拽分隔条宽度常量 ──
        private const double DefaultLeftPanelWidth = 320;

        // 任务列表自动滚动器，在处理/扫描过程中保持当前任务可见
        private readonly TaskListAutoScroller _scroller;

        // 回到顶部悬浮按钮辅助类
        private ScrollToTopButtonHelper? _scrollToTopHelper;

        // 是否已绑定 ViewModel 事件，防止重复绑定
        private bool _eventsHooked;

        // ── 拖拽状态 ──
        private bool _isDropAllFolders;
        private bool _dropHasFiles;
        private bool _isLeftDropFolder;

        // 关联的 SplitViewModel
        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        // 构造函数：初始化组件、创建自动滚动器、注册加载/卸载事件
        public SplitPage()
        {
            InitializeComponent();

            // 初始化 ToggleSwitch 状态（彻底移除开/关占位）
            OverwriteToggle.OnContent = null;
            OverwriteToggle.OffContent = null;

            _scroller = new TaskListAutoScroller(
                "Split",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += SplitPage_Loaded;
            Unloaded += SplitPage_Unloaded;
        }

        // 输出协议下拉框加载完成后注入品牌说明副标题。
        private void ProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;

            // 防止 NavigationCacheMode="Required" 导致页面缓存后 Loaded 事件重复触发
            comboBox.Loaded -= ProtocolComboBox_Loaded;

            string[] names = new string[comboBox.Items.Count];
            string[] hintKeys = ["SplitPage_Protocol_None_Hint", "SplitPage_Protocol_Apple_Hint", "SplitPage_Protocol_Vivo_Hint"];

            double maxNameWidth = 0;
            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize : 14.0;

            if (double.IsNaN(comboBox.Height))
            {
                comboBox.Height = 32;
            }

            for (int i = 0; i < comboBox.Items.Count && i < hintKeys.Length; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item)
                {
                    names[i] = (item.Content as string) ?? "";

                    var measureBlock = new TextBlock { Text = names[i], FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
                    measureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxNameWidth = Math.Max(maxNameWidth, measureBlock.DesiredSize.Width);

                    var nameBlock = new TextBlock
                    {
                        Text = names[i],
                        FontSize = fontSize,
                        FontWeight = FontWeights.Normal
                    };

                    string hint = ResourceService.GetString(hintKeys[i]);
                    var hintBlock = new TextBlock
                    {
                        Text = hint,
                        FontSize = 11,
                        Opacity = 0.65,
                        Margin = new Thickness(0, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 200,
                        Visibility = Visibility.Collapsed
                    };

                    var panel = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            comboBox.DropDownOpened += (_, _) =>
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.SemiBold;
                        hintBlock.Visibility = Visibility.Visible;
                    }
                }
            };

            void ResetToCollapsedState()
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.Normal;
                        hintBlock.Visibility = Visibility.Collapsed;
                    }
                }
            }

            comboBox.DropDownClosed += (_, _) => ResetToCollapsedState();
            comboBox.SelectionChanged += (_, _) => ResetToCollapsedState();

            // 协议切换时，联动输出格式下拉框
            comboBox.SelectionChanged += (_, _) => UpdateOutputFormatOptions(comboBox.SelectedIndex);
            UpdateOutputFormatOptions(comboBox.SelectedIndex);
        }

        // 匹配方式下拉框加载完成后注入品牌说明副标题（首项"所有单文件"，其余复用 MergePage 协议项提示）。
        private void MatchProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;
            comboBox.Loaded -= MatchProtocolComboBox_Loaded;

            string[] hintKeys =
            [
                "SplitPage_Match_All_Hint",
                "SplitPage_Match_Fusion_Hint",
                "SplitPage_Match_V1_Hint",
                "SplitPage_Match_V2_Hint",
                "SplitPage_Match_Oppo_Hint",
                "SplitPage_Match_Vivo_Hint",
                "SplitPage_Match_Samsung_Hint",
                "SplitPage_Match_Huawei_Hint",
            ];

            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize : 14.0;

            for (int i = 0; i < comboBox.Items.Count && i < hintKeys.Length; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item)
                {
                    string name = (item.Content as string) ?? "";
                    var nameBlock = new TextBlock
                    {
                        Text = name,
                        FontSize = fontSize,
                        FontWeight = FontWeights.Normal
                    };

                    string hint = ResourceService.GetString(hintKeys[i]);
                    var hintBlock = new TextBlock
                    {
                        Text = hint,
                        FontSize = 11,
                        Opacity = 0.65,
                        Margin = new Thickness(0, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 200,
                        Visibility = Visibility.Collapsed
                    };

                    var panel = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            comboBox.DropDownOpened += (_, _) =>
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.SemiBold;
                        hintBlock.Visibility = Visibility.Visible;
                    }
                }
            };

            void ResetToCollapsedState()
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.Normal;
                        hintBlock.Visibility = Visibility.Collapsed;
                    }
                }
            }

            comboBox.DropDownClosed += (_, _) => ResetToCollapsedState();
            comboBox.SelectionChanged += (_, _) => ResetToCollapsedState();
        }

        // 拆分页的协议-格式可用性矩阵。
        // 协议索引: 0=无协议, 1=Apple, 2=vivo
        // 下拉项位置: 0=默认原样, 1=HEIC+MOV, 2=JPG+MOV, 3=JPG+MP4
        // 全局 formatIndex 仍为 0=默认原样 / 1=JPG+MOV / 2=HEIC+MOV / 3=JPG+MP4（下方映射转换）
        private static readonly bool[][] SplitFormatMap =
        [
            [true,  true,  true,  true ],  // 无协议：全部可用
            [false, true,  true,  false],  // Apple：HEIC+MOV / JPG+MOV
            [false, false, false, true ],  // vivo：JPG+MP4
        ];

        // 下拉项位置 → 全局 formatIndex
        private static int VisualFormatIndexToSemantic(int visualIndex) => visualIndex switch
        {
            0 => 0,  // 默认原样
            1 => 2,  // HEIC+MOV
            2 => 1,  // JPG+MOV
            3 => 3,  // JPG+MP4
            _ => 0,
        };

        // 全局 formatIndex → 下拉项位置
        private static int SemanticFormatIndexToVisual(int semanticIndex) => semanticIndex switch
        {
            0 => 0,
            1 => 2,  // JPG+MOV
            2 => 1,  // HEIC+MOV
            3 => 3,
            _ => 0,
        };

        // 将持久化的全局 formatIndex 还原到下拉位置，并按当前协议刷新可见性。
        private void SyncOutputFormatSelection()
        {
            if (OutputFormatComboBox == null || ProtocolComboBox == null) return;

            OutputFormatComboBox.SelectedIndex = SemanticFormatIndexToVisual(ViewModel.OutputFormatIndex);
            UpdateOutputFormatOptions(ProtocolComboBox.SelectedIndex);
        }

        // 根据选中的协议切换导出格式下拉框中各项的可见性
        private void UpdateOutputFormatOptions(int protocolIndex)
        {
            if (OutputFormatComboBox == null) return;
            if (protocolIndex < 0 || protocolIndex >= SplitFormatMap.Length) return;

            var available = SplitFormatMap[protocolIndex];
            int newSelected = OutputFormatComboBox.SelectedIndex;

            for (int i = 0; i < OutputFormatComboBox.Items.Count && i < available.Length; i++)
            {
                if (OutputFormatComboBox.Items[i] is ComboBoxItem item)
                {
                    item.Visibility = available[i] ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // 如果当前选中项在新协议下不可用，自动切到第一个可用项
            if (newSelected < 0 || newSelected >= available.Length || !available[newSelected])
            {
                for (int i = 0; i < available.Length; i++)
                {
                    if (available[i])
                    {
                        OutputFormatComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        // 输出格式下拉框加载完成后注入兼容性说明副标题
        private void OutputFormatComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;
            comboBox.Loaded -= OutputFormatComboBox_Loaded;

            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize : 14.0;

            string[] hintKeys =
            [
                "SplitPage_FormatHint_Default",
                "SplitPage_FormatHint_HeicMov",
                "SplitPage_FormatHint_JpgMov",
                "SplitPage_FormatHint_JpgMp4",
            ];

            for (int i = 0; i < comboBox.Items.Count && i < hintKeys.Length; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item)
                {
                    string name = (item.Content as string) ?? "";
                    var nameBlock = new TextBlock
                    {
                        Text = name,
                        FontSize = fontSize,
                        FontWeight = FontWeights.Normal
                    };

                    string hint = ResourceService.GetString(hintKeys[i]);
                    var hintBlock = new TextBlock
                    {
                        Text = hint,
                        FontSize = 11,
                        Opacity = 0.65,
                        Margin = new Thickness(0, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 180,
                        Visibility = Visibility.Collapsed
                    };

                    var panel = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            comboBox.DropDownOpened += (_, _) =>
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.SemiBold;
                        hintBlock.Visibility = Visibility.Visible;
                    }
                }
            };

            void ResetToCollapsedState()
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.Normal;
                        hintBlock.Visibility = Visibility.Collapsed;
                    }
                }
            }

            comboBox.DropDownClosed += (_, _) => ResetToCollapsedState();
            comboBox.SelectionChanged += (_, _) => ResetToCollapsedState();

            // 将用户选中的下拉位置同步回 ViewModel（全局 formatIndex）
            comboBox.SelectionChanged += (_, _) =>
            {
                if (comboBox.SelectedIndex >= 0)
                    ViewModel.OutputFormatIndex = VisualFormatIndexToSemantic(comboBox.SelectedIndex);
            };

            SyncOutputFormatSelection();
        }

        // 页面加载完成后附加自动滚动器，绑定 ViewModel 事件
        private void SplitPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(SplitTaskListView);

            _scrollToTopHelper ??= new ScrollToTopButtonHelper(SplitTaskListView, ScrollToTopButton);
            _scrollToTopHelper.Attach();

            AttachDragEvents();

            LeftPanelScrollViewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Visible;

            // 英文模式下"清除"和"默认"按钮只显示图标，隐藏文字
            if (!LivePhotoBox.Services.LanguageService.IsChineseUi())
            {
                NamingClearBtnText.Visibility = Visibility.Collapsed;
                NamingResetBtnText.Visibility = Visibility.Collapsed;
            }
            else
            {
                NamingClearBtnText.Visibility = Visibility.Visible;
                NamingResetBtnText.Visibility = Visibility.Visible;
            }

            SyncOutputFormatSelection();

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;

            // 恢复上次的自定义命名片段
            ViewModel.LoadSegmentsFromTemplate();

            // 恢复上次拖拽的左侧面板宽度
            RestoreLeftPanelWidth();
        }

        // 页面卸载时分离自动滚动器，解绑 ViewModel 事件
        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scrollToTopHelper?.Detach();

            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            DetachDragEvents();

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        // 任务开始处理时通知自动滚动器定位
        private void OnTaskStarted(object? sender, SplitTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        // 所有任务处理完成时通知自动滚动器
        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        // 响应 ViewModel 属性变更，通知自动滚动器状态变化
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning))
            {
                if (ViewModel.IsScanning)
                    _scroller.NotifyScanStarting();
                else
                    _scroller.NotifyScanFinished();
            }
            else if (e.PropertyName == nameof(ViewModel.IsProcessing) && ViewModel.IsProcessing)
            {
                _scroller.NotifyProcessingStarting();
            }
            else if (e.PropertyName == nameof(ViewModel.IsPaused) && !ViewModel.IsPaused)
            {
                _scroller.NotifyProcessingResumed();
            }
        }

        // ── 文件操作 ──────────────────────────────────

        // 浏览输入目录：选文件夹 → 设置 InputDirectory → 自动触发替换扫描
        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null) ViewModel.InputDirectory = folder.Path;
            }
            finally { btn.IsEnabled = true; }
        }

        // 浏览输出目录按钮点击：选择文件夹并更新 ViewModel。
        // 同时自动填充原始文件移动目录默认值。
        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null)
                {
                    ViewModel.OutputDirectory = folder.Path;
                    ViewModel.AutoFillOriginalDirectory();
                }
            }
            finally { btn.IsEnabled = true; }
        }

        // "添加文件"：多选图片文件 → 追加到队列
        // 自动设置输出目录为第一个文件所在目录下的子文件夹（与拖拽行为一致）
        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".heic");
                picker.FileTypeFilter.Add(".heif");
                var files = await picker.PickMultipleFilesAsync();
                if (files.Count > 0)
                {
                    var paths = files.Select(f => f.Path).ToList();
                    var wasEmpty = ViewModel.Tasks.Count == 0;
                    await ViewModel.AddFilesToQueueAsync(paths);

                    // 队列从空变为有内容时，自动设置输出目录为第一个文件所在目录下的子文件夹
                    if (wasEmpty && ViewModel.Tasks.Count > 0)
                    {
                        var firstFileDir = Path.GetDirectoryName(paths[0]);
                        if (!string.IsNullOrEmpty(firstFileDir))
                        {
                            ViewModel.OutputDirectory = Path.Combine(
                                firstFileDir,
                                ResourceService.GetString("OutputDir_SplitPhotos"));
                        }
                    }
                }
            }
            finally { btn.IsEnabled = true; }
        }

        // 开关切换时更新右侧状态文字（已通过 x:Bind 绑定到 ViewModel，此处仅作兜底）。
        private void Toggle_Toggled(object sender, RoutedEventArgs e)
        {
        }

        // 点击开关行（标签 + 状态文字）切换开关
        private void ToggleRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is ToggleSwitch toggle)
                    {
                        toggle.IsOn = !toggle.IsOn;
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // 浏览原始文件存放目录
        private async void BrowseOriginalDir_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null)
                {
                    ViewModel.MarkOriginalDirectoryUserSet();
                    ViewModel.OriginalDirectory = folder.Path;
                }
            }
            finally { btn.IsEnabled = true; }
        }

        // 文件操作按钮点击：在资源管理器中打开文件所在位置
        private void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"SplitPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        // ════════════════════════════════════════════════════════════
        //  拖拽文件夹导入（Drag & Drop）
        // ════════════════════════════════════════════════════════════

        private void AttachDragEvents()
        {
            LeftConfigPanel.DragEnter += LeftPanel_DragEnter;
            LeftConfigPanel.DragOver += LeftPanel_DragOver;
            LeftConfigPanel.DragLeave += LeftPanel_DragLeave;
            LeftConfigPanel.Drop += LeftPanel_Drop;
            SplitTaskListSurface.DragEnter += TaskList_DragEnter;
            SplitTaskListSurface.DragOver += TaskList_DragOver;
            SplitTaskListSurface.DragLeave += TaskList_DragLeave;
            SplitTaskListSurface.Drop += TaskList_Drop;
        }

        private void DetachDragEvents()
        {
            LeftConfigPanel.DragEnter -= LeftPanel_DragEnter;
            LeftConfigPanel.DragOver -= LeftPanel_DragOver;
            LeftConfigPanel.DragLeave -= LeftPanel_DragLeave;
            LeftConfigPanel.Drop -= LeftPanel_Drop;
            SplitTaskListSurface.DragEnter -= TaskList_DragEnter;
            SplitTaskListSurface.DragOver -= TaskList_DragOver;
            SplitTaskListSurface.DragLeave -= TaskList_DragLeave;
            SplitTaskListSurface.Drop -= TaskList_Drop;
        }

        // ── 左侧面板拖拽（仅接受文件夹 → 替换源目录） ──

        private async void LeftPanel_DragEnter(object sender, DragEventArgs e)
        {
            _isLeftDropFolder = false;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    _isLeftDropFolder = items.Count > 0
                        && items.All(i => i is StorageFolder);
                }
                catch { _isLeftDropFolder = false; }
                finally { deferral.Complete(); }
            }
        }

        private void LeftPanel_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (_isLeftDropFolder)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = false;
                LeftDragOverlay.Visibility = Visibility.Visible;
                LeftDragOverlayText.Text = ResourceService.GetString("SplitPage_DropFolderToReplace");
            }

            e.Handled = true;
        }

        private void LeftPanel_DragLeave(object sender, DragEventArgs e)
        {
            LeftDragOverlay.Visibility = Visibility.Collapsed;
            _isLeftDropFolder = false;
            Bindings.Update();
            e.Handled = true;
        }

        private async void LeftPanel_Drop(object sender, DragEventArgs e)
        {
            try
            {
                LeftDragOverlay.Visibility = Visibility.Collapsed;

                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;

                var items = await e.DataView.GetStorageItemsAsync();
                var folder = items.OfType<StorageFolder>().FirstOrDefault();
                if (folder != null && !string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                {
                    ViewModel.InputDirectory = folder.Path;
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"Left panel drop error: {ex.Message}", LogLevel.Error, ex);
            }
        }

        /// <summary>拖入时异步检测内容类型</summary>
        private async void TaskList_DragEnter(object sender, DragEventArgs e)
        {
            _isDropAllFolders = false;
            _dropHasFiles = false;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    _isDropAllFolders = items.Count > 0
                        && items.All(i => i is StorageFolder);
                    _dropHasFiles = items.Any(i => i is StorageFile);
                }
                catch { _isDropAllFolders = false; _dropHasFiles = false; }
                finally { deferral.Complete(); }
            }
        }

        private void TaskList_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (_isDropAllFolders || _dropHasFiles)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = false;
                DragOverlay.Visibility = Visibility.Visible;
                EmptyQueueHint.Visibility = Visibility.Collapsed;
                RightDragOverlayText.Text = _isDropAllFolders && !_dropHasFiles
                    ? ResourceService.GetString("SplitPage_DropFolderToAppend")
                    : ResourceService.GetString("SplitPage_DropFileToAppend");
            }

            e.Handled = true;
        }

        private void TaskList_DragLeave(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            EmptyQueueHint.ClearValue(UIElement.VisibilityProperty);
            Bindings.Update();
            _isDropAllFolders = false;
            _dropHasFiles = false;
            e.Handled = true;
        }

        /// <summary>拖拽释放：文件夹→追加扫描，文件→追加配对</summary>
        private async void TaskList_Drop(object sender, DragEventArgs e)
        {
            try
            {
                DragOverlay.Visibility = Visibility.Collapsed;
                EmptyQueueHint.ClearValue(UIElement.VisibilityProperty);
                Bindings.Update();

                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;

                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;

                var folders = items.OfType<StorageFolder>().ToList();
                var files = items.OfType<StorageFile>().ToList();

                if (folders.Count > 0)
                {
                    foreach (var folder in folders)
                    {
                        if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                            await ViewModel.AddFolderToQueueAsync(folder.Path);
                    }
                }

                if (files.Count > 0)
                {
                    var wasEmpty = ViewModel.Tasks.Count == 0;
                    var paths = files.Select(f => f.Path).ToList();
                    await ViewModel.AddFilesToQueueAsync(paths);

                    if (wasEmpty && ViewModel.Tasks.Count > 0)
                    {
                        var firstFileDir = Path.GetDirectoryName(paths[0]);
                        if (!string.IsNullOrEmpty(firstFileDir))
                        {
                            ViewModel.OutputDirectory = Path.Combine(
                                firstFileDir,
                                ResourceService.GetString("OutputDir_SplitPhotos"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"Drop CRASH: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);
            }
        }

        // 删除按钮：从队列移除当前任务
        private void DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: SplitTask task }) return;
            ViewModel.RemoveTask(task);
        }

        // Flyout: 在文件夹中查看
        private void Flyout_ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            string? path = (sender as MenuFlyoutItem)?.DataContext is SplitTask task
                ? task.SourcePath : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"SplitPage reveal failed: {ex.Message}", LogSource.UI); }
        }

        // Flyout: 全屏预览
        private void Flyout_Preview_Click(object sender, RoutedEventArgs e)
        {
            string? path = (sender as MenuFlyoutItem)?.DataContext is SplitTask task
                ? task.SourcePath : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            var items = LightboxItemSource.FromSplitTasks(ViewModel.Tasks);
            var paths = items.Select(i => i.ImagePath).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(items, idx);
        }

        // ── 全屏预览 ──────────────────────────────────

        // 缩略图按钮点击：在 Lightbox 中全屏预览文件
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var items = LightboxItemSource.FromSplitTasks(ViewModel.Tasks);
            var paths = items.Select(i => i.ImagePath).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(items, idx);
        }

        // ── 错误详情提示 ──────────────────────────────────

        // 点击状态文本显示错误详情 TeachingTip
        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not SplitTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        // 错误详情提示关闭时清除目标引用
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        // 跳转到拆分相关设置
        private void GoToSplitSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Split");
        }

        // 排序下拉框：自适应宽度
        private void QueueSortCombo_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
            {
                comboBox.Loaded -= QueueSortCombo_Loaded;
                ComboBoxHelper.AutoFitWidth(comboBox);
            }
        }

        // 队列筛选菜单：点击设置过滤状态
        private void QueueFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem { Tag: string tag }) return;
            ViewModel.FilterStatus = tag switch
            {
                "Pending" => ProcessStatus.Pending,
                "Success" => ProcessStatus.Success,
                "Failed" => ProcessStatus.Failed,
                _ => null
            };
        }

        private static FontIcon CreateCheckIcon() => new() { Glyph = "", FontSize = 6 };

        // 展开筛选菜单时：同步选中状态（✓ 图标）+ 统一宽度边距
        private void QueueFilterFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout flyout) return;

            double maxTextWidth = 0;
            var measureTb = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.NoWrap };
            foreach (var item in flyout.Items)
            {
                if (item is not MenuFlyoutItem mi) continue;
                measureTb.Text = mi.Text;
                measureTb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                maxTextWidth = Math.Max(maxTextWidth, measureTb.DesiredSize.Width);
            }

            var currentStatus = ViewModel.FilterStatus;
            foreach (var item in flyout.Items)
            {
                if (item is not MenuFlyoutItem mi || mi.Tag is not string tag) continue;
                mi.MinWidth = maxTextWidth + 76;
                mi.Padding = new Thickness(14, 10, 14, 10);
                mi.MinHeight = 40;

                var itemStatus = tag switch
                {
                    "Pending" => (ProcessStatus?)ProcessStatus.Pending,
                    "Success" => (ProcessStatus?)ProcessStatus.Success,
                    "Failed" => (ProcessStatus?)ProcessStatus.Failed,
                    _ => null
                };
                mi.Icon = itemStatus == currentStatus ? CreateCheckIcon() : null;
            }
        }

        // ── 自定义命名模板事件 ──────────────────────────────────

        // 添加命名片段：根据 Tag 创建对应类型的 NamingSegment 并加入列表。
        private async void NamingAddSegment_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanEditSelectedMode) return;
            if (sender is not MenuFlyoutItem { Tag: string tag }) return;

            NamingSegmentType type = tag switch
            {
                "OriginalName" => NamingSegmentType.OriginalName,
                "Date" => NamingSegmentType.Date,
                "Time" => NamingSegmentType.Time,
                "ExifDate" => NamingSegmentType.ExifDate,
                "ExifTime" => NamingSegmentType.ExifTime,
                "Counter" => NamingSegmentType.Counter,
                "Literal" => NamingSegmentType.Literal,
                _ => NamingSegmentType.OriginalName,
            };

            string format = type switch
            {
                NamingSegmentType.Date => "yyyyMMdd",
                NamingSegmentType.Time => "HHmmss",
                NamingSegmentType.ExifDate => "yyyyMMdd",
                NamingSegmentType.ExifTime => "HHmmss",
                NamingSegmentType.Counter => "D3",
                NamingSegmentType.Literal => await PromptForLiteralAsync(),
                _ => "",
            };

            if (type == NamingSegmentType.Literal && string.IsNullOrEmpty(format))
                return;

            ViewModel.NamingSegments.Add(new NamingSegment(type, format));
            ViewModel.SyncSegmentsToTemplate();

            LeftPanelScrollViewer.ChangeView(null, LeftPanelScrollViewer.ScrollableHeight, null);
        }

        // 弹出文字输入对话框（用于 Literal 类型的 segment），底部附带 token 参考。
        private async Task<string> PromptForLiteralAsync()
        {
            var textBox = new TextBox
            {
                PlaceholderText = ResourceService.GetString("SplitPage_NamingTemplate_AddLiteral_Placeholder"),
                FontSize = 14,
                MinHeight = 32,
            };

            var tokenHelp = new TextBlock
            {
                Text = ResourceService.GetString("SplitPage_NamingTemplate_HelpText"),
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0),
            };

            var panel = new StackPanel { Children = { textBox, tokenHelp } };

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("SplitPage_NamingTemplate_AddLiteral_Title"),
                Content = panel,
                PrimaryButtonText = ResourceService.GetString("Msg_Confirm"),
                CloseButtonText = ResourceService.GetString("Msg_Cancel"),
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                return textBox.Text?.Trim() ?? "";

            return "";
        }

        // 拖拽排序完成后同步模板
        private void NamingSegmentList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.SyncSegmentsToTemplate();
        }

        // 删除命名片段。
        private void NamingSegmentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanEditSelectedMode) return;
            if (sender is not Button { Tag: NamingSegment segment }) return;
            ViewModel.NamingSegments.Remove(segment);
            ViewModel.SyncSegmentsToTemplate();
        }

        // 预设模板映射（Tag 标识 → 模板字符串）。拆分页不使用 {protocol} 后缀。
        private static readonly Dictionary<string, string> PresetTemplates = new()
        {
            ["Default"]   = "{name}",
            ["LivePhoto"] = "LivePhoto{date}{counter:D3}",
            ["Timestamp"] = "{date}_{time}_{counter:D3}",
            ["Full"]      = "{name}_{date}_{time}",
        };

        // 一键填充预设模板。
        private void NamingPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanEditSelectedMode) return;
            if (sender is not MenuFlyoutItem { Tag: string key }) return;
            if (!PresetTemplates.TryGetValue(key, out var template)) return;
            ViewModel.CustomNamingPattern = template;
            ViewModel.LoadSegmentsFromTemplate();
        }

        // 清空所有命名片段。
        private void NamingClear_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanEditSelectedMode) return;
            ViewModel.NamingSegments.Clear();
            ViewModel.SyncSegmentsToTemplate();
        }

        // 重置为默认模板。
        private void NamingReset_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanEditSelectedMode) return;
            ViewModel.CustomNamingPattern = "{name}";
            ViewModel.LoadSegmentsFromTemplate();
        }

        // ── 拖拽分隔条（GridSplitter） ──
        private const double _MinLeftWidth = 224;
        private const double _DesiredRightWidth = 420;
        private const string _LeftPanelRatioKey = "SplitPage_LeftPanelRatio";

        private double _defaultRatio;             // 初始比例 = 320/(总宽-4)，Loaded 时计算
        private bool _isSplitterDragging;
        private double _splitterAnchorX;          // 按下时鼠标在父 Grid 中的 X
        private double _splitterAnchorWidth;      // 按下时左栏的实际宽度

        // 分隔条按下：记录锚点（鼠标 X + 左栏宽度）
        private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isSplitterDragging = true;
            var parentGrid = (Grid)LeftConfigPanel.Parent!;
            var point = e.GetCurrentPoint(parentGrid);
            _splitterAnchorX = point.Position.X;
            _splitterAnchorWidth = LeftConfigPanel.ActualWidth;
            GridSplitterBar.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        // 分隔条拖动：鼠标的移动量原样加到左栏宽度上（不直接以鼠标 X 为准，避免像素错位）
        private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSplitterDragging) return;
            if (LeftConfigPanel.Parent is not Grid parentGrid) return;
            var point = e.GetCurrentPoint(parentGrid);
            var newWidth = _splitterAnchorWidth + (point.Position.X - _splitterAnchorX);
            newWidth = Math.Clamp(newWidth, _MinLeftWidth, Math.Max(_MinLeftWidth, parentGrid.ActualWidth - _DesiredRightWidth));
            parentGrid.ColumnDefinitions[0].Width = new GridLength(newWidth);
            e.Handled = true;
        }

        // 分隔条释放：停止拖动并记录本次的比例
        private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isSplitterDragging = false;
            GridSplitterBar.ReleasePointerCapture(e.Pointer);
            SaveLeftPanelRatio();
            e.Handled = true;
        }

        private void Splitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isSplitterDragging = false;
        }

        // 鼠标进入分隔条 → 显示左右拖拽光标
        private void Splitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }

        // 鼠标离开分隔条 → 恢复默认光标
        private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = null;
        }

        // 双击分隔条：重置为默认比例（320/初始容器宽）
        private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (LeftConfigPanel.Parent is Grid parentGrid)
            {
                ApplyLeftRatio(parentGrid, _defaultRatio);
                SaveLeftPanelRatio();
            }
            e.Handled = true;
        }

        // 恢复上次保存的左侧面板比例；无保存时按 320/(总宽-4) 计算初始比例
        private void RestoreLeftPanelWidth()
        {
            if (LeftConfigPanel.Parent is not Grid parentGrid) return;
            _defaultRatio = 320.0 / (parentGrid.ActualWidth - 4);
            double ratio = Services.AppSettingsService.GetValue(_LeftPanelRatioKey, _defaultRatio);
            ApplyLeftRatio(parentGrid, ratio);
        }

        // 按比例设置左栏（限制：右栏至少保留 _DesiredRightWidth 像素）
        private void ApplyLeftRatio(Grid parentGrid, double ratio)
        {
            double total = parentGrid.ActualWidth - 4;
            if (total <= 0) return;

            double px = ratio * total;
            px = Math.Clamp(px, _MinLeftWidth, Math.Max(_MinLeftWidth, total - _DesiredRightWidth));
            parentGrid.ColumnDefinitions[0].Width = new GridLength(px);
        }

        // 保存当前左栏宽度对应的比例
        private void SaveLeftPanelRatio()
        {
            if (LeftConfigPanel.Parent is not Grid parentGrid) return;
            double px = LeftConfigPanel.ActualWidth;
            double total = parentGrid.ActualWidth - 4;
            if (px <= 0 || total <= 0) return;
            double ratio = Math.Clamp(px / total, 0.15, 0.85);
            Services.AppSettingsService.SetValue(_LeftPanelRatioKey, Math.Round(ratio, 4));
        }

        // 窗口大小变化：按保存的比例（或默认比例）重算左栏宽度，不覆写保存值
        private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid parentGrid) return;
            if (_isSplitterDragging) return;

            if (_defaultRatio <= 0)
                _defaultRatio = 320.0 / (parentGrid.ActualWidth - 4);
            double ratio = Services.AppSettingsService.GetValue(_LeftPanelRatioKey, _defaultRatio);
            ApplyLeftRatio(parentGrid, ratio);
        }
        // ── 拖拽分隔条结束 ──

        // ── 拆分引导教程 ──────────────────────────────────

        // 教学步骤定义：目标控件名 + 标题 + 说明文字（全部走资源文件多语言）
        private struct TutorialStep
        {
            public string? TargetName;                 // 目标控件 x:Name（延迟解析，防止引用前未初始化）
            public string? TitleKey;                   // 标题资源 key
            public string? ContentKey;                 // 正文资源 key
            public TeachingTipPlacementMode Placement; // 弹窗位置
            public bool IsLast;
            public bool UseTip2;                       // 是否使用独立气泡（锚定在分隔条列）
        }

        // 当前引导所处的步骤索引（-1 表示未在引导中）
        private int _tutorialIndex = -1;
        // 等待关闭动画结束后要打开的下一步索引（-1 表示无待开启步骤）
        private int _tutorialPendingIndex = -1;
        private readonly List<TutorialStep> _tutorialSteps = new();

        // 点击顶栏帮助按钮：启动拆分页的引导教程（不再跳转到主页）
        private void HelpBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TutorialTip.IsOpen) { TutorialTip.IsOpen = false; return; }
            ShowTutorialStep(0);
        }

        // 显示指定步骤的 TeachingTip；每步都有「下一步」和「关闭」，最后一步只有「完成」
        private void ShowTutorialStep(int index)
        {
            if (_tutorialSteps.Count == 0) BuildTutorialSteps();
            if (index < 0 || index >= _tutorialSteps.Count) return;

            _tutorialIndex = index;
            var step = _tutorialSteps[index];

            // 第 2 步（拖拽与扫描）用独立的气泡，锚定在分隔条列，从中间弹出
            if (step.UseTip2)
            {
                ShowTutorialStep2();
                return;
            }

            // 延迟解析目标控件：防止在页面加载前引用未初始化的控件
            TutorialTip.Target = ResolveTarget(step.TargetName);

            // 标题带 emoji + 计数：📁 设置输入输出目录（1/7）
            int total = _tutorialSteps.Count;
            TutorialTip.Title = step.TitleKey != null
                ? $"{ResourceService.GetString(step.TitleKey)}（{index + 1}/{total}）"
                : "";

            // 正文：按 **粗体** 标记拆分段落
            var content = step.ContentKey != null ? ResourceService.GetString(step.ContentKey) : "";
            BuildTutorialContent(content);

            // 每个步骤使用独立的弹出位置
            TutorialTip.PreferredPlacement = step.Placement;

            // 最后一步只有一个「完成」按钮，隐藏「下一步」
            TutorialTip.ActionButtonContent = step.IsLast ? null : ResourceService.GetString("SplitTutorial_Btn_Next");
            TutorialTip.CloseButtonContent = ResourceService.GetString(step.IsLast ? "SplitTutorial_Btn_Done" : "SplitTutorial_Btn_Close");

            TutorialTip.IsOpen = true;
        }

        // 显示第 2 步（拖拽与扫描）：锚定在分隔条列的中间，无箭头
        private void ShowTutorialStep2()
        {
            _tutorialIndex = 1; // 第 2 步索引

            int total = _tutorialSteps.Count;
            TutorialTip2.Title = $"{ResourceService.GetString("SplitTutorial_Step02_Title")}（2/{total}）";
            BuildTutorialContent2(ResourceService.GetString("SplitTutorial_Step02_Content"));

            TutorialTip2.ActionButtonContent = ResourceService.GetString("SplitTutorial_Btn_Next");
            TutorialTip2.CloseButtonContent = ResourceService.GetString("SplitTutorial_Btn_Close");

            // 将气泡锚定到分隔条上方 20% 处的锚点
            TutorialTip2.Target = TutorialTip2Anchor;
            TutorialTip2.PreferredPlacement = TeachingTipPlacementMode.Bottom;

            TutorialTip2.IsOpen = true;
        }

        // 第 2 步正文渲染
        private void BuildTutorialContent2(string text)
        {
            var panel = TutorialContentPanel2;
            panel.Children.Clear();

            var paragraphs = text.Split('\n');
            foreach (var raw in paragraphs)
            {
                var para = raw.Trim();
                if (para.Length == 0) continue;

                var tb = new TextBlock
                {
                    MaxWidth = 400,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 20,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    IsTextSelectionEnabled = true,
                    // Foreground inherited from parent (theme-aware)
                };

                AppendBoldSegments(tb, para);
                panel.Children.Add(tb);
            }
        }

        // 按段落（空行分隔）拆分正文，段内支持 **粗体** 标记；
        // 每段是一个独立 TextBlock，粗体部分用 Run + FontWeight 实现
        private void BuildTutorialContent(string text)
        {
            var panel = TutorialContentPanel;
            panel.Children.Clear();

            var paragraphs = text.Split('\n');
            foreach (var raw in paragraphs)
            {
                var para = raw.Trim();
                if (para.Length == 0) continue;

                var tb = new TextBlock
                {
                    MaxWidth = 400,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 20,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    IsTextSelectionEnabled = true,
                    // Foreground inherited from parent (theme-aware)
                };

                AppendBoldSegments(tb, para);
                panel.Children.Add(tb);
            }
        }

        // 解析一段文字中的 **粗体** 标记，拆成普通/粗体 Run 追加到 TextBlock
        private static void AppendBoldSegments(TextBlock tb, string para)
        {
            int idx = 0;
            while (idx < para.Length)
            {
                int boldStart = para.IndexOf("**", idx);
                if (boldStart < 0)
                {
                    tb.Inlines.Add(new Run { Text = para[idx..] });
                    return;
                }

                if (boldStart > idx)
                    tb.Inlines.Add(new Run { Text = para[idx..boldStart] });

                int boldEnd = para.IndexOf("**", boldStart + 2);
                if (boldEnd < 0)
                {
                    // 没有配对的结束 ** → 作为普通文本
                    tb.Inlines.Add(new Run { Text = para[boldStart..] });
                    return;
                }

                tb.Inlines.Add(new Run { Text = para[(boldStart + 2)..boldEnd], FontWeight = FontWeights.SemiBold });
                idx = boldEnd + 2;
            }
        }

        // 根据名称解析目标控件（在页面加载完成后 FindName 才可用）
        private FrameworkElement? ResolveTarget(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return FindName(name) as FrameworkElement;
        }

        // 构建拆分页的引导步骤（目标控件用名称存储，显示时才解析）
        private void BuildTutorialSteps()
        {
            _tutorialSteps.Clear();

            // 第 1 步：输入输出目录（指向整个输入输出区域，从右侧弹出）
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "InputOutputSection",
                TitleKey = "SplitTutorial_Step01_Title",
                ContentKey = "SplitTutorial_Step01_Content",
                Placement = TeachingTipPlacementMode.Right,
            });

            // 第 2 步：拖拽与扫描（无箭头，锚定在分隔条列中间）
            _tutorialSteps.Add(new TutorialStep
            {
                TitleKey = "SplitTutorial_Step02_Title",
                ContentKey = "SplitTutorial_Step02_Content",
                Placement = TeachingTipPlacementMode.Auto,
                UseTip2 = true,
            });

            // 第 3 步：匹配方式（筛选协议）
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "MatchProtocolComboBox",
                TitleKey = "SplitTutorial_Step03_Title",
                ContentKey = "SplitTutorial_Step03_Content",
                Placement = TeachingTipPlacementMode.Right,
            });

            // 第 4 步：输出协议
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "ProtocolComboBox",
                TitleKey = "SplitTutorial_Step04_Title",
                ContentKey = "SplitTutorial_Step04_Content",
                Placement = TeachingTipPlacementMode.Right,
            });

            // 第 5 步：输出格式
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "OutputFormatComboBox",
                TitleKey = "SplitTutorial_Step05_Title",
                ContentKey = "SplitTutorial_Step05_Content",
                Placement = TeachingTipPlacementMode.Right,
            });

            // 第 6 步：命名格式
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "CustomNamingSection",
                TitleKey = "SplitTutorial_Step06_Title",
                ContentKey = "SplitTutorial_Step06_Content",
                Placement = TeachingTipPlacementMode.Right,
            });

            // 第 7 步：开始拆分（右上角主操作按钮；从按钮下方弹出）
            _tutorialSteps.Add(new TutorialStep
            {
                TargetName = "StartSplitButton",
                TitleKey = "SplitTutorial_Step07_Title",
                ContentKey = "SplitTutorial_Step07_Content",
                Placement = TeachingTipPlacementMode.Bottom,
                IsLast = true,
            });
        }

        // 下一步按钮：记录待跳转步骤，关闭后再打开（解决 TeachingTip 关闭动画问题）
        private void TutorialTip_ActionButtonClick(TeachingTip sender, object args)
        {
            int next = _tutorialIndex + 1;
            if (next < _tutorialSteps.Count)
            {
                _tutorialPendingIndex = next;
                TutorialTip.IsOpen = false;
            }
        }

        // 关闭/完成按钮：结束引导
        private void TutorialTip_CloseButtonClick(TeachingTip sender, object args)
        {
            _tutorialPendingIndex = -1;
            TutorialTip.IsOpen = false;
            _tutorialIndex = -1;
        }

        // 关闭动画完成后：如果有待跳转步骤，打开下一步
        private void TutorialTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            ShowTutorialPendingIfAny();
        }

        // ---- 第 2 步气泡（分隔条列锚点）导航 ----
        private void TutorialTip2_ActionButtonClick(TeachingTip sender, object args)
        {
            int next = _tutorialIndex + 1;
            if (next < _tutorialSteps.Count)
            {
                _tutorialPendingIndex = next;
                TutorialTip2.IsOpen = false;
            }
        }

        private void TutorialTip2_CloseButtonClick(TeachingTip sender, object args)
        {
            _tutorialPendingIndex = -1;
            TutorialTip2.IsOpen = false;
            _tutorialIndex = -1;
        }

        private void TutorialTip2_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            ShowTutorialPendingIfAny();
        }

        // 统一的下一步调度
        private void ShowTutorialPendingIfAny()
        {
            if (_tutorialPendingIndex < 0) return;
            int next = _tutorialPendingIndex;
            _tutorialPendingIndex = -1;
            ShowTutorialStep(next);
        }
    }
}
