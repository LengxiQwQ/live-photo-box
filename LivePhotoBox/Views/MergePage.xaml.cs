/*
 * MergePage.xaml.cs
 *
 * 实况照片合并页面的代码后置。
 * 提供将分离的图片+视频合并为实况照片的功能。
 * 包含任务列表自动滚动、文件夹选择、全屏预览和错误详情提示。
 *
 * 对应 ViewModel：MergeViewModel
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoBox.Views
{
    public sealed partial class MergePage : Page
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

        // 关联的 MergeViewModel
        public MergeViewModel ViewModel => AppViewModel.Instance.Merge;

        // 构造函数：初始化组件、创建自动滚动器、注册加载/卸载事件
        public MergePage()
        {
            InitializeComponent();

            // 初始化 ToggleSwitch 状态（彻底移除开/关占位）
            OverwriteToggle.OnContent = null;
            OverwriteToggle.OffContent = null;

            _scroller = new TaskListAutoScroller(
                "Merge",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += MergePage_Loaded;
            Unloaded += MergePage_Unloaded;
        }

        // 输出格式下拉框加载完成后注入品牌说明副标题，并按最长协议名称固定宽度。
        // 收起时只显示名称（单行），展开时显示名称 + 灰色品牌说明（双行，在 Popup 中不影响面板高度）。
        private void ProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;

            // 防止 NavigationCacheMode="Required" 导致页面缓存后 Loaded 事件重复触发，
            // 再次执行会导致 item.Content 已从 string 变为 StackPanel，读取为空字符串而破坏显示。
            comboBox.Loaded -= ProtocolComboBox_Loaded;

            string[] names = new string[comboBox.Items.Count];
            string[] hintKeys = ["MergePage_Protocol_Fusion_Hint", "MergePage_Protocol_V1_Hint", "MergePage_Protocol_V2_Hint", "MergePage_Protocol_Oppo_Hint", "MergePage_Protocol_Vivo_Hint", "MergePage_Protocol_Samsung_Hint", "MergePage_Protocol_Huawei_Hint"];

            double maxNameWidth = 0;
            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize : 14.0;

            // 1. 强行锁定 ComboBox 外部框整体高度为标准 32px，防止任何由于内部元素变化引发的上下“抽搐”
            if (double.IsNaN(comboBox.Height))
            {
                comboBox.Height = 32;
            }

            // 2. 初始化：为每一项永久绑定一个固定结构的 StackPanel 容器
            for (int i = 0; i < comboBox.Items.Count && i < hintKeys.Length; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item)
                {
                    names[i] = (item.Content as string) ?? "";

                    // 测量最长文本宽度以固定控件总宽
                    var measureBlock = new TextBlock { Text = names[i], FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
                    measureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxNameWidth = Math.Max(maxNameWidth, measureBlock.DesiredSize.Width);

                    // 主标题：不设 Foreground，自动从 ComboBoxItem 继承主题感知颜色
                    var nameBlock = new TextBlock
                    {
                        Text = names[i],
                        FontSize = fontSize,
                        FontWeight = FontWeights.Normal
                    };

                    // 副标题：不设 Foreground，通过 Opacity 淡化
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
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;

                    // 利用元组将内部控件的引用存进 Tag，方便事件中直接提取并秒刷属性
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            // 不设固定宽度，让 ComboBox 跟随 Grid 拉伸

            // 3. 展开时：一键将所有项切为“展开态”（主标题加粗 + 显示副标题）
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

            // 统一的重置状态方法：将所有项切回“收起态”（主标题恢复常规细体 + 隐藏副标题）
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

            // 4. 收起时（无论是正常选中收起，还是点击旁边空白处直接返回）：强制恢复常规细体
            // 此时外部显示框由于和内部共享同一个 panel 对象引用，属性一变，外部字体会瞬间同步变细！
            comboBox.DropDownClosed += (_, _) => ResetToCollapsedState();

            // 5. 选择项改变时也同步重置，双重保险
            comboBox.SelectionChanged += (_, _) => ResetToCollapsedState();

            // 6. 协议切换时，HEIC 格式项仅对 V2（index 1）可用
            comboBox.SelectionChanged += (_, _) => UpdateOutputFormatOptions(comboBox.SelectedIndex);
            UpdateOutputFormatOptions(comboBox.SelectedIndex);
        }

        // 配对方式下拉框：与拆分页协议版本一致的双栏样式（主标题 + 适配机型副标题）。
        private void PairingMethodComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;

            // 防止 NavigationCacheMode="Required" 导致页面缓存后 Loaded 事件重复触发
            comboBox.Loaded -= PairingMethodComboBox_Loaded;

            string[] names = new string[comboBox.Items.Count];
            string[] hintKeys =
            [
                "MergePage_Pairing_Filename_Hint",
                "MergePage_Pairing_Apple_Hint",
                "MergePage_Pairing_Vivo_Hint",
            ];

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

        // 每项输出格式在各个协议下的可用性。
        // 协议-格式兼容矩阵已提取到 ProtocolFormatMatrix（Core 项目）
        // 格式索引: 0=JPG_MP4, 1=JPG_MOV, 2=HEIC_MP4, 3=HEIC_MOV
        // 协议索引: 0=Fusion, 1=V1, 2=V2, 3=OPPO, 4=VIVO, 5=Samsung, 6=HUAWEI
        private static readonly bool[][] ProtocolFormatMap = ProtocolFormatMatrix.Matrix;

        // 根据选中的协议切换导出格式下拉框中各项的可见性
        private void UpdateOutputFormatOptions(int protocolIndex)
        {
            if (OutputFormatComboBox == null) return;
            if (protocolIndex < 0 || protocolIndex >= ProtocolFormatMap.Length) return;

            var available = ProtocolFormatMap[protocolIndex];
            int newSelected = OutputFormatComboBox.SelectedIndex;

            // 先更新每项可见性
            for (int i = 0; i < OutputFormatComboBox.Items.Count && i < available.Length; i++)
            {
                if (OutputFormatComboBox.Items[i] is ComboBoxItem item)
                {
                    item.Visibility = available[i] ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // 如果当前选中项在新协议下不可用，自动切到第一项（JPG MP4）
            if (newSelected >= 0 && newSelected < available.Length && !available[newSelected])
            {
                OutputFormatComboBox.SelectedIndex = 0;
            }

            // 按协议 + 格式选择语义化的提示 key
            void SetFormatHint(int formatIndex, string hintKey)
            {
                if (OutputFormatComboBox.Items.Count <= formatIndex) return;
                if (OutputFormatComboBox.Items[formatIndex] is not ComboBoxItem item) return;
                if (item.Tag is not (TextBlock _, TextBlock hint)) return;
                hint.Text = ResourceService.GetString(hintKey);
            }

            string JpegMp4Hint() => protocolIndex switch
            {
                5 => "MergePage_FormatHint_Untested",          // Samsung
                _ => "MergePage_FormatHint_BestCompat",
            };
            string JpegMovHint() => "MergePage_FormatHint_GoodCompat";
            string HeicMp4Hint() => protocolIndex switch
            {
                5 => "MergePage_FormatHint_Untested",          // Samsung
                _ => "MergePage_FormatHint_Efficient",
            };
            string HeicMovHint() => "MergePage_FormatHint_GoogleOnly";
            string HeicH265Hint() => "MergePage_FormatHint_HEVC";

            SetFormatHint(0, JpegMp4Hint());
            SetFormatHint(1, JpegMovHint());
            SetFormatHint(2, HeicMp4Hint());
            SetFormatHint(3, HeicMovHint());
            SetFormatHint(4, HeicH265Hint());
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
                "MergePage_FormatHint_BestCompat",
                "MergePage_FormatHint_GoodCompat",
                "MergePage_FormatHint_Efficient",
                "MergePage_FormatHint_GoogleOnly",
                "MergePage_FormatHint_HEVC",
            ];
            // Adding hintKeys for future format items (keep in sync with XAML ComboBoxItems)

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
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            // 展开时显示副标题
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

            // 收起时恢复
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

            // 输出格式加载完成后再同步一次协议对应的格式提示（协议加载更早，当时 OutputFormat Tag 未就绪）
            UpdateOutputFormatOptions(ProtocolComboBox.SelectedIndex);
        }


        // 页面加载完成后附加自动滚动器，绑定 ViewModel 事件
        private void MergePage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(MergeTaskListView);

            // 回到顶部悬浮按钮
            _scrollToTopHelper ??= new ScrollToTopButtonHelper(MergeTaskListView, ScrollToTopButton);
            _scrollToTopHelper.Attach();

            AttachDragEvents();

            // 左侧面板滚动条常驻（参照 EditPage ForceScrollBarsAlwaysThick）
            LeftPanelScrollViewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Visible;

            // 英文模式下"清除"和"默认"按钮只显示图标，隐藏文字（英文文本太长显示不下）
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
        private void MergePage_Unloaded(object sender, RoutedEventArgs e)
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
        private void OnTaskStarted(object? sender, MergeTask task) =>
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
        // 同时自动填充原始文件移动目录默认值：在输出目录下创建语言适配的子文件夹。
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

                    // 同步填充原始文件移动目录默认值（仅系统自动填充时生效，不覆盖用户手动填写的值）
                    ViewModel.AutoFillOriginalDirectory();
                }
            }
            finally { btn.IsEnabled = true; }
        }

        // "添加文件"：多选图片+视频文件 → 按当前配对方式验证 → 追加到队列
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
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".mov");
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
                                ResourceService.GetString("OutputDir_LivePhotos"));
                        }
                    }
                }
            }
            finally { btn.IsEnabled = true; }
        }

        // 开关切换时更新右侧状态文字（已通过 x:Bind 绑定到 ViewModel，此处仅作兜底）。
        private void Toggle_Toggled(object sender, RoutedEventArgs e)
        {
            // OverwriteStatus.Text 已通过 x:Bind 绑定到 ViewModel.OverwriteStatusText，
            // TwoWay 绑定会自动同步 ToggleSwitch.IsOn ↔ ViewModel.OverwriteExisting。
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
        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        // 文件组操作按钮点击：在资源管理器中打开文件组路径
        private void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
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
            MergeTaskListSurface.DragEnter += TaskList_DragEnter;
            MergeTaskListSurface.DragOver += TaskList_DragOver;
            MergeTaskListSurface.DragLeave += TaskList_DragLeave;
            MergeTaskListSurface.Drop += TaskList_Drop;
        }

        private void DetachDragEvents()
        {
            LeftConfigPanel.DragEnter -= LeftPanel_DragEnter;
            LeftConfigPanel.DragOver -= LeftPanel_DragOver;
            LeftConfigPanel.DragLeave -= LeftPanel_DragLeave;
            LeftConfigPanel.Drop -= LeftPanel_Drop;
            MergeTaskListSurface.DragEnter -= TaskList_DragEnter;
            MergeTaskListSurface.DragOver -= TaskList_DragOver;
            MergeTaskListSurface.DragLeave -= TaskList_DragLeave;
            MergeTaskListSurface.Drop -= TaskList_Drop;
        }

        // ════════════════════════════════════════════════════════════
        //  左侧面板拖拽（仅接受文件夹 → 替换源目录）
        // ════════════════════════════════════════════════════════════

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
                LeftDragOverlayText.Text = ResourceService.GetString("MergePage_DropFolderToReplace");
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
                LogService.Merge($"Left panel drop error: {ex.Message}", LogLevel.Error, ex);
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
                    ? ResourceService.GetString("MergePage_DropFolderToAppend")
                    : ResourceService.GetString("MergePage_DropFileToAppend");
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

                // 分离文件夹和文件
                var folders = items.OfType<StorageFolder>().ToList();
                var files = items.OfType<StorageFile>().ToList();

                // 文件夹 → 追加到队列（逐个扫描添加）
                if (folders.Count > 0)
                {
                    foreach (var folder in folders)
                    {
                        if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                            await ViewModel.AddFolderToQueueAsync(folder.Path);
                    }
                }

                // 文件 → 追加到队列
                if (files.Count > 0)
                {
                    var wasEmpty = ViewModel.Tasks.Count == 0;
                    var paths = files.Select(f => f.Path).ToList();
                    await ViewModel.AddFilesToQueueAsync(paths);

                    // 队列从空变为有内容时，自动设置输出目录为第一个文件所在目录下的子文件夹
                    if (wasEmpty && ViewModel.Tasks.Count > 0)
                    {
                        var firstFileDir = Path.GetDirectoryName(paths[0]);
                        if (!string.IsNullOrEmpty(firstFileDir))
                        {
                            ViewModel.OutputDirectory = Path.Combine(
                                firstFileDir,
                                ResourceService.GetString("OutputDir_LivePhotos"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Merge($"Drop CRASH: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);
            }
        }

        // 删除按钮：从队列移除当前任务
        private void DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: MergeTask task }) return;
            ViewModel.RemoveTask(task);
        }

        // Flyout: 在文件夹中查看
        private void Flyout_ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            string? path = (sender as MenuFlyoutItem)?.DataContext is MergeTask task
                ? task.ImagePath : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal failed: {ex.Message}", LogSource.UI); }
        }

        // Flyout: 全屏预览
        private void Flyout_Preview_Click(object sender, RoutedEventArgs e)
        {
            string? path = (sender as MenuFlyoutItem)?.DataContext is MergeTask task
                ? task.ImagePath : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            var items = LightboxItemSource.FromMergeTasks(ViewModel.Tasks);
            var paths = items.Select(i => i.ImagePath).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(items, idx);
        }

        private void MergeTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }

        // ── 全屏预览 ──────────────────────────────────

        // 缩略图按钮点击：在 Lightbox 中全屏预览文件（含 Live Photo 配对视频播放）
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var items = LightboxItemSource.FromMergeTasks(ViewModel.Tasks);
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
            if (element.DataContext is not MergeTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        // 错误详情提示关闭时清除目标引用
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        // 跳转到合并相关设置
        private void GoToMergeSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Merge");
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

            // 测量所有文本计算统一最小宽度
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
                "Protocol" => NamingSegmentType.Protocol,
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

            // Literal 输入取消则跳过
            if (type == NamingSegmentType.Literal && string.IsNullOrEmpty(format))
                return;

            ViewModel.NamingSegments.Add(new NamingSegment(type, format));
            ViewModel.SyncSegmentsToTemplate();

            // 自动滚动左侧面板到底部
            LeftPanelScrollViewer.ChangeView(null, LeftPanelScrollViewer.ScrollableHeight, null);
        }

        // 弹出文字输入对话框（用于 Literal 类型的 segment），底部附带 token 参考。
        private async Task<string> PromptForLiteralAsync()
        {
            var textBox = new TextBox
            {
                PlaceholderText = ResourceService.GetString("MergePage_NamingTemplate_AddLiteral_Placeholder"),
                FontSize = 14,
                MinHeight = 32,
            };

            var tokenHelp = new TextBlock
            {
                Text = ResourceService.GetString("MergePage_NamingTemplate_HelpText"),
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0),
            };

            var panel = new StackPanel { Children = { textBox, tokenHelp } };

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("MergePage_NamingTemplate_AddLiteral_Title"),
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

        // 拖拽排序完成后同步模板（CanReorderItems 不会自动调用 SyncSegmentsToTemplate）。
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

        // 预设模板映射（Tag 标识 → 模板字符串）。
        private static readonly Dictionary<string, string> PresetTemplates = new()
        {
            ["Default"]   = "{name}_{protocol}",
            ["LivePhoto"] = "LivePhoto{date}{counter:D3}",
            ["Timestamp"] = "{date}_{time}_{counter:D3}",
            ["Full"]      = "{name}_{date}_{time}_{protocol}",
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
        private const double _MaxLeftWidth = 900;
        private const double _DesiredRightWidth = 420;
        private const string _LeftPanelWidthKey = "MergePage_LeftPanelWidth";

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
            // 左栏最小/最大限制
            newWidth = Math.Clamp(newWidth, _MinLeftWidth, _MaxLeftWidth);
            // 右侧面板最小宽度保护（保证按钮/列标题不被挤没）
            newWidth = Math.Min(newWidth, Math.Max(0, parentGrid.ActualWidth - _DesiredRightWidth));
            parentGrid.ColumnDefinitions[0].Width = new GridLength(newWidth);
            e.Handled = true;
        }

        // 分隔条释放：停止拖动
        private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isSplitterDragging = false;
            GridSplitterBar.ReleasePointerCapture(e.Pointer);
            SaveLeftPanelWidth();
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

        // 双击分隔条：重置为默认宽度
        private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (LeftConfigPanel.Parent is Grid parentGrid)
            {
                parentGrid.ColumnDefinitions[0].Width = new GridLength(DefaultLeftPanelWidth);
                SaveLeftPanelWidth();
            }
            e.Handled = true;
        }

        // 恢复上次保存的左侧面板宽度
        private void RestoreLeftPanelWidth()
        {
            if (LeftConfigPanel.Parent is not Grid parentGrid) return;
            double saved = Services.AppSettingsService.GetValue(_LeftPanelWidthKey, DefaultLeftPanelWidth);
            saved = Math.Clamp(saved, _MinLeftWidth, _MaxLeftWidth);
            // 不超过可用宽度（保留右侧最小宽度）
            saved = Math.Min(saved, Math.Max(_MinLeftWidth, parentGrid.ActualWidth - _DesiredRightWidth));
            parentGrid.ColumnDefinitions[0].Width = new GridLength(saved);
        }

        // 保存当前左栏宽度
        private void SaveLeftPanelWidth()
        {
            double w = LeftConfigPanel.ActualWidth;
            if (w <= 0) return;
            Services.AppSettingsService.SetValue(_LeftPanelWidthKey, Math.Round(w));
        }

        // 窗口大小变化时，如果左栏超限则自动回缩
        private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid parentGrid) return;
            var col0 = parentGrid.ColumnDefinitions[0];
            var currentLeft = LeftConfigPanel.ActualWidth;
            // 如果左栏 + 右侧最小宽度 > 总宽度，回缩左栏
            var maxAllowed = Math.Max(_MinLeftWidth, parentGrid.ActualWidth - _DesiredRightWidth);
            if (currentLeft > maxAllowed && !_isSplitterDragging)
            {
                col0.Width = new GridLength(maxAllowed);
                SaveLeftPanelWidth();
            }
        }
        // ── 拖拽分隔条结束 ──
    }
}
